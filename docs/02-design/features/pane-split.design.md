# Pane Split Design Document

> **Summary**: Workspace 기반 Tree\<Pane\> 레이아웃 엔진. 각 workspace는 독립적인 pane tree를 보유하며, workspace 간 전환과 workspace 내 수평/수직 분할을 모두 지원 (cmux 모델: Window → Workspace → Pane → Surface).
>
> **Project**: GhostWin Terminal
> **Author**: 노수장
> **Date**: 2026-04-06 (v0.4.1) / 2026-04-07 (v0.5 Workspace Layer, v0.5.1 legacy fallback 종료)
> **Status**: **Phase A 구현 완료** (v0.5.1 — Surface 전용 경로 정합)
> **Planning Doc**: [multi-session-ui.plan.md](../../01-plan/features/multi-session-ui.plan.md) (FR-05)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | (v0.4) 탭당 1개 세션만 표시 → 병렬 작업에 탭 전환 필수. (v0.5) 추가로 sidebar entry(= 1 session) 모델이 cmux 철학과 불일치 — workspace 개념 부재로 탭과 pane이 따로 놀음. |
| **Solution** | Workspace = sidebar entry = 1 pane tree. 각 workspace가 독립 `IPaneLayoutService` instance 보유. Workspace 내에서 Tree\<Pane\> 기반 재귀 분할 + 엔진 다중 서피스 렌더링 + WPF Grid 동적 레이아웃. |
| **Function/UX Effect** | `Ctrl+T`로 새 workspace, `Ctrl+W`로 workspace 전체 close, `Alt+H/V`로 현재 workspace의 pane 분할, `Alt+방향키`로 pane 포커스, `Ctrl+Shift+W`로 현재 pane만 close, sidebar 클릭으로 workspace 전환. |
| **Core Value** | cmux 정식 모델(Window → Workspace → Pane → Surface)을 WPF + DX11로 구현. Phase 5-F 세션 복원 기반 (workspace 단위 직렬화). |

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

### 1.2.5 cmux 계층 모델 (v0.5 — Workspace Layer)

