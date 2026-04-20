# PaneContainerControl 재설계 — Design v1.0

| 관점 | 요약 |
|------|------|
| 문제 | BuildElement가 Grid 생성 + HwndHost 관리 + 서비스 주입 + 포커스 시각 효과를 377줄에 전부 담당하는 기형 구조 |
| 목표 | Grid 구조는 XAML DataTemplate 재귀, HwndHost 관리는 PaneHostManager 분리, PaneContainerControl은 200줄 이하 coordinator |
| 전략 | PaneNode → PaneNodeViewModel 래퍼 + HierarchicalDataTemplate + Attached Behavior + 호스트 풀링 |
| 위험 | HwndHost와 DataTemplate 자동 생성/파괴 충돌, HC-4 순서 제약 유지, workspace 전환 시 호스트 보존 |

## 1. 현재 문제 분석

### 1.1 개발 히스토리

PaneContainerControl은 M-8(pane split) 도입 시 기존 단일 터미널 구조에 기능을 누적하며 성장했다. 원래는 단일 TerminalHostControl만 표시하면 됐으나, split/close/focus/workspace 전환 기능이 추가되면서 BuildElement 하나가 모든 책임을 떠안게 되었다.

### 1.2 현재 구조의 문제점

**PaneContainerControl.cs (377줄)의 책임 과부하:**

| 줄 범위 | 책임 | 문제 |
|---------|------|------|
| L148-158 | BuildGrid: 트리 순회 시작 | Grid 생성을 코드로 수행 — XAML 선언적 패턴 위반 |
| L187-193 | BuildElement: Branch/Leaf 분기 | DataTemplate이 해야 할 역할을 코드가 대신 |
| L197-209 | BuildLeaf: 호스트 재사용 + Border 래핑 | 호스트 lifecycle과 뷰 생성이 결합 |
| L212-246 | FindOrCreateHost: 3단계 호스트 탐색 | 호스트 풀링 로직이 뷰 컨트롤 안에 위치 |
| L254-275 | EnsureServices: DI 수동 주입 | Ioc.Default 직접 호출 — 테스트 불가 |
| L279-313 | BuildSplitGrid: Grid + GridSplitter 코드 생성 | RowDefinitions/ColumnDefinitions를 C# 코드로 조립 |
| L359-376 | UpdateFocusVisuals: 포커스 시각 처리 | 모든 호스트를 순회하며 Border 속성 변경 — Attached Property가 적합 |

**핵심 문제 요약:**

1. **Grid 구조가 C# 코드에 존재** — BuildSplitGrid(L279-313)가 Grid, RowDefinition, ColumnDefinition, GridSplitter를 전부 코드로 생성. WPF의 핵심 장점인 XAML 선언적 UI를 활용하지 못함
2. **HwndHost lifecycle이 뷰 코드에 결합** — FindOrCreateHost(L212-246)의 3단계 탐색(paneId → sessionId → 신규 생성)이 BuildLeaf 안에 위치
3. **서비스 주입이 수동** — EnsureServices(L254-275)가 `Ioc.Default.GetService`를 직접 호출하여 테스트 불가
4. **포커스 시각 효과가 중앙 집중** — UpdateFocusVisuals(L359-376)가 모든 호스트를 순회. 각 Border가 자체 처리하는 것이 WPF 패턴
5. **GridSplitter Ratio 역전파 누락** — 사용자가 GridSplitter를 드래그해도 PaneNode.Ratio가 갱신되지 않는 버그

## 2. 목표 아키텍처

### 2.1 역할 분리

```
+------------------------------------------------------------------+
|  PaneContainerControl (coordinator, ~150줄)                       |
|  - Messenger 구독 (Layout/Focus/Workspace)                       |
|  - Content = PaneNodeViewModel (root) 바인딩                     |
|  - PaneHostManager 소유                                          |
+------------------------------------------------------------------+
        |                           |
        v                           v
+---------------------+   +----------------------------+
| PaneNodeViewModel   |   | PaneHostManager            |
| (ObservableObject)  |   | (HwndHost 풀링 + lifecycle)|
| - IsLeaf            |   | - per-workspace 풀         |
| - SplitDirection    |   | - FindOrCreate             |
| - Ratio (양방향)    |   | - DisposeOrphans           |
| - Left/Right (재귀) |   | - EnsureServices           |
| - SessionId         |   +----------------------------+
+---------------------+
        |
        v (DataTemplate 바인딩)
+------------------------------------------------------------------+
| XAML DataTemplates (PaneTemplates.xaml)                            |
|                                                                    |
|  LeafTemplate:  Border + ContentPresenter                         |
|    - PaneFocusBehavior (Attached Property)                        |
|    - Loaded → PaneHostManager.AttachHost()                        |
|                                                                    |
|  BranchTemplate: Grid + GridSplitter + 2x ContentPresenter       |
|    - GridDefinitionBehavior (Attached Behavior)                   |
|    - GridSplitter DragCompleted → Ratio 역전파                    |
+------------------------------------------------------------------+
```

### 2.2 데이터 흐름

```
PaneLayoutChangedMessage
  → PaneContainerControl.Receive
    → PaneNodeViewModel.UpdateFrom(IReadOnlyPaneNode)
      → PropertyChanged → DataTemplate 갱신
        → Leaf: ContentPresenter.Loaded → PaneHostManager.AttachHost
        → Branch: GridDefinitionBehavior가 Ratio로 Row/Column 생성

PaneFocusChangedMessage
  → PaneContainerControl.Receive
    → PaneContainerControl.FocusedPaneId DP 갱신
      → PaneFocusBehavior가 각 Border에서 자체 시각 처리

WorkspaceActivatedMessage
  → PaneContainerControl.Receive
    → PaneHostManager.SwitchWorkspace(oldId, newId)
    → Content = 새 workspace의 root PaneNodeViewModel
```

## 3. 상세 설계

### 3.1 PaneNodeViewModel

