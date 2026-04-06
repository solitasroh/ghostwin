# Pane Split Design Document

> **Summary**: 단일 탭 내에서 수평/수직 분할을 지원하는 Tree\<Pane\> 레이아웃 엔진. 엔진 다중 서피스 렌더링 + WPF Grid 동적 레이아웃으로 구현.
>
> **Project**: GhostWin Terminal
> **Author**: 노수장
> **Date**: 2026-04-06
> **Status**: Draft (v0.3)
> **Planning Doc**: [multi-session-ui.plan.md](../../01-plan/features/multi-session-ui.plan.md) (FR-05)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | 현재 탭당 1개 세션만 표시. 병렬 작업(빌드+로그, 에디터+테스트)에 탭 전환이 필수. |
| **Solution** | Tree\<Pane\> 자료구조 기반 재귀 분할 + 엔진 다중 서피스 렌더링 + WPF Grid 동적 레이아웃. |
| **Function/UX Effect** | Alt+H/V로 현재 Pane 수평/수직 분할, Alt+방향키로 포커스 이동, 드래그로 경계 리사이즈. |
| **Core Value** | cmux/tmux 수준의 Pane 분할을 네이티브 DX11 성능으로 제공. Phase 5-F 세션 복원의 기반. |

---

## 1. Overview

### 1.1 Design Goals

1. **Tree\<Pane\> 레이아웃**: 재귀 이진 트리로 무한 분할 지원 (실용 상한: 8 pane)
2. **Surface 전용 렌더링**: 단일 D3D11 Device + pane별 SwapChain. Legacy render path 완전 폐기
3. **WPF MVVM 준수**: PaneLayoutService(Services) + PaneContainerControl(App) 책임 분리
4. **스레드 안전**: render thread ↔ UI thread 간 surfaces 접근에 mutex 보호

### 1.2 현재 아키텍처 제약

| 항목 | 현재 | pane-split 후 |
|------|------|---------------|
| SwapChain | 1개 (단일 HWND) | N개 (pane별 HWND) |
| 렌더 대상 | active_session() 1개만 | 모든 visible pane의 Surface |
| Resize | resize_all() 균일 | pane별 독립 cols/rows |
| TSF 포커스 | active session 1개 | focused pane 1개 |
| GlyphAtlas | 공유 1개 | 변경 없음 (공유 유지) |

### 1.3 v0.1~v0.2 구현에서 발견된 문제

| # | 문제 | 근본 원인 | v0.3 해결 |
|---|------|-----------|-----------|
| 1 | 분할 후 렌더링 안 됨 | legacy/surface 이중 render path — 전환 시 양쪽 다 무효 | Surface 전용 단일 경로 |
| 2 | PaneNode.Split() 후 host 매핑 소실 | Split()이 자식에 새 ID 부여, _hostControls[원래ID] 소멸 | Split()이 host/surface 이전 반환 |
| 3 | PaneContainerControl God Class | 6가지 책임 혼재 (트리/서피스/호스트/레이아웃/포커스/리사이즈) | 책임별 클래스 분리 |
| 4 | MVVM 위반 | UI 컨트롤이 IEngineService 직접 호출 | PaneLayoutService에서 엔진 호출 |
| 5 | 스레드 안전 없음 | surfaces 벡터를 render/UI thread 동시 접근 | std::mutex로 보호 |
| 6 | OnPaneResized 전체 순회 | 어떤 host의 이벤트인지 모름 | host에 PaneId 속성 추가 |
| 7 | retry 매직 넘버 | HWND 생성 타이밍 불확실 | TerminalHostControl.Loaded 이벤트 사용 |
| 8 | `_useSurfaces` 플래그 | 모드 전환 분기 — 상태 머신 없는 boolean | 폐기 (항상 Surface 경로) |
| 9 | 이중 Dictionary 동기화 위험 | hostControls/surfaceIds 별도 관리 | PaneLeafState 단일 레코드로 통합 |
| 10 | C++ render_surface() 캡슐화 깨짐 | context()를 꺼내서 직접 Clear/Viewport | DX11Renderer에 render_to_target() 메서드 추가 |