cmux 공식 docs ([cmux.com/docs](https://cmux.com/docs))와 리서치 문서(`docs/00-research/cmux-ai-agent-ux-research.md`)에서 확인한 정식 계층:

```text
Window → Workspace → Pane → Surface → Panel
```

| Layer | 정의 | GhostWin v0.5 매핑 |
|---|---|---|
| **Window** | OS 윈도우 | `MainWindow` |
| **Workspace** | sidebar entry — 1개의 logical 작업 단위. 자체 pane tree 보유. | `WorkspaceInfo` + per-workspace `IPaneLayoutService` |
| **Pane** | workspace 내 split 영역 (좌/우/상/하 재귀 분할) | `PaneNode` leaf |
| **Surface** | pane 내 multiple tabs (terminal 또는 browser). 각 `CMUX_SURFACE_ID` 환경변수로 식별. | **Phase C 미구현** (현재 pane 1개당 surface 1개 고정) |
| **Panel** | surface의 actual content (native renderer) | `TerminalHostControl` child HWND + native DX11 swap chain |

**v0.4와의 핵심 차이**:
- v0.4는 "1 session = 1 sidebar entry" 모델 → pane-split이 새 session을 만들 때 자동으로 sidebar entry가 추가되어 workspace 의도와 충돌
- v0.5는 "1 workspace = 1 sidebar entry + 1 pane tree + N sessions" 모델 → pane-split은 workspace 내부에서만 동작, sidebar는 workspace 단위

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
| 10 | C++ render_surface() 캡슐화 깨짐 | context()를 꺼내서 직접 Clear/Viewport | DX11Renderer에 `bind_surface`/`upload_and_draw`/`unbind_surface` 3-step API (v0.4에서 `render_to_target` 단일 메서드로 제안되었으나 최종 구현은 3개로 분할) |

### 1.4 Legacy fallback 상태 (v0.5 → v0.5.1 종료)

v0.5까지 런타임에 legacy fallback 경로가 `render_loop` 내부에 공존했다. 구체적으로:

- `ghostwin_engine.cpp:321` — `release_swapchain()` 호출이 주석 처리되어 renderer 내부 SwapChain이 살아있음
- `WorkspaceService.cs:49` — `paneLayout.Initialize(sessionId, 0)` 호출에서 `initialSurfaceId = 0` placeholder
- `render_loop` 내부의 `if (!active.empty()) … else { legacy path }` — `active_surfaces()` 비어있을 때 renderer 내부 SwapChain을 사용하는 legacy 렌더 경로
- `gw_render_resize`의 `renderer->resize_swapchain()` 호출 — 내부 SwapChain 의존 코드

이 상태는 **Phase 5-E 초기(M-8a) Surface 경로 도입 시의 안전망**이었다. Surface 경로가 검증되는 동안 legacy fallback으로 돌아갈 수 있도록 두 경로가 공존했고, 내부 주석/Plan 문서에서는 이를 "BISECT 상태"로 지칭했다.

**v0.5.1 (feature: `bisect-mode-termination`, 2026-04-07)에서 legacy fallback 전면 종료**:

| 변경 | 파일 | 효과 |
|---|---|---|
| `render_loop` else 분기 삭제 + `if (active.empty()) Sleep(1); continue;` 가드 | `ghostwin_engine.cpp` | Surface 경로가 유일 경로. warm-up 기간은 렌더 skip |
| `release_swapchain()` 활성화 | `ghostwin_engine.cpp:305` | 부트스트랩 SwapChain 해제. SurfaceManager가 per-pane SwapChain 단독 소유 |
| `IPaneLayoutService.Initialize(uint)` 시그니처 단순화 | `IPaneLayoutService.cs`, `PaneLayoutService.cs`, `WorkspaceService.cs` | dead parameter `initialSurfaceId` 제거. `OnHostReady`가 2-단계 초기화로 실제 surfaceId 설정 |
| `gw_render_resize` no-op deprecate (ABI 호환) + `MainWindow.OnTerminalResized` 삭제 | `ghostwin_engine.cpp`, `MainWindow.xaml.cs`, `IEngineService.cs` | 중복 resize 경로 제거. pane resize는 `PaneContainerControl.OnPaneResized → SurfaceResize` 단일 경로 |
| `OnHostReady`에 `SurfaceCreate == 0` 실패 진단 로그 추가 | `PaneLayoutService.cs` | silent failure 방지 |

**주의**: 10-agent v0.5 평가 §1 C1은 이 상태를 "Critical: design↔runtime divergence"로 평가했으나, `bisect-mode-termination`의 code-analyzer council 실측 결과 **아키텍처 결함이 아니라 legacy safety net**이었다. 실제 Surface 경로는 v0.5 출시 시점부터 완전히 작동 중이었고, legacy 경로는 warm-up window와 실패 시 fallback으로만 사용되고 있었다. 이는 10-agent 평가의 §7 "정직한 불확실성" 조항이 중요했던 이유이다.

---

## 2. Architecture (v0.3)

### 2.1 컴포넌트 구조 및 책임

```text
GhostWin.Core
  ├── Models/PaneNode.cs               ← 순수 트리 자료구조 (UI/엔진 무관)
  ├── Models/SplitOrientation.cs       ← enum (Horizontal, Vertical)
  ├── Models/WorkspaceInfo.cs          ← (v0.5) Workspace 메타데이터 (Id/Name/Title/Cwd/IsActive)
  ├── Events/WorkspaceEvents.cs        ← (v0.5) Created/Closed/Activated messages
  ├── Interfaces/IPaneLayoutService.cs ← Split/Close/MoveFocus/SetFocused + FocusedSessionId (v0.5)
  └── Interfaces/IWorkspaceService.cs  ← (v0.5) Workspace 라이프사이클 + ActivePaneLayout

GhostWin.Services
  ├── PaneLayoutService.cs             ← 트리 조작 + 엔진 Surface 생명주기
  │                                      (IEngineService, ISessionManager 주입)
  │                                      (v0.5) per-workspace instance로 사용됨 (DI Singleton 폐기)
  └── WorkspaceService.cs              ← (v0.5) workspaces Dictionary 관리.
                                         CreateWorkspace = 새 PaneLayoutService 인스턴스 + 첫 session.
                                         ActiveWorkspaceId + ActivePaneLayout 노출.

GhostWin.App
  ├── Controls/PaneContainerControl.cs ← Grid/GridSplitter 빌드 + per-workspace host 캐시 swap.
  │                                      (v0.5) IRecipient<WorkspaceActivatedMessage> 구현
  ├── Controls/TerminalHostControl.cs  ← HwndHost + PaneId/SessionId 속성 + 정적 _hostsByHwnd
  ├── ViewModels/WorkspaceItemViewModel.cs ← (v0.5) sidebar 항목 wrap (WorkspaceInfo 기반)
  └── ViewModels/MainWindowViewModel.cs ← (v0.5) Workspaces 컬렉션 + NewWorkspace/CloseWorkspace/
                                                  NextWorkspace/SplitV/SplitH/ClosePane 커맨드
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
public record PaneLeafState(uint PaneId, uint SessionId, uint SurfaceId);
```

**v0.1 이중 Dictionary 문제 해결**: `_hostControls[id]`와 `_surfaceIds[id]`를 `Dictionary<uint, PaneLeafState>` 하나로 통합.

**Host 참조 제거 (v0.4)**: `TerminalHostControl`은 App 레이어이므로 Services에서 참조 금지. Host ↔ PaneId 매핑은 `PaneContainerControl`(App)이 `Dictionary<uint, TerminalHostControl>`로 별도 관리. HWND는 `OnHostReady(paneId, hwnd, w, h)` 콜백으로 Service에 전달.

### 3.3 IPaneLayoutService (Core — 인터페이스, v0.4 정본)

```csharp
public interface IPaneLayoutService
{
    IReadOnlyPaneNode? Root { get; }
    uint? FocusedPaneId { get; }
    int LeafCount { get; }
    const int MaxPanes = 8;

    void Initialize(uint initialSessionId, uint initialSurfaceId);
    (uint sessionId, uint newPaneId)? SplitFocused(SplitOrientation direction);
        // null 반환 = MaxPanes 도달 시 분할 거부
    void CloseFocused();
    void MoveFocus(FocusDirection direction);

    // UI 콜백: 새 TerminalHostControl HWND 준비 시 호출
    void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx);
    // UI 콜백: pane 리사이즈 시 호출
    void OnPaneResized(uint paneId, uint widthPx, uint heightPx);
}
```

### 3.4 PaneLayoutService (Services — 비즈니스 로직)

```csharp
public class PaneLayoutService : IPaneLayoutService
{
    private readonly IEngineService _engine;
    private readonly ISessionManager _sessions;
    private readonly IMessenger _messenger;           // DI 주입 (N-4 fix)
    private readonly Dictionary<uint, PaneLeafState> _leaves = [];
    private uint _nextPaneId = 1;

    public PaneLayoutService(
        IEngineService engine, ISessionManager sessions, IMessenger messenger)
    {
        _engine = engine;
        _sessions = sessions;
        _messenger = messenger;
    }

    public IReadOnlyPaneNode? Root { get; private set; }  // §3.7과 통일
    public uint? FocusedPaneId { get; private set; }
    public int LeafCount => _leaves.Count;

    private uint AllocateId() => _nextPaneId++;       // N-1 가독성 개선

    // Split: 트리 분할 + 세션 생성 (Surface는 HWND 준비 후 OnHostReady에서)
    public (uint sessionId, uint newPaneId)? SplitFocused(SplitOrientation direction)
    {                                                  // §3.7 nullable 반환과 통일 (N-2 fix)
        if (LeafCount >= MaxPanes) return null;        // 8 pane 가드

        var focused = FindLeaf(FocusedPaneId);
        if (focused == null) return null;

        var newSessionId = _sessions.CreateSession();
        var oldState = _leaves[focused.Id];
        var oldId = AllocateId();
        var newId = AllocateId();
        var (oldLeaf, newLeaf) = focused.Split(direction, newSessionId, oldId, newId);

        // 기존 leaf 상태를 oldLeaf ID로 이전 (host/surface 유지)
        _leaves.Remove(focused.Id);
        _leaves[oldLeaf.Id] = oldState with { PaneId = oldLeaf.Id };

        // newLeaf placeholder 등록 (N-3 fix) — SurfaceId=0은 OnHostReady에서 갱신
        _leaves[newLeaf.Id] = new PaneLeafState(newLeaf.Id, newSessionId, SurfaceId: 0);

        FocusedPaneId = newLeaf.Id;
        _messenger.Send(new PaneLayoutChangedMessage((IReadOnlyPaneNode)Root!));
        return (newSessionId, newLeaf.Id);
    }

    // HWND 준비 완료 콜백 — Surface 생성
    public void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx)
    {
        if (!_leaves.TryGetValue(paneId, out var state)) return;
        if (state.SurfaceId != 0) return;  // 이미 생성됨

        var leaf = FindLeaf(paneId);
        if (leaf?.SessionId == null) return;

        var surfaceId = _engine.SurfaceCreate(hwnd, leaf.SessionId.Value, widthPx, heightPx);
        _leaves[paneId] = state with { SurfaceId = surfaceId };
    }

    public const int MaxPanes = 8;
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

### 3.6 TerminalHostControl 개선 (v0.4 — W-7)

```csharp
public record HostReadyEventArgs(uint PaneId, nint Hwnd, uint WidthPx, uint HeightPx);
public record PaneResizeEventArgs(uint PaneId, uint WidthPx, uint HeightPx);

public class TerminalHostControl : HwndHost
{
    public uint PaneId { get; set; }

    public event EventHandler<HostReadyEventArgs>? HostReady;
    public event EventHandler<PaneResizeEventArgs>? PaneResizeRequested;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // ... 기존 HWND 생성 ...
        Dispatcher.BeginInvoke(() =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var w = (uint)Math.Max(1, ActualWidth * dpi.DpiScaleX);
            var h = (uint)Math.Max(1, ActualHeight * dpi.DpiScaleY);
            HostReady?.Invoke(this, new(PaneId, _childHwnd, w, h));
        });
        return new HandleRef(this, _childHwnd);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_childHwnd == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = (uint)Math.Max(1, sizeInfo.NewSize.Width * dpi.DpiScaleX);
        var h = (uint)Math.Max(1, sizeInfo.NewSize.Height * dpi.DpiScaleY);
        SetWindowPos(_childHwnd, ...);
        PaneResizeRequested?.Invoke(this, new(PaneId, w, h));
    }
}
```

**v0.1 대비 개선**:
- `HostReadyEventArgs` / `PaneResizeEventArgs` 레코드 → 타입 안전 + hwnd 포함 (W-7)
- `EventHandler<T>` WPF 컨벤션 준수
- PaneId로 어떤 pane인지 식별 → 전체 순회 불필요
- `HostReady` 이벤트 → retry 매직 넘버 폐기

### 3.7 ~~IPaneLayoutService 보강~~ → §3.3에 통합 (v0.4.1)

> §3.3이 IPaneLayoutService의 정본 정의. v0.4에서 여기에 중복 정의가 있었으나 v0.4.1에서 §3.3으로 통합함.

### 3.8 IReadOnlyPaneNode (v0.4 — W-9)

```csharp
// GhostWin.Core/Models/IReadOnlyPaneNode.cs
public interface IReadOnlyPaneNode
{
    uint Id { get; }
    uint? SessionId { get; }
    SplitOrientation? SplitDirection { get; }
    IReadOnlyPaneNode? Left { get; }
    IReadOnlyPaneNode? Right { get; }
    double Ratio { get; }
    bool IsLeaf { get; }
}