PaneNode(Core 모델)를 WPF가 바인딩할 수 있도록 감싸는 ObservableObject 래퍼.

**위치:** `src/GhostWin.App/ViewModels/PaneNodeViewModel.cs`

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using GhostWin.Core.Models;

namespace GhostWin.App.ViewModels;

public partial class PaneNodeViewModel : ObservableObject
{
    [ObservableProperty] private bool _isLeaf;
    [ObservableProperty] private uint _paneId;
    [ObservableProperty] private uint? _sessionId;
    [ObservableProperty] private SplitOrientation? _splitDirection;
    [ObservableProperty] private double _ratio = 0.5;
    [ObservableProperty] private PaneNodeViewModel? _left;
    [ObservableProperty] private PaneNodeViewModel? _right;

    /// <summary>
    /// IReadOnlyPaneNode에서 ViewModel 트리를 재귀적으로 생성.
    /// 기존 VM 트리를 재사용하여 불필요한 재생성 방지.
    /// </summary>
    public static PaneNodeViewModel FromNode(
        IReadOnlyPaneNode node, PaneNodeViewModel? existing = null)
    {
        var vm = existing ?? new PaneNodeViewModel();
        vm.PaneId = node.Id;
        vm.IsLeaf = node.IsLeaf;
        vm.SessionId = node.SessionId;
        vm.SplitDirection = node.SplitDirection;
        vm.Ratio = node.Ratio;

        if (node.IsLeaf)
        {
            vm.Left = null;
            vm.Right = null;
        }
        else
        {
            vm.Left = FromNode(node.Left!, vm.Left);
            vm.Right = FromNode(node.Right!, vm.Right);
        }

        return vm;
    }
}
```

**설계 근거:**
- 10-agent 조사에서 7/10이 "IReadOnlyPaneNode를 직접 바인딩하면 INotifyPropertyChanged가 없어 UI 갱신 불가"를 지적
- CommunityToolkit.Mvvm의 `[ObservableProperty]`로 보일러플레이트 최소화
- `FromNode`의 `existing` 매개변수로 VM 트리 재활용 — split/close 시 변경되지 않은 subtree의 DataTemplate 재생성 방지

### 3.2 DataTemplate 재귀 구조

**위치:** `src/GhostWin.App/Resources/PaneTemplates.xaml`

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:vm="clr-namespace:GhostWin.App.ViewModels"
                    xmlns:b="clr-namespace:GhostWin.App.Behaviors">

    <!-- Leaf: 빈 ContentPresenter → Loaded에서 PaneHostManager가 HwndHost 주입 -->
    <DataTemplate x:Key="PaneLeafTemplate" DataType="{x:Type vm:PaneNodeViewModel}">
        <Border x:Name="FocusBorder"
                b:PaneFocusBehavior.PaneId="{Binding PaneId}"
                BorderThickness="0">
            <ContentPresenter x:Name="HostPresenter"
                              b:PaneHostBehavior.PaneId="{Binding PaneId}"
                              b:PaneHostBehavior.SessionId="{Binding SessionId}"/>
        </Border>
    </DataTemplate>

    <!-- Branch: Grid + GridSplitter + Left/Right ContentPresenter (재귀) -->
    <DataTemplate x:Key="PaneBranchTemplate" DataType="{x:Type vm:PaneNodeViewModel}">
        <Grid b:GridDefinitionBehavior.SplitDirection="{Binding SplitDirection}"
              b:GridDefinitionBehavior.Ratio="{Binding Ratio, Mode=TwoWay}">
            <!-- Left child -->
            <ContentPresenter Content="{Binding Left}"
                              b:GridPositionBehavior.Index="0"
                              b:GridPositionBehavior.IsHorizontal="{Binding SplitDirection}"/>
            <!-- GridSplitter -->
            <GridSplitter b:GridPositionBehavior.Index="1"
                          b:GridPositionBehavior.IsHorizontal="{Binding SplitDirection}"
                          b:GridSplitterRatioBehavior.Ratio="{Binding Ratio, Mode=TwoWay}"
                          Background="{StaticResource DividerColor}"
                          HorizontalAlignment="Stretch"
                          VerticalAlignment="Stretch"/>
            <!-- Right child -->
            <ContentPresenter Content="{Binding Right}"
                              b:GridPositionBehavior.Index="2"
                              b:GridPositionBehavior.IsHorizontal="{Binding SplitDirection}"/>
        </Grid>
    </DataTemplate>

    <!-- DataTemplateSelector 없이 IsLeaf 기반 선택 -->
    <DataTemplate DataType="{x:Type vm:PaneNodeViewModel}">
        <ContentPresenter>
            <ContentPresenter.ContentTemplate>
                <MultiBinding>
                    <!-- PaneTemplateSelector가 IsLeaf 기반으로 분기 -->
                </MultiBinding>
            </ContentPresenter.ContentTemplate>
        </ContentPresenter>
    </DataTemplate>

</ResourceDictionary>
```

**실제 분기 전략 — DataTemplateSelector:**

DataTemplate의 `DataType`만으로는 동일 타입(PaneNodeViewModel)의 Leaf/Branch를 구분할 수 없다. WPF의 표준 패턴인 `DataTemplateSelector`를 사용한다.

**위치:** `src/GhostWin.App/Selectors/PaneTemplateSelector.cs`

```csharp
using System.Windows;
using System.Windows.Controls;
using GhostWin.App.ViewModels;

namespace GhostWin.App.Selectors;

public class PaneTemplateSelector : DataTemplateSelector
{
    public DataTemplate? LeafTemplate { get; set; }
    public DataTemplate? BranchTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is PaneNodeViewModel vm)
            return vm.IsLeaf ? LeafTemplate : BranchTemplate;
        return base.SelectTemplate(item, container);
    }
}
```

**설계 근거:**
- 10-agent 조사에서 8/10이 "DataTemplateSelector가 Leaf/Branch 분기의 표준 WPF 패턴"으로 합의
- IsLeaf 변경 시 ContentPresenter가 자동으로 DataTemplate을 교체하므로 코드 개입 불필요
- PaneContainerControl의 BuildElement/BuildLeaf/BuildSplitGrid 3개 메서드(L187-313, 126줄)를 완전 대체