---

## 2. Architecture (v0.3)

### 2.1 컴포넌트 구조 및 책임

```text
GhostWin.Core
  ├── Models/PaneNode.cs             ← 순수 트리 자료구조 (UI/엔진 무관)
  ├── Models/SplitOrientation.cs     ← enum (Horizontal, Vertical)
  └── Interfaces/IPaneLayoutService.cs ← Split/Close/MoveFocus 인터페이스

GhostWin.Services
  └── PaneLayoutService.cs           ← 트리 조작 + 엔진 Surface 생명주기
                                        (IEngineService, ISessionManager 주입)

GhostWin.App
  ├── Controls/PaneContainerControl.cs ← Grid/GridSplitter 빌드만 (렌더링 무관)
  ├── Controls/TerminalHostControl.cs  ← HwndHost (기존) + PaneId 속성 추가
  └── ViewModels/MainWindowViewModel.cs ← Split/Close 커맨드 → PaneLayoutService 위임
```

### 2.2 책임 분리 원칙

| 클래스 | 단일 책임 | 하지 않는 것 |
|--------|----------|-------------|
| **PaneNode** | 트리 구조 (split/remove/traverse) | 엔진 호출, UI 조작, Surface 관리 |
| **PaneLayoutService** | 트리 조작 + Surface/Session 생명주기 조율 | UI 레이아웃 빌드, WPF 의존 |
| **PaneContainerControl** | PaneNode 트리 → WPF Grid 변환 | 엔진 호출, 세션 관리, Surface 관리 |
| **MainWindowViewModel** | 키 커맨드 → Service 위임 | 직접 Split 로직, 직접 엔진 호출 |

### 2.3 의존성 다이어그램

```text
MainWindowViewModel (App)
  ↓ uses
IPaneLayoutService (Core)
  ↓ impl
PaneLayoutService (Services)
  ├── IEngineService   ← Surface create/destroy/resize/focus
  ├── ISessionManager  ← Session create/close
  └── PaneNode         ← 트리 조작
  ↓ event
PaneLayoutChanged (WeakReferenceMessenger)
  ↓ receives
PaneContainerControl (App)  ← Grid 재구성만 담당
```

### 2.4 데이터 흐름

```text
사용자 Alt+V (수직 분할)
  → MainWindowViewModel.SplitVerticalCommand
    → IPaneLayoutService.SplitFocused(Vertical)
      → ISessionManager.CreateSession()         ← 새 세션
      → PaneNode.Split()                        ← 트리 분할
      → (UI에서 새 HWND 생성 대기)
      → IEngineService.SurfaceCreate(newHwnd)   ← 새 SwapChain
    ← PaneLayoutChanged 메시지
  → PaneContainerControl.OnLayoutChanged()
    → BuildGrid(paneNode)                       ← Grid 재구성
```

---

## 3. Detailed Design

### 3.1 PaneNode (Core — 순수 트리)

```csharp
public class PaneNode
{
    public uint Id { get; init; }
    public uint? SessionId { get; set; }
    public SplitOrientation? SplitDirection { get; set; }
    public PaneNode? Left { get; set; }
    public PaneNode? Right { get; set; }
    public double Ratio { get; set; } = 0.5;

    public bool IsLeaf => Left == null && Right == null;

    // ID 생성은 외부 주입 (팩토리 or Service)
    // SurfaceId는 PaneNode에 두지 않음 — 렌더링은 Service의 관심사
}
```

**v0.1과 차이점**:
- `static uint _nextId` 제거 → ID 생성을 Service에 위임 (테스트 가능)
- `SurfaceId` 제거 → PaneNode는 순수 트리 모델, Surface 매핑은 Service가 관리
- `Split()`은 in-place 변환 유지하되, **old/new leaf를 반환하여 호출자가 매핑 가능**