// PaneNode : IReadOnlyPaneNode 구현
```

메시지 페이로드에 `IReadOnlyPaneNode`를 전달하여 수신자가 트리를 변경할 수 없도록 보장.

### 3.9 PaneLayoutChangedMessage (v0.4 — W-7, W-8)

```csharp
// GhostWin.Core/Events/PaneEvents.cs (Core 레이어에 위치)
public sealed class PaneLayoutChangedMessage(IReadOnlyPaneNode root)
    : ValueChangedMessage<IReadOnlyPaneNode>(root);

public sealed class PaneFocusChangedMessage(uint paneId, uint sessionId)
    : ValueChangedMessage<(uint PaneId, uint SessionId)>((paneId, sessionId));
```

### 3.10 PaneContainerControl 메시지 등록 (v0.4)

```csharp
public PaneContainerControl()
{
    Loaded += (_, _) => WeakReferenceMessenger.Default.Register(this);
    Unloaded += (_, _) => WeakReferenceMessenger.Default.Unregister(this);
}
```

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
    SurfaceManager(ID3D11Device* device, IDXGIFactory2* factory);
                                          // factory 캐싱 (W-10)
    GwSurfaceId create(HWND hwnd, GwSessionId sessionId,
                       uint32_t w, uint32_t h);
    void destroy(GwSurfaceId id);
    void resize(GwSurfaceId id, uint32_t w, uint32_t h);

    // render thread에서 호출 — 스냅샷 반환 (lock 최소화)
    std::vector<RenderSurface*> active_surfaces();

    // render loop 완료 후 호출 — deferred destroy 처리
    void flush_pending_destroys();

private:
    ID3D11Device* device_;          // non-owning
    IDXGIFactory2* factory_;        // non-owning, 캐싱 (W-10)
    std::vector<std::unique_ptr<RenderSurface>> surfaces_;
    std::vector<std::unique_ptr<RenderSurface>> pending_destroy_;  // deferred destroy 큐
    std::mutex mutex_;
    std::atomic<uint32_t> next_id_{1};
};
```