### 3.3 GridDefinitionBehavior (Attached Behavior)

Grid.RowDefinitions/ColumnDefinitions는 DependencyProperty가 아니라 직접 바인딩이 불가능하다. Attached Behavior로 SplitDirection과 Ratio를 받아 Grid의 정의를 코드로 생성한다.

**위치:** `src/GhostWin.App/Behaviors/GridDefinitionBehavior.cs`

```csharp
using System.Windows;
using System.Windows.Controls;
using GhostWin.Core.Models;

namespace GhostWin.App.Behaviors;

public static class GridDefinitionBehavior
{
    // --- SplitDirection AP ---
    public static readonly DependencyProperty SplitDirectionProperty =
        DependencyProperty.RegisterAttached(
            "SplitDirection", typeof(SplitOrientation?), typeof(GridDefinitionBehavior),
            new PropertyMetadata(null, OnLayoutChanged));

    public static void SetSplitDirection(DependencyObject d, SplitOrientation? value)
        => d.SetValue(SplitDirectionProperty, value);
    public static SplitOrientation? GetSplitDirection(DependencyObject d)
        => (SplitOrientation?)d.GetValue(SplitDirectionProperty);

    // --- Ratio AP ---
    public static readonly DependencyProperty RatioProperty =
        DependencyProperty.RegisterAttached(
            "Ratio", typeof(double), typeof(GridDefinitionBehavior),
            new FrameworkPropertyMetadata(0.5,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnLayoutChanged));

    public static void SetRatio(DependencyObject d, double value)
        => d.SetValue(RatioProperty, value);
    public static double GetRatio(DependencyObject d)
        => (double)d.GetValue(RatioProperty);

    // --- Layout rebuild ---
    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid) return;

        var direction = GetSplitDirection(grid);
        var ratio = GetRatio(grid);
        if (direction == null) return;

        bool isH = direction == SplitOrientation.Horizontal;
        var first = new GridLength(ratio, GridUnitType.Star);
        var second = new GridLength(1.0 - ratio, GridUnitType.Star);

        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();

        if (isH)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = first });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = second });
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = first });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = second });
        }
    }
}
```

**설계 근거:**
- 10-agent 조사에서 9/10이 "Grid.RowDefinitions는 DependencyProperty가 아니므로 Attached Behavior가 유일한 XAML-compatible 해법"으로 합의
- 현재 BuildSplitGrid(L279-313)의 Grid 조립 코드 34줄을 이 behavior로 이관
- Ratio는 `BindsTwoWayByDefault`로 선언하여 GridSplitter 역전파 경로를 준비

### 3.4 PaneHostManager (HwndHost 풀링 + lifecycle)

HwndHost는 DataTemplate의 자동 생성/파괴와 충돌한다. DataTemplate이 교체될 때 WPF는 이전 DataTemplate의 시각 트리를 파괴하는데, HwndHost가 이 안에 있으면 DestroyWindowCore가 호출되어 child HWND가 파괴된다. 이를 방지하기 위해:

1. Leaf DataTemplate에는 빈 ContentPresenter만 배치
2. ContentPresenter.Loaded 이벤트에서 PaneHostManager가 HwndHost를 주입
3. DataTemplate 교체 전 Unloaded에서 HwndHost를 ContentPresenter에서 분리하여 풀에 보존

**위치:** `src/GhostWin.App/Services/PaneHostManager.cs`