```csharp
public (PaneNode oldLeaf, PaneNode newLeaf) Split(
    SplitOrientation direction, uint newSessionId, uint oldLeafId, uint newLeafId)
{
    if (!IsLeaf) throw new InvalidOperationException("Cannot split branch node");

    var oldLeaf = new PaneNode { Id = oldLeafId, SessionId = SessionId };
    var newLeaf = new PaneNode { Id = newLeafId, SessionId = newSessionId };

    SplitDirection = direction;
    Left = oldLeaf;
    Right = newLeaf;
    SessionId = null;

    return (oldLeaf, newLeaf);
}
```

### 3.2 PaneLeafState (Service — 매핑 레코드)

```csharp
// GhostWin.Services/PaneLeafState.cs
// 하나의 leaf에 대한 모든 런타임 상태를 단일 레코드로 통합
public record PaneLeafState(
    uint PaneId,
    uint SessionId,
    uint SurfaceId,
    TerminalHostControl Host    // UI 참조 (Service→App 방향 허용: DI로 주입된 콜백 경유)
);
```

**v0.1 이중 Dictionary 문제 해결**: `_hostControls[id]`와 `_surfaceIds[id]`를 `Dictionary<uint, PaneLeafState>` 하나로 통합.

### 3.3 IPaneLayoutService (Core — 인터페이스)

```csharp
public interface IPaneLayoutService
{
    PaneNode? Root { get; }
    uint? FocusedPaneId { get; }

    void Initialize(uint initialSessionId, uint initialSurfaceId);
    (uint sessionId, PaneNode newLeaf) SplitFocused(SplitOrientation direction);
    void CloseFocused();
    void MoveFocus(FocusDirection direction);

    // UI 콜백: 새 TerminalHostControl HWND 준비 시 호출
    void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx);
}
```

### 3.4 PaneLayoutService (Services — 비즈니스 로직)

```csharp
public class PaneLayoutService : IPaneLayoutService
{
    private readonly IEngineService _engine;
    private readonly ISessionManager _sessions;
    private readonly Dictionary<uint, PaneLeafState> _leaves = [];
    private uint _nextPaneId = 1;

    public PaneNode? Root { get; private set; }
    public uint? FocusedPaneId { get; private set; }

    // Split: 트리 분할 + 세션 생성 (Surface는 HWND 준비 후 OnHostReady에서)
    public (uint sessionId, PaneNode newLeaf) SplitFocused(SplitOrientation direction)
    {
        var focused = FindLeaf(FocusedPaneId);
        var newSessionId = _sessions.CreateSession();

        var oldState = _leaves[focused.Id];
        var (oldLeaf, newLeaf) = focused.Split(direction, newSessionId,
            _nextPaneId++, _nextPaneId++);

        // 기존 leaf 상태를 oldLeaf ID로 이전 (host/surface 유지)
        _leaves.Remove(focused.Id);
        _leaves[oldLeaf.Id] = oldState with { PaneId = oldLeaf.Id };
        // newLeaf는 OnHostReady 콜백에서 Surface 생성

        FocusedPaneId = newLeaf.Id;

        WeakReferenceMessenger.Default.Send(new PaneLayoutChangedMessage(Root!));
        return (newSessionId, newLeaf);
    }

    // HWND 준비 완료 콜백 — Surface 생성
    public void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx)
    {
        var leaf = FindLeaf(paneId);
        if (leaf?.SessionId == null) return;

        var surfaceId = _engine.SurfaceCreate(hwnd, leaf.SessionId.Value, widthPx, heightPx);
        _leaves[paneId] = new PaneLeafState(paneId, leaf.SessionId.Value, surfaceId, ...);
    }
}
```

### 3.5 PaneContainerControl (App — UI만)