**Deferred Destroy 패턴 (신규)**:
- `destroy(id)`는 `surfaces_`에서 제거 후 `pending_destroy_`로 이동 (mutex 하에)
- `active_surfaces()` 스냅샷에 있는 포인터는 `pending_destroy_`에 보존되므로 dangling 불가
- render loop 매 프레임 끝에서 `flush_pending_destroys()` 호출 → 실제 해제
- `flush_pending_destroys()` 내부에서도 `mutex_` lock 필요 (UI thread의 destroy와 경합 방지)
- 이로써 스냅샷 후 destroy 시에도 포인터 안전 보장

### 4.3 Render Loop (Surface 전용, 스레드 안전) (v0.4 보강)

```cpp
void render_loop() {
    QuadBuilder builder(atlas->cell_width(), atlas->cell_height(),
                        atlas->baseline(), 0, 0, 0, 0);

    while (render_running.load(std::memory_order_acquire)) {
        Sleep(16);
        if (!renderer) continue;

        // 스냅샷 획득 — 짧은 lock으로 포인터만 복사 (C-1 해결)
        auto active = surface_mgr->active_surfaces();
        if (active.empty()) { Sleep(1); continue; }

        for (auto* surf : active) {
            render_surface(surf, builder);
        }

        // Deferred destroy — 스냅샷 사용 완료 후 안전하게 해제
        surface_mgr->flush_pending_destroys();

        if (callbacks.on_render_done)
            callbacks.on_render_done(callbacks.context);
    }
}

void render_surface(RenderSurface* surf, QuadBuilder& builder) {
    auto* session = session_mgr->get(surf->session_id);
    if (!session || !session->conpty || !session->is_live()) return;

    // C-7 해결: resize 중 torn read 방지
    // SurfaceManager::resize()가 dirty 플래그만 설정,
    // 실제 ResizeBuffers + RTV 재생성은 render thread에서 수행
    if (surf->needs_resize.load(std::memory_order_acquire)) {  // acquire: pending_w/h visibility 보장
        surf->rtv.Reset();
        surf->swapchain->ResizeBuffers(0, surf->pending_w, surf->pending_h,
            DXGI_FORMAT_UNKNOWN, 0);
        // RTV 재생성
        ComPtr<ID3D11Texture2D> bb;
        surf->swapchain->GetBuffer(0, IID_PPV_ARGS(&bb));
        renderer->device()->CreateRenderTargetView(bb.Get(), nullptr, &surf->rtv);
        surf->width_px = surf->pending_w;
        surf->height_px = surf->pending_h;
        surf->needs_resize.store(false);
    }
    if (!surf->rtv) return;

    // staging 동적 확장 (C-3 해결)
    uint32_t needed = (surf->width_px / atlas->cell_width() + 1)
                    * (surf->height_px / atlas->cell_height() + 1)
                    * constants::kInstanceMultiplier + 16;
    if (staging.size() < needed) staging.resize(needed);

    auto& vt = session->conpty->vt_core();
    auto& state = *session->state;

    bool dirty = state.start_paint(session->vt_mutex, vt);
    if (!dirty) return;

    const auto& frame = state.frame();
    uint32_t bg_count = 0;
    uint32_t count = builder.build(frame, *atlas, renderer->context(),
        std::span<QuadInstance>(staging), &bg_count);

    if (count > 0) {
        renderer->render_to_target(surf->rtv.Get(),
            surf->width_px, surf->height_px,
            renderer_clear_color, staging.data(), count, bg_count);
    }

    surf->swapchain->Present(1, 0);
}
```

### 4.5 이중 vt_mutex 문제 (v0.4 — Critical C-5)

**기존 버그 발견**: `Session::vt_mutex`(session.h)와 `ConPtySession::Impl::vt_mutex`(conpty_session.cpp)가
**별개의 mutex**인데 동일한 VtCore를 보호. I/O thread는 내부 mutex, render thread는 외부 mutex만 잡음.

**단기 해결 (pane-split 범위)**: `Session::vt_mutex`를 ConPtySession 내부로 전달하여 단일 mutex로 통합.
또는 ConPtySession 생성 시 외부 mutex 포인터를 주입하는 패턴.

```cpp
// ConPtySession 생성자에 외부 mutex 주입
class ConPtySession {
public:
    ConPtySession(std::mutex& shared_vt_mutex, ...);
    // 내부에서 이 mutex를 사용하여 write() 보호
};
```

**이 문제는 pane-split과 무관하게 기존 코드에 존재**.
다중 pane에서는 race 빈도가 pane 수에 비례하여 증가하므로, **M-8a(Engine Surface API) 구현 시 함께 수정**.
별도 ADR-013으로 추적. pane-split 이전에 해결하지 않으면 multi-pane 안정성 확보 불가.

### 4.6 SurfaceManager resize 스레드 안전 (v0.4 — C-7 상세)

UI thread에서 resize 요청 시 실제 `ResizeBuffers()`를 호출하지 않고, pending 값만 설정:

```cpp
void SurfaceManager::resize(GwSurfaceId id, uint32_t w, uint32_t h) {
    std::lock_guard lk(mutex_);
    auto* surf = find(id);
    if (!surf) return;
    surf->pending_w = w > 0 ? w : 1;
    surf->pending_h = h > 0 ? h : 1;
    surf->needs_resize.store(true, std::memory_order_release);
    // 실제 ResizeBuffers는 render thread의 render_surface()에서 수행
}
```

render thread만 SwapChain/RTV를 조작하므로 torn read 불가.

