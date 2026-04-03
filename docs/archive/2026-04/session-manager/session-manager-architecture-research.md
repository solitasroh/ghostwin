# Session Manager 아키텍처 심층 리서치

> GhostWin Terminal Phase 5-A 설계를 위한 참조 구현 분석
> 작성일: 2026-04-03
> 분석 대상: CMUX, Alacritty, Ghostty, Windows Terminal

---

## 1. CMUX 세션 아키텍처

### 1.1 5단계 계층 구조

**[확인됨]** — 출처: cmux docs/concepts, GitHub README, PR #808

```
Window (macOS 윈도우)
└── Workspace (사이드바 항목 = "탭")
    └── Pane (Bonsplit 트리 리프)
        └── Surface (pane 내 탭)
            └── Panel (터미널 or 브라우저)
```

| 계층 | 역할 | 식별자 | 소유 |
|------|------|--------|------|
| Window | macOS 앱 윈도우 | OS 윈도우 | 1:N Workspace |
| Workspace | 독립 작업 컨텍스트 | `CMUX_WORKSPACE_ID` 환경변수 | 1:N Pane (Bonsplit 트리) |
| Pane | 분할 영역 | Pane UUID | 1:N Surface |
| Surface | 개별 세션 | `CMUX_SURFACE_ID` 환경변수 | 1:1 Panel |
| Panel | 콘텐츠 (libghostty / WKWebView) | 내부 전용 | 콘텐츠 |

**핵심 인사이트**: Pane 안에 여러 Surface가 탭처럼 존재할 수 있음. GhostWin Phase 5에서는 이 수준의 중첩은 불필요하지만, Surface lifecycle 패턴은 참고 가치가 높음.

### 1.2 Bonsplit 레이아웃 엔진

**[확인됨]** — 출처: bonsplit.alasdairmonk.com, GitHub almonk/bonsplit, cmux PR #223

**재귀적 바이너리 트리**:
```swift
enum ExternalTreeNode {
    case pane(ExternalPaneNode)     // 리프
    case split(ExternalSplitNode)   // 분기
}

struct ExternalSplitNode {
    id: UUID
    orientation: "horizontal" | "vertical"
    dividerPosition: Float          // 0.0 ~ 1.0 정규화
    first: ExternalTreeNode
    second: ExternalTreeNode
}
```

- dividerPosition은 **비율** (픽셀 아님) → 창 리사이즈 시 자연스러운 비례 조정
- `.keepAllAlive` 뷰 모드: 모든 Surface를 메모리 유지 (터미널 상태 보존 필수)
- Zoom: `Cmd+Shift+Enter`로 단일 pane 전체 확대, 새 split/tab 생성 시 자동 unzoom

### 1.3 Surface Lifecycle 상태 머신

**[확인됨]** — 출처: cmux PR #808

```
[생성] → .live (generation=1)
           │ close 시작
           ▼
        .closing (generation 증가, portal bind 거부)
           │ deinit
           ▼
        .closed (generation 재증가, 매핑 해제)
```

**generation 카운터**: surfaceId + generation 모두 일치해야 bind 허용. stale 참조 방지.
- 목적: Ctrl+D 셸 종료 시 "ghost terminal"이 잠깐 다시 나타나는 현상 방지
- 멀티스레드 환경에서 dangling reference 방지에 유용

### 1.4 세션 전환 메커니즘

**[확인됨]** — 출처: cmux issue #1789, PR #1964

- Workspace 전환 시 현재 Bonsplit 뷰 숨김, 새 뷰 활성화
- libghostty CVDisplayLink가 background에서 정지 → GPU 렌더링 중단
- 터미널 데이터(스크롤백, 셀 버퍼)는 메모리 유지
- `cmux refresh-surfaces`로 강제 redraw → 데이터 무결, 디스플레이만 정지 확인

### 1.5 사이드바 ↔ 세션 통신 (하이브리드 모델)