```csharp
using GhostWin.App.Controls;
using GhostWin.Core.Interfaces;

namespace GhostWin.App.Services;

/// <summary>
/// HwndHost(TerminalHostControl)의 생성/재사용/파괴를 관리.
/// per-workspace 풀로 workspace 전환 시 호스트를 보존.
/// </summary>
public class PaneHostManager
{
    private readonly IEngineService _engine;
    private readonly Func<IPaneLayoutService?> _activeLayoutAccessor;

    // per-workspace 호스트 풀: workspaceId → (paneId → host)
    private readonly Dictionary<uint, Dictionary<uint, TerminalHostControl>>
        _poolByWorkspace = new();

    // 현재 활성 workspace의 호스트 (빠른 접근용 미러)
    private readonly Dictionary<uint, TerminalHostControl> _activeHosts = new();
    private uint? _activeWorkspaceId;

    public PaneHostManager(
        IEngineService engine,
        Func<IPaneLayoutService?> activeLayoutAccessor)
    {
        _engine = engine;
        _activeLayoutAccessor = activeLayoutAccessor;
    }

    public IReadOnlyDictionary<uint, TerminalHostControl> ActiveHosts => _activeHosts;

    /// <summary>
    /// Leaf의 ContentPresenter.Loaded에서 호출.
    /// paneId/sessionId로 기존 호스트를 찾거나 신규 생성 후
    /// ContentPresenter에 주입.
    /// </summary>
    public TerminalHostControl AttachHost(
        uint paneId, uint sessionId, System.Windows.Controls.ContentPresenter presenter)
    {
        var host = FindOrCreate(paneId, sessionId);
        EnsureServices(host);

        // 이전 부모에서 분리
        DetachFromParent(host);

        presenter.Content = host;
        _activeHosts[paneId] = host;
        return host;
    }

    /// <summary>
    /// Leaf의 ContentPresenter.Unloaded에서 호출.
    /// 호스트를 ContentPresenter에서 분리하여 풀에 보존.
    /// (DestroyWindowCore 방지)
    /// </summary>
    public void DetachHost(uint paneId, System.Windows.Controls.ContentPresenter presenter)
    {
        presenter.Content = null;
        // _activeHosts에서 제거하지 않음 — 풀에 보존
    }

    /// <summary>
    /// workspace 전환 시 호출. 이전 workspace의 호스트를 풀에 저장하고
    /// 새 workspace의 호스트를 복원.
    /// </summary>
    public void SwitchWorkspace(uint? oldId, uint newId)
    {
        // 이전 workspace 저장
        if (oldId is { } prevId)
        {
            var pool = GetPool(prevId);
            pool.Clear();
            foreach (var kv in _activeHosts) pool[kv.Key] = kv.Value;
        }

        // 새 workspace 복원
        _activeHosts.Clear();
        if (_poolByWorkspace.TryGetValue(newId, out var saved))
        {
            foreach (var kv in saved) _activeHosts[kv.Key] = kv.Value;
        }

        _activeWorkspaceId = newId;
    }

    /// <summary>
    /// 트리에서 빠진 orphan 호스트를 지연 파괴.
    /// PaneLayoutChangedMessage 처리 후 호출.
    /// </summary>
    public void DisposeOrphans(HashSet<uint> livePaneIds,
        System.Windows.Threading.Dispatcher dispatcher)
    {
        var orphans = _activeHosts
            .Where(kv => !livePaneIds.Contains(kv.Key))
            .ToList();

        foreach (var (paneId, host) in orphans)
        {
            _activeHosts.Remove(paneId);
            host.HostReady -= OnHostReady;
            host.PaneResizeRequested -= OnPaneResized;
            host.PaneClicked -= OnPaneClicked;

            var h = host;
            dispatcher.BeginInvoke(
                new Action(() => h.Dispose()),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// workspace 삭제 시 해당 풀의 모든 호스트 파괴.
    /// </summary>
    public void DisposeWorkspace(uint workspaceId,
        System.Windows.Threading.Dispatcher dispatcher)
    {
        if (!_poolByWorkspace.TryGetValue(workspaceId, out var pool)) return;
        foreach (var (_, host) in pool)
        {
            var h = host;
            dispatcher.BeginInvoke(
                new Action(() => h.Dispose()),
                System.Windows.Threading.DispatcherPriority.Background);
        }
        _poolByWorkspace.Remove(workspaceId);
    }

    // --- Private ---

    private TerminalHostControl FindOrCreate(uint paneId, uint sessionId)
    {
        // 1. paneId 직접 매칭
        if (_activeHosts.TryGetValue(paneId, out var byPaneId))
            return byPaneId;

        // 2. sessionId 매칭 (split 시 paneId 변경, sessionId 유지)
        foreach (var candidate in _activeHosts.Values)
        {
            if (candidate.SessionId == sessionId)
            {
                candidate.PaneId = paneId;
                return candidate;
            }
        }

        // 3. 신규 생성
        var host = new TerminalHostControl
        {
            PaneId = paneId,
            SessionId = sessionId,
        };
        host.HostReady += OnHostReady;
        host.PaneResizeRequested += OnPaneResized;
        host.PaneClicked += OnPaneClicked;
        return host;
    }

    private static void DetachFromParent(TerminalHostControl host)
    {
        if (host.Parent is System.Windows.Controls.Border border)
            border.Child = null;
        else if (host.Parent is System.Windows.Controls.ContentPresenter cp)
            cp.Content = null;
    }

    private void EnsureServices(TerminalHostControl host)
    {
        host._engine ??= _engine;

        if (host._selectionService == null && host._engine != null)
        {
            var hostRef = host;
            host._selectionService = new GhostWin.Services.SelectionService(
                host._engine,
                (_, range) =>
                {
                    var paneId = hostRef.PaneId;
                    hostRef.Dispatcher.BeginInvoke(() =>
                    {
                        if (hostRef.ChildHwnd != IntPtr.Zero)
                            hostRef.RaiseSelectionChanged(
                                new SelectionChangedEventArgs(paneId, range));
                    });
                },
                GhostWin.Interop.Win32.NativeMethods.GetDoubleClickTime());
        }
    }

    private void OnHostReady(object? sender, HostReadyEventArgs e)
        => _activeLayoutAccessor()?.OnHostReady(e.PaneId, e.Hwnd, e.WidthPx, e.HeightPx);

    private void OnPaneResized(object? sender, PaneResizeEventArgs e)
        => _activeLayoutAccessor()?.OnPaneResized(e.PaneId, e.WidthPx, e.HeightPx);

    private void OnPaneClicked(object? sender, PaneClickedEventArgs e)
        => _activeLayoutAccessor()?.SetFocused(e.PaneId);

    private Dictionary<uint, TerminalHostControl> GetPool(uint workspaceId)
    {
        if (!_poolByWorkspace.TryGetValue(workspaceId, out var pool))
        {
            pool = new Dictionary<uint, TerminalHostControl>();
            _poolByWorkspace[workspaceId] = pool;
        }
        return pool;
    }
}
```

**설계 근거:**
- 현재 PaneContainerControl의 L36-39 (`_hostsByWorkspace`, `_hostControls`)와 L138-184 (workspace cache 관리), L160-177 (orphan dispose), L212-275 (FindOrCreate + EnsureServices) 총 ~140줄을 PaneHostManager로 이관
- 10-agent 조사에서 10/10이 "HwndHost를 DataTemplate 안에 직접 배치하면 DataTemplate 교체 시 DestroyWindowCore가 호출되어 child HWND가 파괴된다"를 경고
- 해법: Leaf DataTemplate에 빈 ContentPresenter → Loaded 이벤트에서 PaneHostManager.AttachHost가 HwndHost를 주입. Unloaded에서 DetachHost로 분리하여 풀에 보존

### 3.5 PaneHostBehavior (Attached Behavior — 호스트 주입 트리거)

Leaf DataTemplate의 ContentPresenter에 붙어서, Loaded/Unloaded 시 PaneHostManager를 호출하는 Attached Behavior.

**위치:** `src/GhostWin.App/Behaviors/PaneHostBehavior.cs`