### 4.4 DX11Renderer Surface 바인딩 API (v0.5.1 — 실제 구현 반영)

> **v0.4 → v0.5.1 갱신**: v0.4 문서는 `render_to_target()` 단일 메서드로 제안했으나 최종 구현은 `bind_surface`/`upload_and_draw`/`unbind_surface` **3단계 호출 패턴**으로 분할되었다. 이 섹션은 실제 코드를 반영한다. (`src/renderer/dx11_renderer.cpp:763-779`, `src/engine-api/ghostwin_engine.cpp:164-170`)

v0.1의 `render_surface()`는 `renderer->context()`를 직접 꺼내서 Clear/Viewport를 설정한 뒤
`upload_and_draw()`를 호출했다. **그러나 `upload_and_draw()` 내부가 다시 main RTV를 바인딩하고
main SwapChain에 Present** → surface에 아무것도 렌더링되지 않는 근본 원인.

해결: DX11Renderer에 외부 surface를 주입할 수 있는 `bind_surface`/`unbind_surface` setter 추가 + 기존 `upload_and_draw`는 주입된 surface에 렌더.

```cpp
// DX11Renderer public API (실제 구현, dx11_renderer.h)
void bind_surface(void* rtv, void* swapchain, uint32_t width_px, uint32_t height_px);
void unbind_surface();
void upload_and_draw(const void* instances, uint32_t count, uint32_t bg_count);
```

**호출 순서** (`ghostwin_engine.cpp:164-170` `render_surface` 내부):
```cpp
renderer->bind_surface(
    static_cast<void*>(surf->rtv.Get()),
    static_cast<void*>(surf->swapchain.Get()),
    surf->width_px, surf->height_px);
renderer->upload_and_draw(staging.data(), count, bg_count);
renderer->unbind_surface();
```

**내부 수행 순서** (`draw_instances` 내부, dx11_renderer.cpp:555-596):
1. constant buffer 갱신: `pos_scale_x = 2.0f / bb_width`, `pos_scale_y = -2.0f / bb_height` (C-4 해결) — bb_width/bb_height는 `bind_surface`가 설정
2. `ClearRenderTargetView(rtv, clear_color)`
3. `OMSetRenderTargets(1, &rtv, nullptr)`
4. `RSSetViewports(bb_width, bb_height)`
5. instance upload + `DrawIndexedInstanced`
6. **Present 호출 안 함** — caller(render_surface)가 `surf->swapchain->Present(1, 0)`

**Surface unbind**: `unbind_surface()`는 `impl_->rtv.Reset()`, `impl_->swapchain.Reset()`, `bb_width/height = 0`으로 내부 state를 정리한다. 다음 `bind_surface` 호출 전까지 renderer는 "no target" 상태 유지.

**staging 버퍼 동적 확장** (C-3 해결): `render_surface()` 시작부에서 surface 크기에 맞게 staging 벡터를 재할당.

**v0.5.1에서 내부 SwapChain 해제**: `gw_render_init`에서 `release_swapchain()` 호출로 DX11Renderer의 부트스트랩 HWND SwapChain을 해제. 이후 `upload_and_draw`는 반드시 `bind_surface`로 외부 RTV가 주입된 상태에서만 호출되어야 한다.

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
    0. if (LeafCount >= MaxPanes) return null   ← 8 pane 가드 (W-4)
    1. oldState = _leaves[focusedId]            ← 기존 surface 보존
    2. newSessionId = _sessions.CreateSession()
    3. (oldLeaf, newLeaf) = focused.Split()     ← 트리 분할
    4. _leaves[oldLeaf.Id] = oldState with { PaneId = oldLeaf.Id }
    5. Send(PaneLayoutChangedMessage(Root))

  → PaneContainerControl.OnLayoutChanged()
    6. BuildGrid: oldLeaf → oldHost 재사용, newLeaf → new TerminalHostControl
    7. newHost.HostReady 이벤트 발생 (HWND 생성 후)

  → PaneLayoutService.OnHostReady(newLeaf.Id, hwnd, w, h)
    8. surfaceId = gw_surface_create(hwnd, newSessionId, w, h)
    9. _leaves[newLeaf.Id] = new PaneLeafState(newLeaf.Id, newSessionId, surfaceId)
   10. gw_surface_focus(surfaceId)              ← TSF 포커스 전환 (W-6)
```

**핵심**: 기존 host/surface를 파괴하지 않고 ID만 이전. 새 pane만 Surface 생성.

### 5.3 닫기 흐름 (v0.4 보강 — W-3, W-6)

```text
Ctrl+Shift+W → PaneLayoutService.CloseFocused()
  1. if (Root.IsLeaf) → 마지막 pane: 탭 닫기로 에스컬레이션 (W-3)
     → ISessionManager.CloseSession(sessionId)
     → SessionClosedMessage → MainWindowViewModel → 탭 제거 → 앱 종료 (기존 흐름)
     → return

  2. state = _leaves[focusedId]
  3. adjacentLeaf = FindAdjacentLeaf(focusedId)
  4. gw_surface_destroy(state.SurfaceId)           ← SwapChain 해제
  5. ISessionManager.CloseSession(state.SessionId) ← ConPTY 정리 (C-8 명확화)
     → IEngineService.CloseSession() → gw_session_close() 호출 체인
  6. PaneNode.RemoveLeaf()                          ← 트리 축소
  7. _leaves.Remove(focusedId)
  8. FocusedPaneId = adjacentLeaf.Id
  9. gw_surface_focus(adjacent.SurfaceId)           ← TSF 포커스 전환 (W-6)
 10. Send(PaneLayoutChangedMessage(Root))