**[확인됨]** — 출처: cmux PR #905, #1139, issue #494

| 정보 | 방식 | 주기/트리거 |
|------|------|-------------|
| CWD | Shell hook (precmd/preexec) push | 프롬프트 표시 시 |
| Git branch | `.git/HEAD` content-signature + background probe | HEAD 변경 즉시 |
| PR 배지 | Background poll loop | **45초** + CWD/HEAD 변경 시 즉시 relaunch |
| 포트 | [추측] 이벤트/텔레메트리 기반 | 포트 open/close 시 |
| 알림 | Push (OSC 9/777/99 + CLI) | 수신 즉시 |

**PR 폴링 세부**:
- `_CMUX_PR_POLL_INTERVAL=45`초 기본
- `gh` CLI 일시 실패 시 마지막 배지 유지 (flicker 방지)
- CWD/HEAD 컨텍스트 변경 시에만 배지 클리어
- EXIT trap으로 background poller 정리

---

## 2. Alacritty 아키텍처

### 2.1 Per-Window 격리 단위

**[확인됨]** — 출처: alacritty/src/event.rs, window_context.rs

```rust
// 전체 앱 상태
struct Processor {
    windows: HashMap<WindowId, WindowContext>,  // per-window
    config: Rc<UiConfig>,                       // 공유
    clipboard: Clipboard,                        // 공유
    scheduler: Scheduler,                        // 공유
}

// Per-window 상태
struct WindowContext {
    terminal: Arc<FairMutex<Term<EventProxy>>>,  // VT 상태 (Arc+Mutex)
    display: Display,                             // GL context, renderer
    notifier: Notifier,                           // PTY I/O 채널
    mouse, search_state, modifiers,               // 입력 상태
    // + PTY I/O thread (별도 spawn)
}
```

**공유 vs Per-window**:

| 공유 | Per-window |
|------|-----------|
| Config (Rc) | Terminal (Arc<FairMutex>) |
| Clipboard | Display (GL context) |
| Scheduler | Notifier (PTY channel) |
| GL config | PTY I/O thread |

### 2.2 FairMutex 패턴

**[확인됨]** — 출처: alacritty/src/sync.rs

```rust
pub struct FairMutex<T> {
    data: Mutex<T>,      // parking_lot::Mutex
    next: Mutex<()>,     // 공정성 보장용
}
```

- `lock()`: next lock → data lock (공정 순서)
- `lease()`: next만 lock (PTY 스레드가 예약)
- `lock_unfair()`: data만 lock (fast path)

**목적**: PTY read throughput vs render latency 균형. PTY 스레드가 lease()로 예약하면 render 스레드가 끼어들지 못함.

### 2.3 2-Layer Damage Tracking

**[확인됨]** — 출처: alacritty_terminal/src/term/mod.rs, alacritty/src/display/damage.rs

| Layer | 위치 | 단위 | 용도 |
|-------|------|------|------|
| Terminal | `TermDamageState` | line-level (left/right column) | VT 파서가 셀 변경 시 자동 기록 |
| Display | `DamageTracker` (double-buffered) | pixel Rect | vi cursor, selection, visual bell 등 추가 |

렌더링 흐름:
1. `terminal.lock()` → `RenderableContent` 수집
2. `terminal.damage()` → line-level dirty 가져오기
3. `terminal.reset_damage()` → lock 해제 (최소 hold time)
4. Display-level damage 병합 → GL draw → swap

### 2.4 Thread Model (2-thread per window)

| Thread | 역할 |
|--------|------|
| Main | winit event loop, 입력 처리, **GL 렌더링** |
| PTY I/O | PTY read/write, VT 파싱, terminal state 업데이트 |

- PTY I/O: `lease()` → read → `try_lock_unfair()` → parse → 1MB 누적 시 강제 `lock_unfair()`
- Main: `AboutToWait` → `terminal.lock()` → 이벤트 처리 → `draw()`

