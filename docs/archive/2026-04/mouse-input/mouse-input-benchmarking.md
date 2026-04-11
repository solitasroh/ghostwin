# Mouse Input Benchmarking: 5개 터미널 코드베이스 줄단위 분석

> **Author**: Claude + 노수장
> **Date**: 2026-04-10
> **Status**: Complete (v0.3 — 5개 터미널 함수 본문 전수 조사)

---

## 1. 크로스 분석 (코드 근거)

### Q1: VT 인코딩 패턴

| Terminal | 방식 | 힙 할당 | 핵심 코드 |
|----------|------|:-------:|-----------|
| **ghostty** | `encode()` 순수 함수 + 스택 38B `WriteReq.Small.Array` | **0** | `Surface.zig:3631` |
| **WT** | `fmt::format(FMT_COMPILE)` stateless constexpr | **0** | `mouseInput.cpp:471` `_GenerateSGRSequence` |
| **Alacritty** | `format!` 매크로 → 스택 임시 String | **최소** | `input/mod.rs` `sgr_mouse_report` |
| **WezTerm** | `write!(self.writer)` 직접 출력 | **0** | `terminalstate/mouse.rs` |
| **cmux** | **VT 인코딩 안 함** — `ghostty_surface_mouse_*` C API 위임 | **0** | `GhosttyTerminalView.swift:7202` |

**GhostWin 시사점**: cmux와 동일하게 `ghostty_surface_mouse_*` C API를 직접 호출하는 것이 가장 자연스러운 패턴. 현재 Design의 `ghostty_mouse_encoder_*` opaque handle 방식보다 **ghostty Surface 레벨 C API 사용이 정답**.

### Q2: Motion Throttling

| Terminal | 방식 | 시간 기반 | 핵심 코드 |
|----------|------|:---------:|-----------|
| **ghostty** | 2단계: 1px 글로벌(`embedded.zig:870`) + cell 중복(`mouse_encode.zig:107`) | **없음** | `opts.last_cell` 비교 |
| **WT** | cell + button 비교 | **없음** | `mouseInput.cpp:340` `sameCoord` |
| **Alacritty** | cell + cell_side + inside_text_area | **없음** | `input/mod.rs` `old_point != point` |
| **WezTerm** | cell 비교 (SgrPixels만 pixel) | **없음** | `mouse.rs` `last_mouse_move` |
| **cmux** | **안 함** — libghostty 내부 처리 위임 | **N/A** | ghostty 내부에서 처리 |

**GhostWin 시사점**: cmux처럼 libghostty에 위임하면 GhostWin 측 throttling 불필요.

### Q3: 스레드 모델

| Terminal | 처리 위치 | Dispatcher/큐 | 핵심 코드 |
|----------|----------|:-------------:|-----------|
| **ghostty** | 콜백 동기 인코딩 → IO 큐 | 인코딩 동기, 전송만 큐 | `Surface.zig:3654` `queueIo` |
| **WT** | UI 스레드 동기 + write lock | **없음** | `TermControl.cpp` → `Terminal::SendMouseEvent` |
| **Alacritty** | winit 단일 스레드 | **없음** | `event.rs` `handle_event` |
| **WezTerm** | GUI 스레드 동기 | **없음** | `mouseevent.rs` `&mut self` |
| **cmux** | **메인 스레드 동기** | **없음** | `GhosttyTerminalView.swift` NSView handler |

**GhostWin 시사점**: 5개 전부 동기 처리. v0.1의 `Dispatcher.BeginInvoke` 제거 필수.

### Q4: 스크롤 처리

| Terminal | 패턴 | 핵심 코드 |
|----------|------|-----------|
| **ghostty** | `pending_scroll_y` 누적 → cell_height 나누기 → 나머지 보존 | `Surface.zig:3392` |
| **WT** | `accumulatedDelta` → `WHEEL_DELTA(120)` 임계값 | `mouseInput.cpp:300` |
| **Alacritty** | `accumulated_scroll.y` 픽셀 누적 → cell_height 나누기 → `%=` | `input/mod.rs` `scroll_terminal` |
| **WezTerm** | discrete 정규화 (delta=1) | `mouseevent.rs` `*delta = 1` |
| **cmux** | `ghostty_surface_mouse_scroll` + precision 2x + momentum phase | `GhosttyTerminalView.swift:8049` |

### Q5: 다중 Pane 라우팅