```

**마지막 pane 정책 (W-3)**: 마지막 pane 닫기 = 탭 전체 닫기. 빈 탭 상태는 허용하지 않음. 앱의 마지막 탭이면 기존 SessionClosedMessage 흐름에 의해 앱 종료.

---

### 5.4 Resize 흐름 (v0.4 — W-1)

```text
GridSplitter 드래그:
  → GridSplitter.DragDelta
    → PaneNode.Ratio 업데이트 (WPF Grid Star 비율 자동 반영)
    → 각 leaf의 TerminalHostControl.OnRenderSizeChanged 발생
      → PaneResizeRequested(paneId, w, h) 이벤트
    → PaneLayoutService.OnPaneResized(paneId, w, h)
      → gw_surface_resize(surfaceId, w, h)
        → SurfaceManager: pending_w/h 설정 + needs_resize = true
        → render thread의 render_surface()에서 실제 ResizeBuffers 수행 (C-7)
      → gw_session_resize(sessionId, cols, rows)
        → ConPTY resize

주의: GridSplitter 드래그 중에는 PaneLayoutChangedMessage를 발행하지 않음.
Grid 재구성이 필요 없으며, WPF Grid Star 비율이 자동으로 레이아웃 처리.
```

### 5.5 포커스 이동 흐름 (v0.4 — W-5 개선)

```text
Alt+방향키 → PaneLayoutService.MoveFocus(direction)
  → 공간 인식 탐색 알고리즘:
    1. 현재 focused leaf의 bounds(pixel rect) 계산
    2. 방향에 맞는 인접 leaf 탐색:
       - Left: focused.Left 경계에서 왼쪽에 위치한 leaf 중 가장 가까운 것
       - Right: focused.Right 경계에서 오른쪽
       - Up/Down: 동일 로직 수직 방향
    3. 1차 구현: leaf 리스트 선형 탐색 (2-pane에서는 충분)
       2차 개선: bounds 기반 공간 탐색 (4+ pane에서 정확도 보장)
  → FocusedPaneId 갱신
  → gw_surface_focus(surfaceId)   ← TSF 포커스 전환 (W-6)
  → Send(PaneFocusChangedMessage)