```csharp
public class PaneContainerControl : ContentControl,
    IRecipient<PaneLayoutChangedMessage>
{
    // 엔진/세션 참조 없음 — Grid 빌드만 담당

    public void Receive(PaneLayoutChangedMessage msg)
    {
        BuildGrid(msg.Root);
    }

    private void BuildGrid(PaneNode node) { ... }

    // leaf → Border + TerminalHostControl
    // branch → Grid + GridSplitter + 재귀
}
```

**v0.1 대비 제거된 것**: `_engine`, `_hostControls`, `_surfaceIds`, `_useSurfaces`, `SetupSurfaces()`, `OnPaneResized()` 전체 순회, retry 로직

### 3.6 TerminalHostControl 개선

```csharp
public class TerminalHostControl : HwndHost
{
    public uint PaneId { get; set; }  // 어떤 pane인지 식별

    public event Action<uint, uint, uint>? HostReady;  // paneId, widthPx, heightPx

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // ... 기존 HWND 생성 ...
        // HWND 생성 직후 이벤트 발생 — retry 불필요
        Dispatcher.BeginInvoke(() =>
            HostReady?.Invoke(PaneId, widthPx, heightPx));
        return new HandleRef(this, _childHwnd);
    }

    // RenderResizeRequested → paneId 포함하여 발생
    public event Action<uint, uint, uint>? PaneResizeRequested;  // paneId, w, h
}
```

**v0.1 대비 개선**:
- `PaneId` 속성 → resize 시 어떤 pane인지 식별 가능 (전체 순회 불필요)
- `HostReady` 이벤트 → HWND 생성 직후 발생 (retry 매직 넘버 폐기)

---

## 4. Engine (C++)

### 4.1 C API (변경 없음)

```c
typedef uint32_t GwSurfaceId;

GWAPI GwSurfaceId gw_surface_create(GwEngine, HWND, GwSessionId, uint32_t w, uint32_t h);
GWAPI int  gw_surface_destroy(GwEngine, GwSurfaceId);
GWAPI int  gw_surface_resize(GwEngine, GwSurfaceId, uint32_t w, uint32_t h);
GWAPI int  gw_surface_focus(GwEngine, GwSurfaceId);
```

`gw_render_init()`은 Device/Atlas/Pipeline 초기화만 담당. 렌더링은 Surface 전용.

### 4.2 SurfaceManager (C++ — 책임 분리)

v0.1에서 EngineImpl에 인라인되어 있던 surface 관리 코드를 별도 클래스로 분리.

```cpp
// src/engine-api/surface_manager.h
class SurfaceManager {
public:
    SurfaceManager(ID3D11Device* device);

    GwSurfaceId create(HWND hwnd, GwSessionId sessionId,
                       uint32_t w, uint32_t h);
    void destroy(GwSurfaceId id);
    void resize(GwSurfaceId id, uint32_t w, uint32_t h);

    // render thread에서 호출 — 스냅샷 반환 (lock 최소화)
    std::vector<RenderSurface*> active_surfaces();

private:
    ID3D11Device* device_;   // non-owning
    std::vector<std::unique_ptr<RenderSurface>> surfaces_;
    std::mutex mutex_;       // UI thread(create/destroy) ↔ render thread(active_surfaces)
    std::atomic<uint32_t> next_id_{1};
};
```

### 4.3 Render Loop (Surface 전용, 스레드 안전)

```cpp
void render_loop() {
    QuadBuilder builder(atlas->cell_width(), atlas->cell_height(),
                        atlas->baseline(), 0, 0, 0, 0);

    while (render_running.load(std::memory_order_acquire)) {
        Sleep(16);
        if (!renderer) continue;

        // 스냅샷 획득 (짧은 lock)
        auto active = surface_mgr->active_surfaces();
        if (active.empty()) { Sleep(1); continue; }

        for (auto* surf : active) {
            render_surface(surf, builder);
        }

        if (callbacks.on_render_done)
            callbacks.on_render_done(callbacks.context);
    }
}
```

