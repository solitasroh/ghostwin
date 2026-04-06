# Pane Split Design Document

> **Summary**: 단일 탭 내에서 수평/수직 분할을 지원하는 Tree\<Pane\> 레이아웃 엔진. 엔진 다중 서피스 렌더링 + WPF Grid 동적 레이아웃으로 구현.
>
> **Project**: GhostWin Terminal
> **Author**: 노수장
> **Date**: 2026-04-06
> **Status**: Draft
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
2. **엔진 다중 서피스**: 단일 D3D11 Device + pane별 SwapChain (HWND 기반)
3. **WPF MVVM 통합**: PaneViewModel + 동적 Grid + GridSplitter
4. **기존 API 하위 호환**: 단일 pane(분할 없음) = 기존 동작과 동일

### 1.2 현재 아키텍처 제약

| 항목 | 현재 | pane-split 후 |
|------|------|---------------|
| SwapChain | 1개 (단일 HWND) | N개 (pane별 HWND) |
| 렌더 대상 | active_session() 1개만 | 모든 visible pane |
| Resize | resize_all() 균일 | pane별 독립 cols/rows |
| TSF 포커스 | active session 1개 | focused pane 1개 |
| GlyphAtlas | 공유 1개 | 변경 없음 (공유 유지) |

### 1.3 Plan과의 차이점

| Plan 기술 | Design 결정 | 변경 근거 |
|-----------|------------|-----------|
| Tree\<Pane\> C++ 엔진 내부 | Tree\<Pane\> WPF ViewModel 레이어 | 레이아웃은 UI 관심사. C++ 엔진은 서피스 렌더링만 담당 |
| 단일 SwapChain + viewport | pane별 SwapChain (HWND 기반) | HwndHost 별도 HWND가 자연스러움. ClearType 서피스 독립 보장 |
| PaneLayoutManager C++ 클래스 | PaneNode C# 트리 + PaneContainerControl | WPF Grid/GridSplitter 활용이 더 효율적 |

### 1.4 렌더 모드 전환 전략 (v0.2 보완)

**문제**: `gw_render_init()`이 생성하는 SwapChain은 특정 HWND에 바인딩됨.
분할 시 `RebuildVisualTree()`가 새 TerminalHostControl(새 HWND)을 생성하면
기존 SwapChain이 파괴된 HWND를 가리켜서 legacy render path가 실패.

**해결**: legacy render path를 제거하고, **처음부터 Surface API만 사용**.

```text
초기화:
  gw_render_init(hwnd, ...)  → Device + Atlas + 셰이더 파이프라인 생성
                                (SwapChain도 생성되지만 렌더링에 사용 안 함)
  gw_surface_create(hwnd, session0, w, h) → Surface 0 생성 (렌더링 담당)
  gw_render_start() → render loop은 항상 surfaces 벡터 순회

분할 시:
  gw_session_create() → session 1 생성
  gw_surface_destroy(surface0) → 기존 서피스 제거
  RebuildVisualTree() → 새 TerminalHostControl 2개 (새 HWND 2개)
  gw_surface_create(hwnd_left, session0, w, h) → Surface 1
  gw_surface_create(hwnd_right, session1, w, h) → Surface 2
  render loop이 Surface 1, 2를 순회 렌더링
```

**핵심 원칙**: legacy render path(active_session 단일 렌더)를 완전 폐기.
모든 렌더링은 Surface API를 통해 수행. 단일 pane이든 다중 pane이든 동일한 경로.

### 1.5 PaneNode.Split() ID 관리 (v0.2 보완)

**문제**: `Split()`이 기존 노드를 branch로 변환하면서 자식에게 새 ID를 부여.
`_hostControls[원래ID]`가 소실되어 host ↔ leaf 매핑이 깨짐.

**해결**: 분할 시 기존 leaf의 host를 보존하고 자식에게 이전.