```

---

## 6. 키바인딩 (v0.5 — Workspace 모델 반영)

### 6.1 Workspace (sidebar 단위)

| 동작 | 키 | 설명 |
|------|---|------|
| 새 Workspace | `Ctrl+T` | 새 workspace 생성 (새 sidebar entry + 자체 pane tree + 첫 session) |
| Workspace 닫기 | `Ctrl+W` | 현재 workspace 전체 닫기 (그 안의 모든 panes/sessions 정리). 마지막 workspace였으면 앱 종료. |
| 다음 Workspace | `Ctrl+Tab` | sidebar 다음 workspace로 순환 |
| Workspace 선택 | Sidebar 클릭 | 해당 workspace의 PaneContainer로 전환 (각 workspace의 host 인스턴스 보존) |

### 6.2 Pane (active workspace 내)

| 동작 | 키 | 설명 |
|------|---|------|
| 수직 분할 | `Alt+V` | 현재 pane을 좌/우로 분할 |
| 수평 분할 | `Alt+H` | 현재 pane을 상/하로 분할 |
| 포커스 이동 | `Alt+방향키` | 인접 pane으로 포커스 이동 |
| Pane 닫기 | `Ctrl+Shift+W` | 현재 pane만 닫기 (workspace 자체는 유지) |
| 마우스 클릭 | 좌/우/중 클릭 | 클릭한 pane으로 포커스 이동 (child HWND WndProc에서 `_hostsByHwnd` lookup + `Dispatcher.BeginInvoke`로 UI 스레드 마샬링) |

### 6.3 구현 주의: `Ctrl+...` 단축키 직접 dispatch

`TerminalHostControl` (HwndHost)의 child HWND가 키보드 포커스를 가지면 일반 `WM_KEYDOWN`이 child WndProc → `DefWindowProc`에서 소비되어 WPF의 `InputBinding`까지 bubble up 되지 않음. 반면 `Alt+...`는 `WM_SYSKEYDOWN`이라 `HwndSource.CriticalTranslateAccelerator`가 preprocessing 단계에서 WPF input pipeline으로 forward → InputBinding 통과.

따라서 `Ctrl+T/W/Tab`, `Ctrl+Shift+W`는 `MainWindow.OnTerminalKeyDown` (PreviewKeyDown) 핸들러에서 **직접 `IWorkspaceService` 메서드 호출**. `Alt+V/H`도 같은 핸들러에 fallback 분기를 둠 (InputBinding은 여전히 유효하나 중복 안전장치).

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

### M-8d: Crash Fixes + Session-based Host Migration (v0.5 — 완료)

M-8c 통합 후 사용자 검증 과정에서 발견된 root cause 10건 해결:

1. **Focus sync**: `SetFocused/MoveFocus/CloseFocused`에 `_sessions.ActivateSession(sessionId)` 동기화
2. **PaneNode.RemoveLeaf 재설계**: in-place mutation → grandparent splice. 시그니처 `bool → PaneNode?`. leaf paneId 보존으로 `_leaves`/`_hostControls` dict 정합성 유지
3. **Alt+화살표 SystemKey**: `e.Key == Key.System ? e.SystemKey : e.Key` 보정 — Alt는 `WM_SYSKEYDOWN`으로 들어와 `e.Key == Key.System`
4. **Border host reparent**: reused host의 `previousBorder.Child = null`로 WPF logical tree 단일 parent 제약 충족
5. **Last-pane `_root` stale**: `CloseFocused`의 last-pane 분기에서 `_root = null; FocusedPaneId = null;` 명시
6. **`_initialHost` ghost reference**: `InitializeRenderer` 끝에서 `_initialHost = null`로 끊고 `AdoptInitialHost`가 항상 Border wrap
7. **Dead host dispose**: cleanup loop에 `host.Dispose()` 추가. 단, **`Dispatcher.BeginInvoke(..., Background)`로 deferred** — 동기 dispose는 WPF visual tree update 전에 `DestroyWindow` 호출 → native AV
8. **`_hostsByHwnd` race**: `Dictionary → ConcurrentDictionary`. WndProc 람다에서 `_childHwnd != 0` 가드
9. **Session-based host migration**: `oldLeaf.SessionId` 보존 활용. paneId 매칭 실패 시 sessionId로 fallback lookup → 같은 session의 host 인스턴스 영속화. cleanup loop을 host instance 비교(`HashSet<TerminalHostControl>`)로 변경해 false dispose 방지
10. **App crash diagnostics**: `App.xaml.cs`에 `DispatcherUnhandledException` + `AppDomain.UnhandledException` + `UnobservedTaskException` 핸들러 + `ghostwin-crash.log` dump

### M-9: Workspace Layer (v0.5 — 완료, cmux 정식 모델)

`docs/00-research/cmux-ai-agent-ux-research.md` + [cmux.com/docs](https://cmux.com/docs) 재조사 후 5-level 계층 (Window → Workspace → Pane → Surface → Panel)을 정식 반영.

1. **신규 Core 타입**: `WorkspaceInfo` (ObservableObject), `WorkspaceCreated/Closed/ActivatedMessage`, `IWorkspaceService`, `IPaneLayoutService.FocusedSessionId`
2. **신규 Services**: `WorkspaceService` — per-workspace `PaneLayoutService` 인스턴스 관리, CreateWorkspace/CloseWorkspace/ActivateWorkspace, session title/cwd를 WorkspaceInfo에 mirror
3. **DI 변경**: `IPaneLayoutService` Singleton 제거. `IWorkspaceService` Singleton 등록. `PaneLayoutService`는 `WorkspaceService`가 직접 `new`로 인스턴스 생성·소유
4. **PaneContainerControl workspace-aware 재작성**: `_hostsByWorkspace: Dictionary<uint, Dictionary<uint, TerminalHostControl>>` per-workspace 캐시. workspace 전환 시 `SwitchToWorkspace`가 이전 workspace의 hosts를 저장하고 새 workspace의 hosts를 복원 + BuildGrid. `IRecipient<WorkspaceActivatedMessage>` 구현
5. **MainWindowViewModel 리팩토링**: `Tabs`/`SelectedTab`/`NewTab`/`CloseTab`/`NextTab` → `Workspaces`/`SelectedWorkspace`/`NewWorkspace`/`CloseWorkspace`/`NextWorkspace`. `IRecipient<WorkspaceCreated/Closed/Activated>` 구독. `TerminalTabViewModel` → `WorkspaceItemViewModel`
6. **MainWindow.xaml 갱신**: sidebar `ItemsSource="{Binding Workspaces}"`, KeyBinding `Ctrl+T` = `NewWorkspaceCommand`, `Ctrl+W` = `CloseWorkspaceCommand`
7. **MainWindow.xaml.cs**: `_paneLayout` 필드 제거 → `_workspaceService`. `InitializeRenderer`에서 `_workspaceService.CreateWorkspace()` 호출 후 `AdoptInitialHost(initialHost, workspaceId, paneId, sessionId)`. `OnTerminalKeyDown`에 **Ctrl+T/W/Tab, Ctrl+Shift+W 직접 dispatch** 추가 — HwndHost focus 상태에서 `InputBinding` 미도달 문제 우회

**검증 (2026-04-07)**: 앱 시작 → split → pane focus → pane close → Ctrl+T 새 workspace → sidebar 전환 → Ctrl+W workspace close 전부 통과.

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

## 9. Risks (v0.4 보강)

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| 다중 SwapChain Present(1,0) vsync 누적 | 높음 | 중 | 8 pane * 16ms = 128ms 최악. **`Present(0, DXGI_PRESENT_ALLOW_TEARING)` + waitable 검토** (W-11). 1차는 Present(1,0) 유지, 4+ pane에서 프레임 드랍 시 전환 |
| HwndHost 다중 인스턴스 안정성 | 높음 | 중 | WT/VS에서 검증된 패턴 |
| GridSplitter + HwndHost Airspace | 중 | 낮음 | splitter를 별도 Grid row/column에 배치 — HwndHost와 겹치지 않음. 검증 완료 |
| surfaces mutex 경합 | 낮음 | 낮음 | active_surfaces()는 포인터 벡터 복사만 (< 1μs) |
| HWND 생성 타이밍 | 중 | 높음 | HostReady 이벤트로 해결 (retry 폐기) |
| 이중 vt_mutex (C-5) | 높음 | 확정 | 기존 버그. 별도 ADR로 추적. 단기: Session mutex를 ConPty에 주입 |
| resize 중 torn read (C-7) | 높음 | 중 | pending 값 + render thread에서 ResizeBuffers 수행 |
| DXGI Factory 재질의 비효율 | 낮음 | 확정 | SurfaceManager 초기화 시 Factory 캐싱 |

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

## 11. Test Plan (v0.4 — W-2)

### 단위 테스트 (PaneNode)

| # | 시나리오 | 기대 결과 |
|---|---------|----------|
| T-1 | CreateLeaf → Split(Vertical) | branch(Left=oldLeaf, Right=newLeaf), (old,new) 반환 |
| T-2 | Split on branch node | throw InvalidOperationException |
| T-3 | RemoveLeaf (2-pane → 1-pane) | 부모가 surviving child로 대체 |
| T-4 | GetLeaves on 3-level tree | 올바른 DFS 순서 |
| T-5 | FindLeaf(존재하지 않는 sessionId) | null 반환 |

### 통합 테스트 (PaneLayoutService)

| # | 시나리오 | 기대 결과 |
|---|---------|----------|
| T-6 | Initialize → SplitFocused → 2 leaves | 2개 PaneLeafState, 2개 Surface |
| T-7 | 8번 Split → 9번째 시도 | null 반환 (MaxPanes 가드) |
| T-8 | Split → CloseFocused → 1 leaf | Surface destroy 호출, 트리 축소 |
| T-9 | 마지막 pane Close | SessionManager.CloseSession 호출 |
| T-10 | MoveFocus(Right) on 2-pane | FocusedPaneId 변경 + SurfaceFocus 호출 |
| T-11 | OnPaneResized | SurfaceResize + SessionResize 호출 |

### 스레드 안전 테스트 (C++)

| # | 시나리오 | 기대 결과 |
|---|---------|----------|
| T-12 | Render thread 순회 중 Surface create | crash 없음 (mutex 보호) |
| T-13 | Render thread 순회 중 Surface destroy | crash 없음 (스냅샷 사용) |
| T-14 | Resize 중 render_surface 호출 | torn read 없음 (pending 패턴) |

---

## 12. Migration Checklist (v0.4 — C-8)

### 신규 파일

| 파일 | 프로젝트 | 설명 |
|------|---------|------|
| `Core/Models/IReadOnlyPaneNode.cs` | Core | 읽기 전용 트리 인터페이스 |
| `Core/Models/FocusDirection.cs` | Core | enum (App에서 이동) |
| `Core/Events/PaneEvents.cs` | Core | PaneLayoutChangedMessage, PaneFocusChangedMessage |
| `Core/Interfaces/IPaneLayoutService.cs` | Core | pane 서비스 인터페이스 |
| `Services/PaneLayoutService.cs` | Services | 비즈니스 로직 (트리 + Surface 생명주기) |
| `Services/PaneLeafState.cs` | Services | 통합 레코드 (Host 참조 없음) |
| `engine-api/surface_manager.h/cpp` | C++ | SurfaceManager 클래스 (mutex 포함) |

### 수정 파일

| 파일 | 변경 내용 |
|------|----------|
| `Core/Models/PaneNode.cs` | static _nextId 제거, Split() 시그니처 변경, IReadOnlyPaneNode 구현 |
| `App/Controls/PaneContainerControl.cs` | 전면 리팩토링 — 엔진 참조 제거, Grid 빌드만, IRecipient 등록 |
| `App/Controls/TerminalHostControl.cs` | PaneId 속성, HostReady/PaneResizeRequested EventHandler\<T\> |
| `App/ViewModels/MainWindowViewModel.cs` | SplitRequested 이벤트 제거 → IPaneLayoutService 직접 호출 |
| `App/MainWindow.xaml.cs` | 초기화 흐름 §5.1 적용, OnSplitRequested/OnClosePaneRequested 제거 |
| `App/App.xaml.cs` | DI: IPaneLayoutService + IMessenger 등록 |
| `engine-api/ghostwin_engine.cpp` | SurfaceManager 위임, legacy render path 삭제, render_surface() → render_to_target() 사용 |
| `renderer/dx11_renderer.h/cpp` | render_to_target() 메서드 추가 |

### 삭제 대상

| 항목 | 근거 |
|------|------|
| `PaneContainerControl._engine` 필드 | MVVM 위반 제거 |
| `PaneContainerControl._surfaceIds` / `_hostControls` | PaneLeafState + 별도 host map으로 대체 |
| `PaneContainerControl._useSurfaces` 플래그 | Surface 전용 경로로 폐기 |
| `PaneContainerControl.SetupSurfaces()` retry 로직 | HostReady 이벤트로 대체 |
| `MainWindowViewModel.SplitRequested` / `ClosePaneRequested` 이벤트 | Service 직접 호출로 대체 |
| `ghostwin_engine.cpp` legacy render path (else 분기) | Surface 전용 |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-06 | Initial design — multi-surface rendering + WPF PaneContainer | 노수장 |
| 0.2 | 2026-04-07 | Legacy render path 폐기, Surface 전용. 초기화/분할 흐름 구체화 | 노수장 |
| 0.3 | 2026-04-07 | 코드 품질 전면 보완 — SRP 분리, PaneLeafState, 스레드 안전, MVVM 준수, HostReady, DX11Renderer 캡슐화 | 노수장 |
| 0.4 | 2026-04-07 | 4-agent 검증 반영 — Critical 8건 + Warning 13건 해결 | 노수장 |
| 0.4.1 | 2026-04-07 | 재평가 잔여 8건 완료 — IMessenger DI 주입, SplitFocused 시그니처 통일, newLeaf placeholder 등록, deferred destroy 패턴, memory_order_acquire 명시, AllocateId() 분리, ADR-013 할당, OnPaneResized 인터페이스 추가 | 노수장 |
| **0.5** | **2026-04-07** | **M-8d Crash fixes (10건) + M-9 Workspace Layer 정식 도입. cmux 5-level 계층 (Window → Workspace → Pane → Surface → Panel) 반영. IWorkspaceService/WorkspaceService 추가. PaneLayoutService Singleton 폐기 → per-workspace instance. PaneContainerControl `_hostsByWorkspace` 캐시 swap. MainWindowViewModel Tabs→Workspaces 리팩토링. KeyBinding Ctrl+T/W/Tab direct dispatch (HwndHost focus 우회)** | **노수장** |
| **0.5.1** | **2026-04-07** | **Legacy fallback 전면 종료 (feature: `bisect-mode-termination`). §1.4 신설, §4.4 실제 구현(`bind_surface`/`upload_and_draw`/`unbind_surface` 3-step API) 반영. `render_loop` else 분기 삭제 + warm-up 가드, `release_swapchain()` 활성화, `IPaneLayoutService.Initialize(uint)` 단순화, `gw_render_resize` no-op deprecate (ABI 호환), `MainWindow.OnTerminalResized` 중복 경로 제거, `OnHostReady`에 `SurfaceCreate == 0` 실패 진단 로그 추가. Council: wpf-architect + code-analyzer + dotnet-expert.** | **노수장 (CTO Lead)** |