```csharp
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.DependencyInjection;
using GhostWin.App.Services;

namespace GhostWin.App.Behaviors;

public static class PaneHostBehavior
{
    public static readonly DependencyProperty PaneIdProperty =
        DependencyProperty.RegisterAttached(
            "PaneId", typeof(uint), typeof(PaneHostBehavior),
            new PropertyMetadata(0u, OnPaneIdChanged));

    public static void SetPaneId(DependencyObject d, uint value)
        => d.SetValue(PaneIdProperty, value);
    public static uint GetPaneId(DependencyObject d)
        => (uint)d.GetValue(PaneIdProperty);

    public static readonly DependencyProperty SessionIdProperty =
        DependencyProperty.RegisterAttached(
            "SessionId", typeof(uint?), typeof(PaneHostBehavior),
            new PropertyMetadata(null));

    public static void SetSessionId(DependencyObject d, uint? value)
        => d.SetValue(SessionIdProperty, value);
    public static uint? GetSessionId(DependencyObject d)
        => (uint?)d.GetValue(SessionIdProperty);

    private static void OnPaneIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentPresenter presenter) return;

        // 이벤트 핸들러 중복 방지
        presenter.Loaded -= OnPresenterLoaded;
        presenter.Unloaded -= OnPresenterUnloaded;
        presenter.Loaded += OnPresenterLoaded;
        presenter.Unloaded += OnPresenterUnloaded;
    }

    private static void OnPresenterLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ContentPresenter presenter) return;
        var paneId = GetPaneId(presenter);
        var sessionId = GetSessionId(presenter) ?? 0;
        if (paneId == 0) return;

        var manager = Ioc.Default.GetService<PaneHostManager>();
        manager?.AttachHost(paneId, sessionId, presenter);
    }

    private static void OnPresenterUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ContentPresenter presenter) return;
        var paneId = GetPaneId(presenter);

        var manager = Ioc.Default.GetService<PaneHostManager>();
        manager?.DetachHost(paneId, presenter);
    }
}
```

### 3.6 PaneFocusBehavior (Attached Property)

각 Leaf의 Border가 자체적으로 포커스 시각 효과를 처리. 현재 UpdateFocusVisuals(L359-376)의 중앙 집중 순회를 제거.

**위치:** `src/GhostWin.App/Behaviors/PaneFocusBehavior.cs`

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GhostWin.App.Behaviors;

public static class PaneFocusBehavior
{
    private static SolidColorBrush? _accentBrush;
    private static SolidColorBrush AccentBrush =>
        _accentBrush ??= (SolidColorBrush)Application.Current.Resources["AccentColor"];

    // --- PaneId AP ---
    public static readonly DependencyProperty PaneIdProperty =
        DependencyProperty.RegisterAttached(
            "PaneId", typeof(uint), typeof(PaneFocusBehavior),
            new PropertyMetadata(0u));

    public static void SetPaneId(DependencyObject d, uint value)
        => d.SetValue(PaneIdProperty, value);
    public static uint GetPaneId(DependencyObject d)
        => (uint)d.GetValue(PaneIdProperty);

    // --- IsFocused AP (PaneContainerControl이 설정) ---
    public static readonly DependencyProperty IsFocusedProperty =
        DependencyProperty.RegisterAttached(
            "IsFocused", typeof(bool), typeof(PaneFocusBehavior),
            new PropertyMetadata(false, OnIsFocusedChanged));

    public static void SetIsFocused(DependencyObject d, bool value)
        => d.SetValue(IsFocusedProperty, value);
    public static bool GetIsFocused(DependencyObject d)
        => (bool)d.GetValue(IsFocusedProperty);

    private static void OnIsFocusedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;
        bool isFocused = (bool)e.NewValue;
        border.BorderBrush = isFocused ? AccentBrush : Brushes.Transparent;
        border.BorderThickness = isFocused ? new Thickness(1) : new Thickness(0);
    }
}
```

**설계 근거:**
- 현재 UpdateFocusVisuals(L359-376)는 모든 `_hostControls`를 순회하며 Parent Border를 찾아 속성 변경. O(N) 순회이며 host-border 구조 가정에 의존
- Attached Property로 변경하면 각 Border가 IsFocused 변경에 독립적으로 반응. PaneContainerControl은 FocusedPaneId만 갱신하면 됨

### 3.7 GridSplitter Ratio 역전파

현재 구현에는 GridSplitter 드래그 후 PaneNode.Ratio가 갱신되지 않는 버그가 있다. GridSplitter의 DragCompleted 이벤트에서 실제 Grid 크기를 읽어 Ratio를 역산하는 Attached Behavior.

**위치:** `src/GhostWin.App/Behaviors/GridSplitterRatioBehavior.cs`

```csharp
using System.Windows;
using System.Windows.Controls;

namespace GhostWin.App.Behaviors;