```text
Before Split:
  _hostControls[rootId] = host_A (RenderInit SwapChain 바인딩)

Split 호출 시:
  1. 기존 host_A 참조를 보존
  2. PaneNode.Split() → oldLeaf(newId), newLeaf(newId) 생성
  3. _hostControls[oldLeaf.Id] = host_A (보존된 host 이전)
  4. _hostControls[newLeaf.Id] = new TerminalHostControl()
  5. Grid 재구성 시 host_A는 왼쪽 Grid cell에 배치 (HWND 유지)
```

이렇게 하면 기존 host의 HWND가 유지되어 Surface의 SwapChain도 유효.

---

## 2. Architecture

### 2.1 컴포넌트 구조

```text
GhostWin.App (WPF UI)
  ├── PaneContainerControl          ← 동적 Grid + GridSplitter 관리
  ├── PaneNode (트리 자료구조)        ← Split/Close/Navigate 로직
  └── TerminalHostControl           ← pane별 HwndHost (기존)

GhostWin.Core (인터페이스)
  ├── IPaneLayout                   ← Pane 트리 조회/조작
  └── Models/PaneInfo               ← Pane 상태 데이터

GhostWin.Interop (P/Invoke)
  └── NativeEngine.cs               ← 신규 API 추가 (4개)

ghostwin_engine.dll (C++ Engine)
  ├── gw_surface_create()           ← pane HWND용 SwapChain 생성
  ├── gw_surface_destroy()          ← SwapChain 해제
  ├── gw_surface_resize()           ← pane 리사이즈
  └── Render Loop                   ← 모든 서피스 순회 렌더링
```

### 2.2 의존성 다이어그램

```text
PaneContainerControl (App)
  ↓ uses
PaneNode (App/Models)
  ↓ uses
IPaneLayout (Core)
  ↓ impl
PaneLayoutService (Services)
  ↓ uses
IEngineService (Core)
  ↓ impl
EngineService (Interop)
  ↓ P/Invoke
ghostwin_engine.dll
```

### 2.3 데이터 흐름

```text
사용자 Alt+V (수직 분할)
  → MainWindowViewModel.SplitVerticalCommand
    → PaneLayoutService.Split(focusedPaneId, Orientation.Vertical)
      → IEngineService.CreateSession(cols, rows)  ← 새 세션 생성
      → PaneNode.Split(newSessionId, Orientation.Vertical)  ← 트리 분할
      → IEngineService.SurfaceCreate(newHwnd, w, h)  ← 새 SwapChain
      → IEngineService.SurfaceBindSession(surfaceId, newSessionId)
    ← PaneLayoutChanged 이벤트 (WeakReferenceMessenger)
  → PaneContainerControl.RebuildGrid()  ← Grid 재구성
```

---

## 3. Detailed Design

### 3.1 Engine C API 확장 (4개 추가)

```c
// ── Surface management (pane별 렌더 서피스) ──
typedef uint32_t GwSurfaceId;

// pane HWND에 SwapChain 생성 + 세션 바인딩
GWAPI GwSurfaceId gw_surface_create(GwEngine engine, HWND hwnd,
    GwSessionId session_id,
    uint32_t width_px, uint32_t height_px);

// SwapChain 해제
GWAPI int gw_surface_destroy(GwEngine engine, GwSurfaceId id);

// pane 리사이즈 (SwapChain + ConPTY cols/rows)
GWAPI int gw_surface_resize(GwEngine engine, GwSurfaceId id,
    uint32_t width_px, uint32_t height_px);

// focused pane 변경 (TSF 포커스 전환)
GWAPI int gw_surface_focus(GwEngine engine, GwSurfaceId id);
```

**하위 호환 폐기 (v0.2 변경)**: legacy render path 제거. `gw_render_init()`은 Device/Atlas/Pipeline 초기화만 담당. 모든 렌더링은 `gw_surface_create()`로 생성된 Surface를 통해 수행. 초기 단일 pane도 Surface를 사용.

### 3.2 Engine 내부 구조 변경

#### 3.2.1 Surface 구조체

```cpp
struct RenderSurface {
    GwSurfaceId id;
    GwSessionId session_id;
    HWND hwnd;
    ComPtr<IDXGISwapChain2> swapchain;
    ComPtr<ID3D11RenderTargetView> rtv;
    uint32_t width_px, height_px;
    bool dirty = true;  // 리사이즈 후 RTV 재생성 플래그
};
```