---

## 3. Ghostty 아키텍처

### 3.1 Thread Model (3-thread per surface)

**[확인됨]** — 출처: ghostty-org/ghostty, renderer/Thread.zig, termio/Thread.zig

| Thread | 역할 |
|--------|------|
| Main | App event loop, 입력 처리 |
| Renderer | xev 기반 render loop, 120 FPS timer |
| IO | PTY read/write, VT 파싱 |

동기화:
- `renderer_state: renderer.State` → `std.Thread.Mutex`
- `renderer_wakeup: xev.Async` → IO → Renderer 깨움
- `surface_mailbox` / `renderer_mailbox` / `io_mailbox` → BlockingQueue 메시지 패싱

### 3.2 공유 리소스

- `SharedGridSet`: 폰트 그리드 캐시 (전 surface 공유)
- Config: 전역 설정

---

## 4. Windows Terminal 아키텍처 (수집 데이터 기반)

### 4.1 3-Layer 분리

| Layer | 클래스 | 역할 |
|-------|--------|------|
| App | TerminalPage | 탭 목록 관리, UI 레이아웃 |
| Control | TermControl / ControlCore | 개별 터미널 컨트롤, 렌더링 |
| Core | Terminal (shared_ptr) | VT 파서, 터미널 상태 |

### 4.2 ControlCore 구조 (per-tab)

```cpp
struct ControlCore {
    unique_ptr<AtlasEngine> _renderEngine;     // per-tab 렌더 엔진
    unique_ptr<Renderer> _renderer;             // per-tab 렌더러
    shared_ptr<Terminal> _terminal;              // per-tab VT 상태
    ITerminalConnection _connection;             // ConPTY 연결
    DispatcherQueue _dispatcher;                 // UI 스레드 디스패치
    til::shared_mutex<SharedState> _shared;      // 스레드 안전 상태
};
```

### 4.3 탭 전환

```cpp
void TerminalPage::_OnTabSelectionChanged(...) {
    // 모든 탭 Unfocus → 선택된 탭만 Focus
    auto tab = _tabs.GetAt(selectedIndex);
    _UpdatedSelectedTab(tab);
}
```

- `_removing` 플래그로 탭 제거 중 selection 이벤트 억제
- 비활성 탭도 ConPTY I/O 계속 (VT 파싱 유지)

### 4.4 Pane 트리

- `Pane::_Split()`: 바이너리 트리로 수평/수직 분할
- 각 리프 Pane이 TermControl을 소유

---

## 5. GhostWin 설계 시사점 종합

### 5.1 참조 구현 비교 매트릭스

| 항목 | CMUX | Alacritty | Ghostty | WT | **GhostWin 결정** |
|------|------|-----------|---------|----|--------------------|
| 계층 깊이 | 5 (Win→WS→Pane→Surface→Panel) | 2 (App→Window) | 2 (App→Surface) | 3 (Page→Tab→Pane) | **3 (App→Tab→Pane→Session)** |
| 세션별 스레드 | Main + Renderer(CVDisplayLink) + IO | Main + IO | Main + Renderer + IO | Main + IO + Renderer(AtlasEngine) | **Main + IO (세션당), Render 1개 공유** |
| VT 상태 격리 | Surface별 완전 격리 | Window별 완전 격리 | Surface별 완전 격리 | TermControl별 완전 격리 | **Session별 완전 격리** |
| 공유 리소스 | (macOS 네이티브) | Config, Clipboard, GL config | SharedGridSet, Config | (WinUI3 프레임워크) | **DX11Renderer, GlyphAtlas, QuadBuilder** |
| 비활성 렌더 | CVDisplayLink 정지 | GL context switch | Renderer thread 정지 | AtlasEngine per-tab | **active만 렌더, 나머지 VT parse만** |
| Mutex 패턴 | (Swift 추측) | FairMutex (lease/lock_unfair) | std.Thread.Mutex | til::shared_mutex | **세션별 독립 mutex (ADR-006 확장)** |
| Damage tracking | (libghostty 내장) | 2-layer (term + display) | (libghostty 내장) | AtlasEngine 내장 | **기존 bitset dirty_rows 유지** |
| Lifecycle | 3-state + generation | HashMap insert/remove | (불명) | Tab.Shutdown() | **3-state (live→closing→closed) + generation** |
| Pane 분할 | Bonsplit (정규화 divider) | 없음 (multi-window) | libghostty Splits | Pane::_Split (바이너리 트리) | **Phase 5-D: 바이너리 트리 + 정규화 divider** |