| Terminal | 방식 | 핵심 코드 |
|----------|------|-----------|
| **ghostty** | per-Surface 독립 콜백 | `embedded.zig` per-Surface |
| **WT** | XAML visual tree hit-test | `Pane.cpp` `_borderTappedHandler` |
| **Alacritty** | 단일 pane | N/A |
| **WezTerm** | 좌표 hit-test + `MouseCapture(PaneId)` | `mouseevent.rs` `get_panes_to_render()` |
| **cmux** | AppKit hitTest + Bonsplit focus sync | `WorkspaceContentView.swift:309` |

### Q6: cmux에서 발견된 핵심 패턴

| 패턴 | cmux 코드 | GhostWin 적용 |
|------|-----------|---------------|
| `ghostty_surface_mouse_captured` 분기 | 우클릭 시 VT 캡처면 터미널에, 아니면 앱 메뉴에 | 동일 분기 필요 |
| `clickCount == 1`일 때만 pos 업데이트 | double-click selection 간섭 방지 (issue #1698) | WPF `e.ClickCount` 체크 |
| drag out-of-bounds 좌표 그대로 전달 | libghostty가 auto-scroll 처리 | `Mouse.Capture()` 사용 |
| Y축 flip 불필요 (WPF top-left = ghostty top-left) | cmux는 `bounds.height - y` (AppKit) | WPF는 변환 불필요 |

---

## 2. 핵심 결론: GhostWin v0.2 아키텍처 결정

### 최대 발견: cmux 패턴 = 정답

cmux가 ghostty의 **Surface 레벨 C API** (`ghostty_surface_mouse_*`)를 직접 사용하는 패턴이 가장 단순하고 정확합니다.

현재 Design의 `ghostty_mouse_encoder_*` opaque handle 패턴 vs cmux의 `ghostty_surface_mouse_*` 패턴:

| 비교 | `ghostty_mouse_encoder_*` (현 Design) | `ghostty_surface_mouse_*` (cmux 패턴) |
|------|---------------------------------------|---------------------------------------|
| 힙 할당 | 매 호출 new/free (또는 per-session 캐시) | **0** (libghostty 내부 스택) |
| Cell 중복 제거 | GhostWin에서 구현 필요 | **libghostty 내부** 자동 처리 |
| 스크롤 누적 | GhostWin에서 구현 필요 | **libghostty 내부** `pending_scroll` |
| VT 인코딩 | GhostWin C++ 엔진에서 호출 | **libghostty 내부** 자동 |
| Selection | GhostWin에서 자체 구현 | **libghostty 내부** (ghostty Selection.zig) |
| 마우스 모드 쿼리 | `gw_mouse_mode` API 필요 | `ghostty_surface_mouse_captured` 1개 |
| 구현 복잡도 | C++ Engine API + C# Interop + WndProc | **WndProc → C API 직접 호출만** |

**결정: cmux 패턴 채택 (ghostty Surface C API 직접 사용)**

### 필요 조건

1. ghostty-vt.dll에 `ghostty_surface_mouse_*` 심볼이 export되는지 확인
2. GhostWin이 ghostty Surface 핸들을 보유하는지 확인 (현재 VtCore가 Terminal 핸들 보유)

---

## 3. 소스 코드 참조

| Terminal | 핵심 파일 | 조사 방법 | 함수 본문 |
|----------|----------|-----------|:---------:|
| ghostty | `Surface.zig`, `mouse_encode.zig`, `embedded.zig` | 로컬 소스 줄단위 읽기 | 전체 |
| WT | `mouseInput.cpp`, `ControlInteractivity.cpp`, `TermControl.cpp` | GitHub raw WebFetch | 전체 |
| Alacritty | `input/mod.rs`, `selection.rs`, `event.rs` | GitHub raw WebFetch | 전체 |
| WezTerm | `mouseevent.rs`, `terminalstate/mouse.rs`, `input.rs` | GitHub raw WebFetch | 전체 |
| cmux | `GhosttyTerminalView.swift`, `WorkspaceContentView.swift` | GitHub WebFetch | 전체 |

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | 2026-04-10 | CTO-lead 초안 |
| 0.2 | 2026-04-10 | 4개 터미널 코드베이스 검증 (병렬 agent 4개) |
| 0.3 | 2026-04-10 | 5개 터미널 함수 본문 전수 조사 (병렬 agent 5개). cmux 패턴 발견 |