#### 3.2.2 EngineImpl 변경

```cpp
struct EngineImpl {
    // 기존
    std::unique_ptr<SessionManager> session_mgr;
    std::unique_ptr<DX11Renderer> renderer;      // D3D11 Device 소유
    std::unique_ptr<GlyphAtlas> atlas;            // 공유 아틀라스

    // 신규 (v0.2: legacy render path 폐기, Surface만 사용)
    std::vector<std::unique_ptr<RenderSurface>> surfaces;
    std::atomic<uint32_t> next_surface_id{1};
    GwSurfaceId focused_surface_id{0};
};
```

#### 3.2.3 Render Loop 변경 (v0.2: Surface 전용)

```cpp
void render_loop() {
    QuadBuilder builder(atlas->cell_width(), atlas->cell_height(),
                        atlas->baseline(), 0, 0, 0, 0);

    while (render_running.load(std::memory_order_acquire)) {
        Sleep(16);
        if (!renderer || surfaces.empty()) { Sleep(1); continue; }

        // 모든 서피스 순회 (단일 pane도 Surface로 렌더)
        for (auto& surf : surfaces) {
            render_surface(surf.get(), builder);
        }

        if (callbacks.on_render_done)
            callbacks.on_render_done(callbacks.context);
    }
}

// Legacy render path 완전 폐기.
// gw_render_init()의 SwapChain은 사용하지 않음.
// 첫 pane도 gw_surface_create()로 Surface를 생성하여 렌더.
```

### 3.3 PaneNode 트리 구조 (C#)

```csharp
// GhostWin.Core/Models/PaneNode.cs
public class PaneNode
{
    public uint Id { get; init; }
    public uint? SessionId { get; set; }        // leaf만 세션 보유
    public uint? SurfaceId { get; set; }        // leaf만 서피스 보유
    public Orientation? SplitDirection { get; set; }  // branch: H or V
    public PaneNode? Left { get; set; }          // 첫 번째 자식
    public PaneNode? Right { get; set; }         // 두 번째 자식
    public double Ratio { get; set; } = 0.5;     // 분할 비율 (0.0~1.0)

    public bool IsLeaf => Left == null && Right == null;
}
```

#### 트리 예시

```text
초기 상태 (분할 없음):
  Leaf(session=0, surface=0)

Alt+V 수직 분할 후:
  Branch(dir=Vertical, ratio=0.5)
  ├── Leaf(session=0, surface=0)    ← 왼쪽
  └── Leaf(session=1, surface=1)    ← 오른쪽 (신규)

왼쪽 pane에서 Alt+H 수평 분할 후:
  Branch(dir=Vertical, ratio=0.5)
  ├── Branch(dir=Horizontal, ratio=0.5)
  │   ├── Leaf(session=0, surface=0)   ← 위
  │   └── Leaf(session=2, surface=2)   ← 아래 (신규)
  └── Leaf(session=1, surface=1)
```

### 3.4 PaneContainerControl (WPF)

```csharp
// GhostWin.App/Controls/PaneContainerControl.cs
public class PaneContainerControl : ContentControl
{
    public void BuildFromTree(PaneNode root)
    {
        Content = BuildElement(root);
    }

    private UIElement BuildElement(PaneNode node)
    {
        if (node.IsLeaf)
        {
            var host = new TerminalHostControl();
            host.Tag = node;  // pane 참조
            return host;
        }

        var grid = new Grid();

        if (node.SplitDirection == Orientation.Horizontal)
        {
            grid.RowDefinitions.Add(new RowDefinition
                { Height = new GridLength(node.Ratio, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition
                { Height = GridLength.Auto });  // splitter
            grid.RowDefinitions.Add(new RowDefinition
                { Height = new GridLength(1.0 - node.Ratio, GridUnitType.Star) });

            var left = BuildElement(node.Left!);
            Grid.SetRow(left, 0);
            grid.Children.Add(left);

            var splitter = new GridSplitter
            {
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Color.FromRgb(0x58, 0x58, 0x58))
            };
            Grid.SetRow(splitter, 1);
            grid.Children.Add(splitter);

            var right = BuildElement(node.Right!);
            Grid.SetRow(right, 2);
            grid.Children.Add(right);
        }
        else // Vertical
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(node.Ratio, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(1.0 - node.Ratio, GridUnitType.Star) });

            var left = BuildElement(node.Left!);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var splitter = new GridSplitter
            {
                Width = 4,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Color.FromRgb(0x58, 0x58, 0x58))
            };
            Grid.SetColumn(splitter, 1);
            grid.Children.Add(splitter);

            var right = BuildElement(node.Right!);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }

        return grid;
    }
}
```