public static class GridSplitterRatioBehavior
{
    public static readonly DependencyProperty RatioProperty =
        DependencyProperty.RegisterAttached(
            "Ratio", typeof(double), typeof(GridSplitterRatioBehavior),
            new FrameworkPropertyMetadata(0.5,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static void SetRatio(DependencyObject d, double value)
        => d.SetValue(RatioProperty, value);
    public static double GetRatio(DependencyObject d)
        => (double)d.GetValue(RatioProperty);

    // GridSplitter에 이 behavior를 붙이면 DragCompleted에서 Ratio를 역산
    // (DragCompleted는 GridSplitter의 기본 이벤트)
    // → XAML에서 EventTrigger 대신 코드 연결
    // 실제로는 GridDefinitionBehavior.OnLayoutChanged에서 Grid의
    // Star 비율 변경을 감지하여 역전파하는 방식이 더 안정적.
    // GridSplitter가 Grid의 RowDefinitions/ColumnDefinitions를
    // 직접 수정하므로, SizeChanged 시점에 실제 비율을 읽으면 된다.
}
```

**구현 전략 (GridDefinitionBehavior 내 통합):**

GridSplitter는 드래그 시 Grid의 RowDefinition.Height 또는 ColumnDefinition.Width를 직접 변경한다. GridDefinitionBehavior에서 Grid.SizeChanged 이벤트를 구독하여, Star 값의 비율이 Ratio와 다르면 역전파한다.

```csharp
// GridDefinitionBehavior에 추가
private static void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
{
    if (sender is not Grid grid) return;
    var direction = GetSplitDirection(grid);
    if (direction == null) return;

    bool isH = direction == SplitOrientation.Horizontal;
    double first, second;

    if (isH && grid.RowDefinitions.Count >= 3)
    {
        first = grid.RowDefinitions[0].ActualHeight;
        second = grid.RowDefinitions[2].ActualHeight;
    }
    else if (!isH && grid.ColumnDefinitions.Count >= 3)
    {
        first = grid.ColumnDefinitions[0].ActualWidth;
        second = grid.ColumnDefinitions[2].ActualWidth;
    }
    else return;

    double total = first + second;
    if (total < 1.0) return;

    double newRatio = first / total;
    double currentRatio = GetRatio(grid);

    // 부동소수점 오차 무시 (0.001 이내)
    if (Math.Abs(newRatio - currentRatio) > 0.001)
        SetRatio(grid, newRatio); // TwoWay 바인딩으로 VM → PaneNode까지 전파
}
```

### 3.8 PaneContainerControl (최종 — 얇은 coordinator)

**위치:** `src/GhostWin.App/Controls/PaneContainerControl.cs` (기존 파일 교체)

```csharp
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.App.Behaviors;
using GhostWin.App.Services;
using GhostWin.App.ViewModels;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;

namespace GhostWin.App.Controls;

/// <summary>
/// Pane 트리의 최상위 coordinator.
/// - Messenger 구독 (Layout/Focus/Workspace)
/// - PaneNodeViewModel 트리를 Content에 바인딩
/// - PaneHostManager에 호스트 lifecycle 위임
/// - 포커스 시각 효과를 PaneFocusBehavior에 위임
/// </summary>
public class PaneContainerControl : ContentControl,
    IRecipient<PaneLayoutChangedMessage>,
    IRecipient<PaneFocusChangedMessage>,
    IRecipient<WorkspaceActivatedMessage>
{
    private IWorkspaceService? _workspaces;
    private PaneHostManager? _hostManager;
    private PaneNodeViewModel? _rootVm;
    private uint? _activeWorkspaceId;
    private uint? _focusedPaneId;

    public PaneContainerControl()
    {
        // HC-4 유지: Unloaded에서 메신저 해제
        Unloaded += (_, _) => WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    public void Initialize(IWorkspaceService workspaces, PaneHostManager hostManager)
    {
        _workspaces = workspaces;
        _hostManager = hostManager;
        // HC-4: 즉시 구독 (Loaded로 지연 금지)
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    public void Receive(WorkspaceActivatedMessage msg)
        => SwitchToWorkspace(msg.Value);

    public void Receive(PaneLayoutChangedMessage msg)
    {
        var activeRoot = _workspaces?.ActivePaneLayout?.Root;
        if (activeRoot != null)
            RebuildTree(activeRoot);
    }

    public void Receive(PaneFocusChangedMessage msg)
    {
        _focusedPaneId = msg.Value.PaneId;
        UpdateFocusedPaneId();
    }

    private void SwitchToWorkspace(uint workspaceId)
    {
        if (_workspaces == null || _hostManager == null) return;
        if (_activeWorkspaceId == workspaceId) return;

        _hostManager.SwitchWorkspace(_activeWorkspaceId, workspaceId);
        _activeWorkspaceId = workspaceId;

        var paneLayout = _workspaces.GetPaneLayout(workspaceId);
        _focusedPaneId = paneLayout?.FocusedPaneId;

        var root = paneLayout?.Root;
        if (root != null)
        {
            _rootVm = PaneNodeViewModel.FromNode(root, _rootVm);
            Content = _rootVm;
        }
        else
        {
            _rootVm = null;
            Content = null;
        }

        UpdateFocusedPaneId();
    }

    private void RebuildTree(IReadOnlyPaneNode root)
    {
        _rootVm = PaneNodeViewModel.FromNode(root, _rootVm);
        Content = _rootVm;

        // Orphan 정리
        var livePaneIds = CollectLeafIds(_rootVm);
        _hostManager?.DisposeOrphans(livePaneIds, Dispatcher);

        UpdateFocusedPaneId();
    }

    private void UpdateFocusedPaneId()
    {
        // PaneFocusBehavior를 사용하는 각 Border가 자체적으로 시각 처리.
        // 여기서는 FocusedPaneId DP만 갱신하면 됨.
        // (DataTemplate 내 Border의 IsFocused 바인딩이 반응)
        //
        // 구현: 시각 트리를 한번 순회하여 각 Border의 IsFocused를 설정.
        // PaneFocusBehavior.IsFocused는 Border마다 독립 처리.
        if (_rootVm == null) return;
        SetFocusRecursive(_rootVm, _focusedPaneId);
    }

    private static void SetFocusRecursive(PaneNodeViewModel vm, uint? focusedId)
    {
        // VM에 IsFocused 속성을 두어 Binding으로 처리하는 방식
        // (시각 트리 순회 대신 데이터 바인딩 활용)
        if (vm.IsLeaf)
        {
            vm.IsFocused = vm.PaneId == focusedId;
            return;
        }
        if (vm.Left != null) SetFocusRecursive(vm.Left, focusedId);
        if (vm.Right != null) SetFocusRecursive(vm.Right, focusedId);
    }

    private static HashSet<uint> CollectLeafIds(PaneNodeViewModel vm)
    {
        var ids = new HashSet<uint>();
        CollectLeafIdsRecursive(vm, ids);
        return ids;
    }

    private static void CollectLeafIdsRecursive(PaneNodeViewModel vm, HashSet<uint> ids)
    {
        if (vm.IsLeaf) { ids.Add(vm.PaneId); return; }
        if (vm.Left != null) CollectLeafIdsRecursive(vm.Left, ids);
        if (vm.Right != null) CollectLeafIdsRecursive(vm.Right, ids);
    }
}
```

**줄 수 추정:** ~120줄 (목표 200줄 이하 달성)

**참고:** PaneNodeViewModel에 `IsFocused` 속성 추가 필요:

```csharp
// PaneNodeViewModel에 추가
[ObservableProperty] private bool _isFocused;
```

Leaf DataTemplate의 Border 바인딩:

```xml
<Border b:PaneFocusBehavior.IsFocused="{Binding IsFocused}" ...>
```

## 4. 파일 변경 목록

### 신규 파일

| 파일 | 프로젝트 | 예상 줄 수 | 설명 |
|------|----------|-----------|------|
| `src/GhostWin.App/ViewModels/PaneNodeViewModel.cs` | App | ~60 | PaneNode ObservableObject 래퍼 |
| `src/GhostWin.App/Resources/PaneTemplates.xaml` | App | ~50 | Leaf/Branch DataTemplate |
| `src/GhostWin.App/Selectors/PaneTemplateSelector.cs` | App | ~25 | IsLeaf 기반 DataTemplate 선택 |
| `src/GhostWin.App/Behaviors/GridDefinitionBehavior.cs` | App | ~80 | Grid Row/Column 정의 + Ratio 역전파 |
| `src/GhostWin.App/Behaviors/PaneHostBehavior.cs` | App | ~65 | Loaded/Unloaded 호스트 주입 트리거 |
| `src/GhostWin.App/Behaviors/PaneFocusBehavior.cs` | App | ~45 | IsFocused → Border 시각 처리 |
| `src/GhostWin.App/Behaviors/GridPositionBehavior.cs` | App | ~30 | Grid.SetRow/SetColumn Attached |
| `src/GhostWin.App/Services/PaneHostManager.cs` | App | ~180 | HwndHost 풀링 + lifecycle |

### 수정 파일

| 파일 | 변경 내용 |
|------|----------|
| `src/GhostWin.App/Controls/PaneContainerControl.cs` | 377줄 → ~120줄 전면 교체 |
| `src/GhostWin.App/MainWindow.xaml` | PaneContainer에 ContentTemplateSelector 설정 |
| `src/GhostWin.App/MainWindow.xaml.cs` | Initialize 호출에 PaneHostManager 전달 |
| `src/GhostWin.App/App.xaml` | PaneTemplates.xaml MergedDictionary 추가 |
| `src/GhostWin.App/App.xaml.cs` (또는 DI 등록 위치) | PaneHostManager DI 등록 |

### 삭제 코드 (PaneContainerControl.cs 내)

| 메서드 | 줄 범위 | 대체 |
|--------|---------|------|
| `_hostsByWorkspace`, `_hostControls` | L36-39 | PaneHostManager 내부 |
| `GetHostsForWorkspace` | L138-146 | PaneHostManager.GetPool |
| `BuildGrid` | L148-158 | RebuildTree (VM 기반) |
| `DisposeOrphanedHosts` | L160-177 | PaneHostManager.DisposeOrphans |
| `SyncWorkspaceCache` | L179-185 | PaneHostManager.SwitchWorkspace |
| `BuildElement` | L187-193 | DataTemplateSelector |
| `BuildLeaf` | L197-209 | PaneHostBehavior + PaneHostManager |
| `FindOrCreateHost` | L212-246 | PaneHostManager.FindOrCreate |
| `DetachFromParent` | L248-252 | PaneHostManager.DetachFromParent |
| `EnsureServices` | L254-275 | PaneHostManager.EnsureServices |
| `BuildSplitGrid` | L279-313 | GridDefinitionBehavior + XAML |
| `CreateSplitter` | L315-327 | XAML 내 GridSplitter |
| `SetPosition` | L329-335 | GridPositionBehavior |
| `UpdateFocusVisuals` | L359-376 | PaneFocusBehavior |

## 5. 구현 순서 (의존성 기반)

```
Phase 1 (기반, 독립)
  |-- [1a] PaneNodeViewModel (Core 모델 래퍼, 의존성 없음)
  |-- [1b] PaneTemplateSelector (DataTemplate 선택, 의존성 없음)
  |-- [1c] PaneFocusBehavior (Attached Property, 의존성 없음)
  |-- [1d] GridPositionBehavior (Grid.SetRow/SetColumn, 의존성 없음)

Phase 2 (Behavior, Phase 1에 의존)
  |-- [2a] GridDefinitionBehavior (1a의 SplitOrientation 사용)
  |-- [2b] PaneHostManager (1a의 PaneNodeViewModel 사용하지 않으나
  |         TerminalHostControl + IEngineService 의존)
  |-- [2c] PaneHostBehavior (2b의 PaneHostManager 사용)

Phase 3 (XAML, Phase 1+2에 의존)
  |-- [3a] PaneTemplates.xaml (1a, 1b, 1c, 1d, 2a, 2c 전부 사용)

Phase 4 (통합, Phase 1+2+3에 의존)
  |-- [4a] PaneContainerControl 교체 (전체 조립)
  |-- [4b] MainWindow 수정 (Initialize 시그니처 변경)
  |-- [4c] App.xaml 수정 (MergedDictionary + DI)

Phase 5 (검증)
  |-- [5a] 단일 pane 렌더링 (기존 기능 회귀 확인)
  |-- [5b] Alt+V/H split 검증
  |-- [5c] Ctrl+Shift+W pane close
  |-- [5d] Workspace 전환 (sidebar 클릭)
  |-- [5e] GridSplitter 드래그 → Ratio 역전파 검증
  |-- [5f] 포커스 시각 효과 (마우스/키보드)
```

## 6. 위험 요소 + 완화 방안

### 6.1 HwndHost와 DataTemplate 충돌

**위험:** WPF DataTemplate이 교체되면 이전 시각 트리의 Dispose가 호출되어 HwndHost의 DestroyWindowCore가 실행됨. child HWND가 파괴되면 DX11 swapchain이 무효화.

**완화:**
- Leaf DataTemplate에는 빈 ContentPresenter만 배치 (HwndHost를 DataTemplate 시각 트리에 포함시키지 않음)
- ContentPresenter.Loaded에서 PaneHostManager.AttachHost가 HwndHost를 ContentPresenter.Content로 주입
- ContentPresenter.Unloaded에서 DetachHost가 Content를 null로 설정하여 HwndHost를 풀에 보존
- HwndHost는 PaneHostManager가 명시적으로 Dispose할 때만 파괴됨

**검증:** Phase 5b의 split 테스트에서 기존 pane의 child HWND가 유지되는지 확인 (RenderDiag `buildwindow-enter` 로그에서 동일 instance_hash가 2회 나오지 않아야 함)

### 6.2 HC-4 순서 제약 유지

**위험:** first-pane-render-failure의 핵심 수정인 HC-4 (Initialize가 Loaded 이벤트가 아닌 즉시 메신저 구독)가 재설계 과정에서 깨질 수 있음.

**완화:**
- PaneContainerControl.Initialize 시그니처가 `(IWorkspaceService, PaneHostManager)`로 변경되지만, WeakReferenceMessenger.Default.RegisterAll(this) 호출은 동일하게 유지
- MainWindow.InitializeRenderer의 호출 순서 변경 없음: `PaneContainer.Initialize` → `RenderInit` → `RenderStart` → `CreateWorkspace`
- 주석으로 HC-4 제약을 명시적으로 문서화 (기존 L62-74 주석 유지)

**검증:** Phase 5a의 단일 pane 렌더링에서 첫 workspace의 터미널이 정상 표시되는지 확인

### 6.3 Workspace 전환 시 호스트 보존

**위험:** workspace 전환 시 DataTemplate이 전부 교체되면서 모든 HwndHost의 Unloaded가 발생. PaneHostBehavior.OnPresenterUnloaded가 호출되지만, 새 workspace의 Loaded보다 먼저 처리되어야 함.

**완화:**
- PaneHostManager.SwitchWorkspace를 PaneContainerControl.SwitchToWorkspace의 **첫 번째** 동작으로 호출 (Content 변경 전)
- SwitchWorkspace가 이전 workspace의 호스트를 풀에 저장한 후, Content를 새 root VM으로 변경
- 새 root VM의 Leaf DataTemplate Loaded에서 PaneHostBehavior가 PaneHostManager.AttachHost를 호출하면, 풀에 저장된 호스트가 반환됨

**검증:** Phase 5d의 workspace 전환에서 Ctrl+T로 새 workspace 생성 → sidebar로 이전 workspace 전환 → 터미널 내용이 보존되는지 확인

### 6.4 ContentPresenter.Loaded 타이밍

**위험:** ContentPresenter.Loaded는 WPF 레이아웃 패스에서 발생. BuildWindowCore의 HostReady Dispatcher.BeginInvoke(Normal)와의 순서가 보장되지 않을 수 있음.

**완화:**
- 기존 구조에서도 BuildWindowCore는 Dispatcher.BeginInvoke로 HostReady를 발화하며, 구독자는 BuildElement에서 동기적으로 등록됨
- 새 구조에서는 PaneHostBehavior.OnPresenterLoaded → PaneHostManager.AttachHost → `host.HostReady += OnHostReady`가 BuildWindowCore 실행 전에 완료되어야 함
- ContentPresenter.Loaded는 HwndHost가 시각 트리에 추가된 후, BuildWindowCore가 호출되기 전에 발생 (WPF 내부 순서: AddChild → Loaded → BuildWindowCore). 그러나 AttachHost에서 Content를 설정하면 HwndHost가 시각 트리에 추가되므로, AttachHost 내에서 이벤트 구독이 HwndHost 시각 트리 추가 전에 완료됨
- **추가 안전장치:** PaneHostManager.AttachHost에서 이벤트 구독을 Content 설정 **전에** 수행

### 6.5 Ratio 역전파의 무한 루프

**위험:** GridSplitter 드래그 → SizeChanged → SetRatio → OnLayoutChanged → Grid 재구성 → SizeChanged... 무한 루프.

**완화:**
- OnLayoutChanged에서 Grid Row/ColumnDefinitions를 재구성할 때, 기존 Star 값과 새 값이 동일하면 (0.001 오차 이내) 재구성을 건너뜀
- SizeChanged 핸들러에서도 newRatio와 currentRatio의 차이가 0.001 이내면 SetRatio를 호출하지 않음
- 두 지점의 이중 가드로 루프 방지

## 7. 수락 기준

| # | 기준 | 검증 방법 |
|---|------|----------|
| AC-1 | 기존 기능 100% 동작: split, close, focus, workspace 전환 | Phase 5 수동 테스트 (5a~5f) |
| AC-2 | PaneContainerControl 200줄 이하 | `wc -l PaneContainerControl.cs` |
| AC-3 | BuildElement/BuildGrid/BuildSplitGrid 완전 제거 | `grep -r "BuildElement\|BuildGrid\|BuildSplitGrid" src/` |
| AC-4 | Grid 구조가 XAML(DataTemplate)에만 존재 | `new Grid()` 검색 결과 0건 (PaneContainerControl 내) |
| AC-5 | HwndHost가 DataTemplate 파괴로 인해 의도치 않게 Dispose되지 않음 | split/close 반복 10회, RenderDiag 로그에서 buildwindow-enter 중복 없음 |
| AC-6 | GridSplitter 드래그 후 PaneNode.Ratio가 갱신됨 | GridSplitter 드래그 → workspace 전환 → 원래 workspace 복귀 시 비율 유지 |
| AC-7 | HC-4 순서 제약 유지 (첫 pane 렌더링 정상) | 앱 시작 시 첫 workspace의 터미널이 빈 화면 없이 표시 |
| AC-8 | Workspace 전환 시 호스트 보존 | Ctrl+T → 새 workspace → sidebar로 이전 workspace → 터미널 내용 보존 |