### 4.4 DX11Renderer 캡슐화 유지

v0.1에서 `renderer->context()`를 직접 꺼내서 Clear/Viewport를 조작했던 것을 메서드로 캡슐화.

```cpp
// DX11Renderer에 추가
void DX11Renderer::render_to_surface(
    ID3D11RenderTargetView* rtv,
    uint32_t width, uint32_t height,
    uint32_t clear_color_rgb,
    const void* instances, uint32_t count, uint32_t bg_count);
```

render_surface()가 renderer 내부 상태를 직접 건드리지 않음.

---

## 5. 초기화/분할/닫기 흐름

### 5.1 초기화 흐름

```text
MainWindow.OnLoaded:
  1. engine.Initialize(callbacks)
  2. host = new TerminalHostControl()
  3. PaneContainer.Content = host

  (Dispatcher.Loaded)
  4. gw_render_init(host.ChildHwnd)      ← Device + Atlas + Pipeline
  5. TsfBridge 초기화
  6. gw_render_start()
  7. sessionId = CreateSession()           ← 렌더러 준비 후 세션 생성
  8. surfaceId = gw_surface_create(hwnd, sessionId, w, h)
                                           ← 첫 pane도 Surface로 렌더
  9. PaneLayoutService.Initialize(sessionId, surfaceId)
  10. PaneContainer.AdoptInitialHost(host, paneId)
```

### 5.2 분할 흐름

```text
Alt+V → MainWindowViewModel.SplitVerticalCommand
  → PaneLayoutService.SplitFocused(Vertical)
    1. oldState = _leaves[focusedId]         ← 기존 host/surface 보존
    2. newSessionId = _sessions.CreateSession()
    3. (oldLeaf, newLeaf) = focused.Split()  ← 트리 분할
    4. _leaves[oldLeaf.Id] = oldState        ← host/surface를 새 ID로 이전
    5. Send(PaneLayoutChangedMessage)

  → PaneContainerControl.OnLayoutChanged()
    6. BuildGrid: oldLeaf → oldHost 재사용, newLeaf → new TerminalHostControl
    7. newHost.HostReady 이벤트 발생 (HWND 생성 후)

  → PaneLayoutService.OnHostReady(newLeaf.Id, hwnd, w, h)
    8. surfaceId = gw_surface_create(hwnd, newSessionId, w, h)
    9. _leaves[newLeaf.Id] = new PaneLeafState(...)
```

**핵심**: 기존 host/surface를 파괴하지 않고 ID만 이전. 새 pane만 Surface 생성.

### 5.3 닫기 흐름

```text
Ctrl+Shift+W → PaneLayoutService.CloseFocused()
  1. state = _leaves[focusedId]
  2. gw_surface_destroy(state.SurfaceId)
  3. gw_session_close(state.SessionId)
  4. PaneNode.RemoveLeaf()                ← 트리 축소
  5. _leaves.Remove(focusedId)
  6. FocusedPaneId = 인접 leaf
  7. gw_surface_focus(인접 surfaceId)
  8. Send(PaneLayoutChangedMessage)
```

---

## 6. 키바인딩

| 동작 | 키 | 설명 |
|------|---|------|
| 수직 분할 | `Alt+V` | 현재 pane을 좌/우로 분할 |
| 수평 분할 | `Alt+H` | 현재 pane을 상/하로 분할 |
| 포커스 이동 | `Alt+방향키` | 인접 pane으로 포커스 이동 |
| Pane 닫기 | `Ctrl+Shift+W` | 현재 pane 닫기 (Ctrl+W는 탭 닫기) |

---

## 7. Implementation Order

### M-8a: Engine SurfaceManager + render_to_surface (C++ — 2일)

1. `SurfaceManager` 클래스 분리 (create/destroy/resize/active_surfaces + mutex)
2. `DX11Renderer::render_to_surface()` 메서드 추가
3. Render loop: Surface 전용 (legacy path 삭제)
4. C API 4개: 기존 구현을 SurfaceManager 위임으로 변경