### 3.5 초기화 흐름 (v0.2)

```text
MainWindow.OnLoaded:
  1. engine.Initialize(callbacks)
  2. PaneContainer.Initialize(engine)

InitializeRenderer:
  1. host = new TerminalHostControl()        ← 첫 pane의 HwndHost
  2. PaneContainer.Content = host             ← visual tree에 부착
  3. (Dispatcher.Loaded)
     a. gw_render_init(host.ChildHwnd, ...)  ← Device + Atlas + Pipeline
     b. TsfBridge 초기화
     c. gw_render_start()
     d. sessionId = CreateSession()           ← 렌더러 준비 후 세션 생성
     e. surfaceId = gw_surface_create(host.ChildHwnd, sessionId, w, h)
                                              ← 첫 pane도 Surface로 렌더
     f. PaneContainer.AdoptInitialHost(host, sessionId, surfaceId)
                                              ← host를 PaneNode 트리에 등록
```

**핵심**: `gw_render_init`의 SwapChain은 렌더링에 사용하지 않음. 모든 렌더링은 Surface.

### 3.6 분할 흐름 (v0.2)

```text
SplitFocused(direction, newSessionId):
  1. oldHost = _hostControls[focusedLeaf.Id]  ← 기존 host 참조 보존
  2. oldSurfaceId = _surfaceIds[focusedLeaf.Id]
  3. PaneNode.Split() → oldLeaf(새 ID), newLeaf(새 ID) 생성
  4. _hostControls[oldLeaf.Id] = oldHost       ← 보존된 host를 새 ID로 이전
  5. _surfaceIds[oldLeaf.Id] = oldSurfaceId    ← Surface도 이전
  6. newHost = new TerminalHostControl()
  7. _hostControls[newLeaf.Id] = newHost
  8. Grid 재구성 (oldHost + GridSplitter + newHost)
  9. (Dispatcher.Loaded)
     newSurfaceId = gw_surface_create(newHost.ChildHwnd, newSessionId, w, h)
     _surfaceIds[newLeaf.Id] = newSurfaceId
```

**핵심**: 기존 host/surface를 파괴하지 않고 새 PaneNode ID에 이전. HWND/SwapChain 유지.

### 3.7 포커스 관리

```text
포커스 이동 (Alt+방향키):
  → PaneLayoutService.MoveFocus(Direction)
    → PaneNode 트리 탐색 (현재 focused leaf → 방향에 맞는 인접 leaf)
    → IEngineService.SurfaceFocus(newSurfaceId)  ← TSF 전환
    → FocusedPaneChanged 이벤트
  → PaneContainerControl: 포커스 표시 Border 업데이트 (2px accent color)
```

**방향 탐색 알고리즘**:
1. 현재 leaf에서 부모로 올라가면서 이동 가능한 분기점 탐색
2. 분기점에서 반대쪽 자식으로 진입
3. 이동 방향의 가장 가까운 leaf로 도달

### 3.6 Pane 닫기