### 5.2 현재 Design 대비 개선 필요 사항

| # | 현재 Design | 리서치 발견 | 개선 방향 |
|---|------------|------------|-----------|
| 1 | Session 상태 = alive bool | CMUX 3-state + generation | **SessionState enum (Live/Closing/Closed) + generation 카운터** |
| 2 | close_session이 즉시 erase | CMUX portal lifecycle (bind 거부 후 정리) | **2단계 종료: begin_close → erase (stale 참조 방지)** |
| 3 | vt_mutex만 사용 | Alacritty FairMutex (lease/lock_unfair) | **FairMutex 패턴 도입 검토 (PTY throughput 보장)** |
| 4 | TsfHandle per-session | WT ControlCore에서 Detach/AttachToNewControl | **TSF Detach/Attach 패턴 명시** |
| 5 | resize_all이 모든 세션 순회 lock | WT/Alacritty는 per-tab resize | **resize는 활성 세션 우선, 비활성은 activate 시 lazy resize** |
| 6 | 이벤트 콜백 단순 function | CMUX 환경변수 (CMUX_SURFACE_ID 등) | **세션별 환경변수 주입 (Phase 6 훅 연동 대비)** |

### 5.3 Phase 5-D (pane-split) 미리보기

CMUX Bonsplit + WT Pane::_Split 분석에서:
- 정규화 divider (0.0~1.0)가 픽셀 기반보다 우월
- 바이너리 트리가 가장 일반적이고 검증된 구조
- Zoom 기능 (단일 pane 전체 확대)은 UX에서 매우 유용

---

## 참고 자료

### CMUX
- [cmux GitHub](https://github.com/manaflow-ai/cmux) — AGPL-3.0
- [cmux docs/concepts](https://cmux.com/docs/concepts)
- [Bonsplit 공식 문서](https://bonsplit.alasdairmonk.com/)
- [cmux PR #223](https://github.com/manaflow-ai/cmux/pull/223) — pane.resize via Bonsplit
- [cmux PR #634](https://github.com/manaflow-ai/cmux/pull/634) — zoom/maximize pane
- [cmux PR #808](https://github.com/manaflow-ai/cmux/pull/808) — surface rebind lifecycle
- [cmux PR #905](https://github.com/manaflow-ai/cmux/pull/905) — sidebar branch refresh
- [cmux PR #1139](https://github.com/manaflow-ai/cmux/pull/1139) — PR badge polling
- [cmux issue #1789](https://github.com/manaflow-ai/cmux/issues/1789) — blank surfaces bug

### Alacritty
- [alacritty/src/event.rs](https://github.com/alacritty/alacritty) — Processor, WindowContext
- [alacritty/src/sync.rs](https://github.com/alacritty/alacritty) — FairMutex
- [alacritty_terminal/src/term/mod.rs](https://github.com/alacritty/alacritty) — Term, TermDamageState

### Ghostty
- [ghostty-org/ghostty](https://github.com/ghostty-org/ghostty) — renderer/Thread.zig, termio/Thread.zig

### Windows Terminal
- [microsoft/terminal — ControlCore.h](https://github.com/microsoft/terminal) — per-tab 구조
- [microsoft/terminal — TabManagement.cpp](https://github.com/microsoft/terminal) — 탭 전환 패턴