**검증**: gw_surface_create → render loop에서 렌더링 확인

### M-8b: PaneLayoutService + PaneNode 리팩토링 (C# — 2일)

1. `PaneNode.Split()` — ID를 외부에서 주입, (oldLeaf, newLeaf) 반환
2. `PaneLeafState` 레코드 — host/surface/session 통합
3. `IPaneLayoutService` 인터페이스 (Core)
4. `PaneLayoutService` 구현 (Services) — Split/Close/MoveFocus + OnHostReady
5. `TerminalHostControl` — PaneId 속성 + HostReady 이벤트

**검증**: 단위 테스트로 PaneNode 트리 조작 검증

### M-8c: PaneContainerControl 리팩토링 + 통합 (C# — 2일)

1. `PaneContainerControl` — Grid 빌드만 (IRecipient\<PaneLayoutChangedMessage\>)
2. `MainWindowViewModel` — Split/Close 커맨드 → PaneLayoutService 위임
3. `MainWindow.xaml.cs` — 초기화 흐름 §5.1 구현
4. 키바인딩 연결 + 포커스 Border 시각 표시

**검증**: 단일 pane 렌더링 → Alt+V 분할 → 양쪽 렌더링 → pane 닫기

---

## 8. Non-Functional Requirements

| NFR | 목표 | 측정 방법 |
|-----|------|-----------|
| NFR-01 | 분할 시 < 100ms (렌더 시작까지) | SwapChain 생성 + 첫 프레임 |
| NFR-02 | pane당 추가 메모리 < 15MB | SwapChain 버퍼 + ConPTY |
| NFR-03 | 8 pane까지 60fps 유지 | GPU-Z / 프레임 카운터 |
| NFR-04 | Splitter 드래그 지연 < 16ms | 시각적 매끄러움 |
| NFR-05 | 기존 단일 pane 성능 동일 | V3/V6 벤치마크 회귀 없음 |
| NFR-06 | surfaces mutex 경합 < 1μs | lock 구간은 포인터 복사만 |

---

## 9. Risks

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| 다중 SwapChain Present 성능 | 중 | 중 | 8 pane 상한. dirty flag로 변경 없는 pane skip |
| HwndHost 다중 인스턴스 안정성 | 높음 | 중 | WT/VS에서 검증된 패턴 |
| GridSplitter + HwndHost Airspace | 중 | 중 | splitter 영역을 HwndHost 바깥에 배치 |
| surfaces mutex 경합 | 낮음 | 낮음 | active_surfaces()는 포인터 벡터 복사만 (< 1μs) |
| HWND 생성 타이밍 | 중 | 높음 | HostReady 이벤트로 해결 (retry 폐기) |

---

## 10. 직렬화 (Phase 5-F 대비)

PaneNode 트리는 JSON 직렬화를 고려하여 설계:

```json
{
  "root": {
    "split": "vertical",
    "ratio": 0.5,
    "left": { "session_cwd": "C:\\project", "shell": "pwsh.exe" },
    "right": {
      "split": "horizontal",
      "ratio": 0.6,
      "left": { "session_cwd": "C:\\logs", "shell": "pwsh.exe" },
      "right": { "session_cwd": "C:\\test", "shell": "pwsh.exe" }
    }
  }
}
```

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-06 | Initial design — multi-surface rendering + WPF PaneContainer | 노수장 |
| 0.2 | 2026-04-07 | Legacy render path 폐기, Surface 전용. 초기화/분할 흐름 구체화 | 노수장 |
| 0.3 | 2026-04-07 | 코드 품질 전면 보완 — SRP 분리 (PaneLayoutService/PaneContainerControl/SurfaceManager), PaneLeafState 통합 레코드, 스레드 안전, MVVM 준수, HostReady 이벤트, DX11Renderer 캡슐화 | 노수장 |