```text
Ctrl+Shift+W (현재 pane 닫기):
  → PaneLayoutService.ClosePane(focusedPaneId)
    → IEngineService.SurfaceDestroy(surfaceId)  ← SwapChain 해제
    → IEngineService.CloseSession(sessionId)    ← 세션 종료
    → PaneNode: leaf 제거 → 부모가 단일 자식 → 부모를 자식으로 대체 (트리 축소)
    → 인접 pane으로 포커스 이동
  → PaneContainerControl.RebuildGrid()

마지막 pane 닫기:
  → 탭 전체 닫기 (기존 Ctrl+W 동작)
```

### 3.7 키바인딩

| 동작 | 키 | 설명 |
|------|---|------|
| 수직 분할 | `Alt+V` | 현재 pane을 좌/우로 분할 |
| 수평 분할 | `Alt+H` | 현재 pane을 상/하로 분할 |
| 포커스 이동 | `Alt+방향키` | 인접 pane으로 포커스 이동 |
| Pane 닫기 | `Ctrl+Shift+W` | 현재 pane 닫기 (Ctrl+W는 탭 닫기) |

---

## 4. Implementation Order

### M-8a: Engine Surface API (C++ — 2일)

1. `RenderSurface` 구조체 정의
2. `gw_surface_create/destroy/resize/focus` 구현
3. EngineImpl에 surfaces 벡터 추가
4. Render loop 다중 서피스 순회 로직
5. 기존 `gw_render_init` 하위 호환 유지

**검증**: 단일 서피스로 기존 동작 동일 + 2개 서피스 생성/파괴 테스트

### M-8b: P/Invoke + PaneNode (C# — 1일)

1. `NativeEngine.cs`에 4개 API 추가
2. `EngineService.cs`에 래퍼 메서드 추가
3. `IEngineService`에 Surface 메서드 추가
4. `PaneNode` 클래스 구현 (Core/Models)
5. `IPaneLayout` 인터페이스 정의

### M-8c: PaneContainerControl + 통합 (C# — 3일)

1. `PaneContainerControl` 구현 (Grid 동적 빌드)
2. `PaneLayoutService` 구현 (Split/Close/MoveFocus)
3. MainWindow.xaml에서 TerminalHostControl → PaneContainerControl 교체
4. 키바인딩 연결 (Alt+V/H/방향키, Ctrl+Shift+W)
5. 포커스 표시 (Border accent)
6. GridSplitter 드래그 → SurfaceResize 연동

**검증**: 2-pane 분할 → 독립 렌더링 → 포커스 전환 → pane 닫기

---

## 5. Non-Functional Requirements

| NFR | 목표 | 측정 방법 |
|-----|------|-----------|
| NFR-01 | 분할 시 < 100ms (렌더 시작까지) | SwapChain 생성 + 첫 프레임 |
| NFR-02 | pane당 추가 메모리 < 15MB | SwapChain 버퍼 + ConPTY |
| NFR-03 | 8 pane까지 60fps 유지 | GPU-Z / 프레임 카운터 |
| NFR-04 | Splitter 드래그 지연 < 16ms | 시각적 매끄러움 |
| NFR-05 | 기존 단일 pane 성능 동일 | V3/V6 벤치마크 회귀 없음 |

---

## 6. Risks

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| 다중 SwapChain Present 성능 | 중 | 중 | 8 pane 상한. dirty flag로 변경 없는 pane skip |
| HwndHost 다중 인스턴스 안정성 | 높음 | 중 | WPF HwndHost 다중 생성은 WT/VS에서 검증된 패턴 |
| GridSplitter + HwndHost Airspace | 중 | 중 | GridSplitter는 HwndHost 위에 그려지지 않음 → splitter 영역은 HwndHost 바깥에 배치 |
| Render loop 다중 mutex 락 | 중 | 낮음 | pane 순서 고정 (ID 오름차순) → 교착 방지 |
| 기존 gw_render_init 호환 깨짐 | 높음 | 낮음 | 기본 서피스(ID=0)로 래핑하여 하위 호환 보장 |

---

## 7. 직렬화 (Phase 5-F 대비)

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
| 0.2 | 2026-04-07 | Legacy render path 폐기, Surface 전용 렌더링. 초기화 흐름/분할 흐름 구체화. PaneNode ID 이전 전략 추가 | 노수장 |
