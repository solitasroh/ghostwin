using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using GhostWin.App.Automation;
using GhostWin.App.Input;
using GhostWin.App.Services;
using GhostWin.App.ViewModels;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.App.Controls;

/// <summary>
/// View for the active workspace's pane tree. The structural input is a bound
/// <see cref="TerminalPaneLayoutSnapshot"/> projected by the view model; this
/// control owns only WPF/HwndHost composition and per-workspace host caches.
/// </summary>
public class PaneContainerControl : ContentControl
{
    private bool _isInitialized;
    private uint? _activeWorkspaceId;
    private uint? _focusedPaneId;
    private ISessionManager? _sessionManager;
    private IEngineService? _engine;
    private ITerminalSurfaceCoordinator? _surfaceCoordinator;
    private ITerminalPaneScrollService? _scrollService;
    private ITerminalPaneCommandService? _paneCommands;
    private ITerminalInputRouter? _inputRouter;

    // Per-workspace host caches: workspaceId → (paneId → host).
    private readonly Dictionary<uint, Dictionary<uint, TerminalHostControl>> _hostsByWorkspace = new();

    // The active workspace's host dictionary (mirror of _hostsByWorkspace[_activeWorkspaceId]).
    private readonly Dictionary<uint, TerminalHostControl> _hostControls = new();

    // M-16-C Phase B2: ScrollBar per pane (only visible in active workspace).
    private readonly Dictionary<uint, ScrollBar> _scrollBars = new();
    // Suppress feedback while we update Value programmatically from the timer.
    private readonly HashSet<uint> _scrollSuppressed = new();
    private DispatcherTimer? _scrollPollTimer;
    private string? _layoutShapeKey;
    private TerminalPaneLayoutSnapshot? _pendingLayout;
    private const double FocusFrameThickness = 1.0;

    private sealed record FocusFrameEdge(uint PaneId);

    public static readonly DependencyProperty LayoutProperty =
        DependencyProperty.Register(
            nameof(Layout),
            typeof(TerminalPaneLayoutSnapshot),
            typeof(PaneContainerControl),
            new PropertyMetadata(null, OnLayoutChanged));

    public TerminalPaneLayoutSnapshot? Layout
    {
        get => (TerminalPaneLayoutSnapshot?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    private static void OnLayoutChanged(
        DependencyObject d,
        DependencyPropertyChangedEventArgs e)
    {
        ((PaneContainerControl)d).ApplyLayout((TerminalPaneLayoutSnapshot?)e.NewValue);
    }

    public static readonly DependencyProperty ClosedWorkspaceIdProperty =
        DependencyProperty.Register(
            nameof(ClosedWorkspaceId),
            typeof(uint?),
            typeof(PaneContainerControl),
            new PropertyMetadata(null, OnClosedWorkspaceIdChanged));

    public uint? ClosedWorkspaceId
    {
        get => (uint?)GetValue(ClosedWorkspaceIdProperty);
        set => SetValue(ClosedWorkspaceIdProperty, value);
    }

    private static void OnClosedWorkspaceIdChanged(
        DependencyObject d,
        DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is uint workspaceId)
            ((PaneContainerControl)d).ReleaseWorkspaceHosts(workspaceId);
    }

    // M-10c: Selection overlay is now rendered by DX11 engine (shading_type=2
    // semi-transparent quads), bypassing the HwndHost Airspace limitation.
    // WPF Canvas overlay removed — see gw_session_set_selection C API.

    /// <summary>
    /// 현재 포커스된 pane의 TerminalHostControl 반환 (클립보드 등에서 사용).
    /// </summary>
    public TerminalHostControl? GetFocusedHost()
    {
        if (_focusedPaneId is { } id && _hostControls.TryGetValue(id, out var host))
            return host;
        // 포커스된 pane이 없으면 첫 번째 호스트 반환
        foreach (var kv in _hostControls)
            return kv.Value;
        return null;
    }

    public PaneContainerControl()
    {
        // Initialize() wires runtime services synchronously before the first
        // workspace is created. This control no longer subscribes to messenger
        // messages directly; it receives a VM-projected Layout snapshot through
        // dependency-property binding.
        Unloaded += (_, _) =>
        {
            // M-16-C Phase B2: stop the scrollback poll timer.
            StopScrollPolling();
        };
    }

    public void StopScrollPolling()
    {
        if (_scrollPollTimer == null)
            return;

        _scrollPollTimer.Stop();
        _scrollPollTimer.Tick -= OnScrollPollTick;
        _scrollPollTimer = null;
    }

    public void Initialize(
        ISessionManager sessionManager,
        IEngineService engine,
        ITerminalSurfaceCoordinator surfaceCoordinator,
        ITerminalPaneScrollService scrollService,
        ITerminalPaneCommandService paneCommands,
        ITerminalInputRouter inputRouter)
    {
        _isInitialized = true;
        _sessionManager = sessionManager;
        _engine = engine;
        _surfaceCoordinator = surfaceCoordinator;
        _scrollService = scrollService;
        _paneCommands = paneCommands;
        _inputRouter = inputRouter;
        // M-16-C Phase B2: poll scrollback geometry at ~10 Hz. ghostty does not
        // raise an event when scrollback or viewport position changes, so a
        // short DispatcherTimer is the simplest source of truth for the bar.
        if (_scrollPollTimer == null)
        {
            _scrollPollTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100),
            };
            _scrollPollTimer.Tick += OnScrollPollTick;
            _scrollPollTimer.Start();
        }

        var pending = _pendingLayout ?? Layout;
        _pendingLayout = null;
        if (pending != null)
            ApplyLayout(pending);
    }

    // Note: AdoptInitialHost was removed in first-pane-render-failure Option B.
    // The initial pane is now created by BuildElement via the bound Layout
    // snapshot -> BuildGrid path, using the same code path as split panes.
    // MainWindow no longer owns any host lifecycle; PaneContainerControl is
    // the single owner.

    private void ApplyLayout(TerminalPaneLayoutSnapshot? layout)
    {
        if (layout != null && !_isInitialized)
        {
            _pendingLayout = layout;
            return;
        }

        if (layout == null)
        {
            _pendingLayout = null;
            SaveActiveWorkspaceHosts();
            _activeWorkspaceId = null;
            _focusedPaneId = null;
            _layoutShapeKey = null;
            _hostControls.Clear();
            Content = null;
            return;
        }

        if (_activeWorkspaceId != layout.WorkspaceId)
        {
            SaveActiveWorkspaceHosts();

            _activeWorkspaceId = layout.WorkspaceId;

            // Restore new workspace's hosts.
            _hostControls.Clear();
            if (_hostsByWorkspace.TryGetValue(layout.WorkspaceId, out var saved))
            {
                foreach (var kv in saved) _hostControls[kv.Key] = kv.Value;
            }

            _layoutShapeKey = null;
        }

        _focusedPaneId = layout.FocusedPaneId;

        var nextShapeKey = BuildShapeKey(layout.Root);
        if (_layoutShapeKey == nextShapeKey && Content != null)
        {
            UpdateFocusVisuals();
            ReassertFocusedPane(layout);
            return;
        }

        _layoutShapeKey = nextShapeKey;
        BuildGrid(layout.Root);
        ReassertFocusedPane(layout);
    }

    private void ReassertFocusedPane(TerminalPaneLayoutSnapshot layout)
    {
        if (layout.FocusedPaneId is { } paneId)
            _surfaceCoordinator?.FocusPane(layout.WorkspaceId, paneId);
    }

    private void SaveActiveWorkspaceHosts()
    {
        if (_activeWorkspaceId is not { } prevId) return;

        var prevHosts = GetHostsForWorkspace(prevId);
        prevHosts.Clear();
        foreach (var kv in _hostControls) prevHosts[kv.Key] = kv.Value;
    }

    private void ReleaseWorkspaceHosts(uint workspaceId)
    {
        var hostsToDispose = new HashSet<TerminalHostControl>();

        if (_activeWorkspaceId == workspaceId)
        {
            foreach (var host in _hostControls.Values)
                hostsToDispose.Add(host);

            _hostControls.Clear();
            ClearScrollBarState();
            _activeWorkspaceId = null;
            _focusedPaneId = null;
            _layoutShapeKey = null;
            _paneCommands?.ClearZoom(workspaceId);
            Content = null;
        }

        if (_hostsByWorkspace.Remove(workspaceId, out var cachedHosts))
        {
            foreach (var host in cachedHosts.Values)
                hostsToDispose.Add(host);
        }

        foreach (var host in hostsToDispose)
            DetachAndDisposeHost(host);
    }

    private static string BuildShapeKey(TerminalPaneNodeViewModel node)
    {
        if (node.IsLeaf)
        {
            var sessionId = node.SessionId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            var surface = node.SurfaceState;
            var surfaceKey = surface == null
                ? string.Empty
                : $":{surface.Status}:{surface.SurfaceId}:{surface.Failure?.Attempt ?? 0}";
            return $"L:{node.PaneId}:{sessionId}{surfaceKey}";
        }

        var ratio = node.Ratio.ToString("R", CultureInfo.InvariantCulture);
        return $"S:{node.PaneId}:{node.SplitDirection}:{ratio}({BuildShapeKey(node.Left!)})({BuildShapeKey(node.Right!)})";
    }

    private Dictionary<uint, TerminalHostControl> GetHostsForWorkspace(uint workspaceId)
    {
        if (!_hostsByWorkspace.TryGetValue(workspaceId, out var hosts))
        {
            hosts = new Dictionary<uint, TerminalHostControl>();
            _hostsByWorkspace[workspaceId] = hosts;
        }
        return hosts;
    }

    private void BuildGrid(TerminalPaneNodeViewModel root)
    {
        // Detach events from old hosts that won't be reused
        var oldHosts = new Dictionary<uint, TerminalHostControl>(_hostControls);
        _hostControls.Clear();

        // M-16-C Phase B2: drop the previous ScrollBar map. The Scroll handler
        // is unhooked when the bar leaves the visual tree (no other strong
        // references), and BuildElement repopulates the dict for live leaves.
        ClearScrollBarState();

        // Any structural rebuild resets zoom. Split/close already clear this
        // in the command service; this keeps workspace switches and restore
        // paths on the same presentation rule.
        if (_activeWorkspaceId is { } activeWorkspaceId)
            _paneCommands?.ClearZoom(activeWorkspaceId);

        Content = BuildElement(root, oldHosts);

        // Dispose hosts no longer in the tree. Compare by host *instance*, not
        // paneId — session-based migration in BuildElement may rebind a host to
        // a new paneId, so the old paneId key is absent from _hostControls but
        // the host itself is still alive under a new key.
        //
        // Dispose is *deferred* via Dispatcher.BeginInvoke at Background priority.
        // Calling HwndHost.Dispose() synchronously here triggers DestroyWindow on
        // the child HWND while WPF still holds visual-tree references to the host
        // (the new Content was just assigned but layout/render hasn't run yet).
        // The next layout pass would then dereference the destroyed HWND, causing
        // a native access violation that the managed exception handlers cannot catch.
        // Background priority guarantees execution after Render so the host has
        // been fully unparented by the time DestroyWindowCore runs.
        var liveHosts = new HashSet<TerminalHostControl>(_hostControls.Values);
        foreach (var (_, host) in oldHosts)
        {
            if (!liveHosts.Contains(host))
                DetachAndDisposeHost(host);
        }

        // Mirror back to the per-workspace cache.
        if (_activeWorkspaceId is { } id)
        {
            var workspaceHosts = GetHostsForWorkspace(id);
            workspaceHosts.Clear();
            foreach (var kv in _hostControls) workspaceHosts[kv.Key] = kv.Value;
        }

        UpdateFocusVisuals();
    }

    private UIElement BuildElement(TerminalPaneNodeViewModel node, Dictionary<uint, TerminalHostControl> oldHosts)
    {
        if (node.IsLeaf)
        {
            // Host migration strategy: prefer reuse so the child HWND (and any
            // swap chain target bound to it) survives across BuildGrid passes.
            //
            // 1. paneId match — straight reuse (close case: surviving leaf keeps id).
            // 2. sessionId match — Split allocates new paneIds but PaneNode.Split
            //    preserves the original sessionId on oldLeaf, so the host that was
            //    displaying that session can be reparented under the new paneId.
            // 3. Otherwise — fresh host (new session from Split's newLeaf).
            TerminalHostControl? host = null;

            if (oldHosts.TryGetValue(node.PaneId, out var byPaneId))
            {
                host = byPaneId;
            }
            else if (node.SessionId is { } sessionId)
            {
                foreach (var candidate in oldHosts.Values)
                {
                    if (candidate.SessionId == sessionId)
                    {
                        host = candidate;
                        host.PaneId = node.PaneId;
                        break;
                    }
                }
            }

            if (host != null)
            {
                host.WorkspaceId = _activeWorkspaceId ?? 0;
                // Detach from previous parent before re-parenting. WPF forbids
                // a UIElement being the logical child of two parents simultaneously.
                DetachHostFromParent(host);
            }
            else
            {
                host = new TerminalHostControl
                {
                    WorkspaceId = _activeWorkspaceId ?? 0,
                    PaneId = node.PaneId,
                    SessionId = node.SessionId ?? 0,
                };
                // M-15 Stage A: expose host to UIA so the MeasurementDriver
                // can count panes after Alt+V/Alt+H splits. Metadata-only —
                // does not affect rendering or input.
                System.Windows.Automation.AutomationProperties.SetAutomationId(host, AutomationIds.LegacyTerminalHost);
                host.HostReady += OnHostReady;
                host.PaneResizeRequested += OnPaneResized;
                host.PaneClicked += OnPaneClicked;
            }
            // HwndHost keeps the low-level Win32 selection adapter; higher
            // level wheel/context commands are routed through ITerminalInputRouter.
            host._engine ??= _engine;
            host.InputRouter = _inputRouter;
            host.ForceContextMenu = _scrollService?.ForceContextMenu ?? false;
            host.SnapsToDevicePixels = true;
            host.UseLayoutRounding = true;
            host.Margin = new Thickness(0, 0, FocusFrameThickness, FocusFrameThickness);
            if (node.SessionId is { } sid)
            {
                var mouseShape = _sessionManager?.Sessions.FirstOrDefault(s => s.Id == sid)?.MouseCursorShape ?? 0;
                host.ApplyMouseCursorShape(mouseShape);
            }

            _hostControls[node.PaneId] = host;

            if (node.SurfaceState?.Status == TerminalPaneSurfaceStatus.Failed)
                return BuildFailedLeafElement(node, host);

            host.Visibility = Visibility.Visible;

            var frame = BuildHostFrame(node.PaneId, host);

            // M-16-C Phase B2: ScrollBar overlay container.
            // Layout: Grid with two columns —
            //   col 0 (*)    : 1-DIP focus frame + host (terminal)
            //   col 1 (Auto) : ScrollBar (vertical, right edge)
            var leafGrid = new Grid
            {
                Tag = node.PaneId,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
            };
            leafGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            leafGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(frame, 0);
            leafGrid.Children.Add(frame);

            var paneProbe = new Button
            {
                Width = 0,
                Height = 0,
                Focusable = false,
                IsTabStop = false,
                Tag = node.PaneId,
            };
            ApplyPaneAutomationProperties(
                paneProbe,
                node.PaneId,
                host.SessionId,
                node.IsFocused);
            Grid.SetColumn(paneProbe, 0);
            leafGrid.Children.Add(paneProbe);

            var scrollBar = new ScrollBar
            {
                Orientation = Orientation.Vertical,
                Width = 12,
                Minimum = 0,
                Maximum = 0,
                SmallChange = 1,
                LargeChange = 10,
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Stretch,
                Tag = node.PaneId,
            };
            scrollBar.Scroll += OnScrollBarScroll;
            Grid.SetColumn(scrollBar, 1);
            leafGrid.Children.Add(scrollBar);
            _scrollBars[node.PaneId] = scrollBar;

            return leafGrid;
        }

        var grid = new Grid();
        bool isHorizontal = node.SplitDirection == SplitOrientation.Horizontal;

        if (isHorizontal)
        {
            grid.RowDefinitions.Add(new RowDefinition
            { Height = new GridLength(node.Ratio, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition
            { Height = new GridLength(1.0 - node.Ratio, GridUnitType.Star) });

            var left = BuildElement(node.Left!, oldHosts);
            Grid.SetRow(left, 0);
            grid.Children.Add(left);

            var splitter = new GridSplitter
            {
                Height = 2,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            // M-16-F FR-15: imperative brush via SetResourceReference so theme
            // swap (Dark <-> Light) reaches the splitter without rebuild.
            // (feedback_setresourcereference_for_imperative_brush.md)
            splitter.SetResourceReference(Control.BackgroundProperty, "Divider.Brush");
            Grid.SetRow(splitter, 1);
            grid.Children.Add(splitter);

            var right = BuildElement(node.Right!, oldHosts);
            Grid.SetRow(right, 2);
            grid.Children.Add(right);
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            { Width = new GridLength(node.Ratio, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            { Width = new GridLength(1.0 - node.Ratio, GridUnitType.Star) });

            var left = BuildElement(node.Left!, oldHosts);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var splitter = new GridSplitter
            {
                Width = 2,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            splitter.SetResourceReference(Control.BackgroundProperty, "Divider.Brush");
            Grid.SetColumn(splitter, 1);
            grid.Children.Add(splitter);

            var right = BuildElement(node.Right!, oldHosts);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }

        return grid;
    }

    private Grid BuildHostFrame(uint paneId, TerminalHostControl host)
    {
        var frame = new Grid
        {
            Tag = paneId,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            ClipToBounds = true,
            // M-16-D D-06: pane area ContextMenu (5 items).
            ContextMenu = BuildPaneContextMenu(paneId),
        };
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FocusFrameThickness) });
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FocusFrameThickness) });
        frame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FocusFrameThickness) });
        frame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        frame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FocusFrameThickness) });

        var top = CreateFocusFrameEdge(paneId);
        Grid.SetRow(top, 0);
        Grid.SetColumn(top, 0);
        Grid.SetColumnSpan(top, 3);
        frame.Children.Add(top);

        var right = CreateFocusFrameEdge(paneId);
        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 2);
        frame.Children.Add(right);

        var bottom = CreateFocusFrameEdge(paneId);
        Grid.SetRow(bottom, 2);
        Grid.SetColumn(bottom, 0);
        Grid.SetColumnSpan(bottom, 3);
        frame.Children.Add(bottom);

        var left = CreateFocusFrameEdge(paneId);
        Grid.SetRow(left, 1);
        Grid.SetColumn(left, 0);
        frame.Children.Add(left);

        Grid.SetRow(host, 1);
        Grid.SetColumn(host, 1);
        frame.Children.Add(host);

        return frame;
    }

    private static Border CreateFocusFrameEdge(uint paneId)
    {
        return new Border
        {
            Tag = new FocusFrameEdge(paneId),
            Background = Brushes.Transparent,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            IsHitTestVisible = false,
        };
    }

    private UIElement BuildFailedLeafElement(TerminalPaneNodeViewModel node, TerminalHostControl host)
    {
        host.Visibility = Visibility.Collapsed;

        var grid = new Grid { Tag = node.PaneId };
        grid.Children.Add(host);

        var border = new Border
        {
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(18),
            Tag = node.PaneId,
            ContextMenu = BuildPaneContextMenu(node.PaneId),
        };
        border.SetResourceReference(Border.BackgroundProperty, "Terminal.Background.Brush");
        if (node.IsFocused)
            border.SetResourceReference(Border.BorderBrushProperty, "Accent.Primary.Brush");
        else
            border.BorderBrush = Brushes.Transparent;
        ApplyPaneAutomationProperties(border, node.PaneId, host.SessionId, node.IsFocused);

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 360,
        };

        var title = new TextBlock
        {
            Text = "Terminal surface failed",
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary.Brush");
        stack.Children.Add(title);

        var detailText = node.SurfaceState?.Failure is { } failure
            ? $"{failure.Reason} ({failure.WidthPx}x{failure.HeightPx}, attempt {failure.Attempt})"
            : "Surface creation failed.";
        var detail = new TextBlock
        {
            Text = detailText,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary.Brush");
        stack.Children.Add(detail);

        var retry = new Button
        {
            Content = "Retry",
            Padding = new Thickness(16, 6, 16, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(
            retry,
            $"E2E_TerminalPane_Retry_{node.PaneId.ToString(CultureInfo.InvariantCulture)}");
        System.Windows.Automation.AutomationProperties.SetName(retry, "Retry terminal surface");
        retry.Click += (_, _) =>
        {
            if (_activeWorkspaceId is { } workspaceId)
            {
                if (node.SurfaceState?.LastHwnd == IntPtr.Zero)
                    RecreateHostForPane(node.PaneId);
                else
                    _surfaceCoordinator?.RetryHostSurface(workspaceId, node.PaneId);
            }
        };
        stack.Children.Add(retry);

        border.Child = stack;
        grid.Children.Add(border);
        return grid;
    }

    private void RecreateHostForPane(uint paneId)
    {
        if (_hostControls.Remove(paneId, out var host))
            DetachAndDisposeHost(host);

        _layoutShapeKey = null;
        if (Layout != null)
            ApplyLayout(Layout);
    }

    private void OnHostReady(object? sender, HostReadyEventArgs e)
    {
        if (sender is TerminalHostControl { WorkspaceId: var workspaceId })
            _surfaceCoordinator?.OnHostReady(workspaceId, e.PaneId, e.Hwnd, e.WidthPx, e.HeightPx);
    }

    private void OnPaneResized(object? sender, PaneResizeEventArgs e)
    {
        if (sender is TerminalHostControl { WorkspaceId: var workspaceId })
            _surfaceCoordinator?.OnHostResized(workspaceId, e.PaneId, e.WidthPx, e.HeightPx);
    }

    private void OnPaneClicked(object? sender, PaneClickedEventArgs e)
    {
        if (sender is not TerminalHostControl host)
            return;

        _surfaceCoordinator?.FocusPane(host.WorkspaceId, e.PaneId);
    }

    private void ClearScrollBarState()
    {
        foreach (var oldBar in _scrollBars.Values)
            oldBar.Scroll -= OnScrollBarScroll;
        _scrollBars.Clear();
        _scrollSuppressed.Clear();
    }

    private void DetachAndDisposeHost(TerminalHostControl host)
    {
        host.HostReady -= OnHostReady;
        host.PaneResizeRequested -= OnPaneResized;
        host.PaneClicked -= OnPaneClicked;

        DetachHostFromParent(host);

        var hostToDispose = host;
        Dispatcher.BeginInvoke(
            new Action(() => hostToDispose.Dispose()),
            DispatcherPriority.Background);
    }

    private static void DetachHostFromParent(TerminalHostControl host)
    {
        switch (host.Parent)
        {
            case Border border:
                border.Child = null;
                break;
            case Panel panel:
                panel.Children.Remove(host);
                break;
            case ContentControl content:
                content.Content = null;
                break;
        }
    }

    // M-10c: OnSelectionChanged WPF overlay handler removed.
    // Selection is now rendered by DX11 engine via gw_session_set_selection.
    // The SelectionChanged event is still fired by TerminalHostControl for
    // potential future consumers (e.g. clipboard text extraction on mouse up).

    private void UpdateFocusVisuals()
    {
        // M-16-C Phase A1 (D-01) — verification audit #1: BorderThickness was
        // toggling between 0 and 2 on focus change, which shifted the child
        // HwndHost BoundingRect by 2 px and caused glyph layout shift on the
        // active pane. Now the Border is ALWAYS Thickness(1); only BorderBrush
        // changes. Inactive panes get a transparent border (same metrics, no
        // visible color), so the child HWND geometry stays constant.
        //
        // 2026-05-11: keep that constant-metrics rule, but use a full 1 DIP
        // frame. A 0.5 DIP frame can land on fractional physical pixels in a
        // restored window; HwndHost then rounds the child HWND to integer pixels
        // and can cover the right/bottom stroke.
        foreach (var (paneId, host) in _hostControls)
        {
            bool isFocused = paneId == _focusedPaneId;

            if (host.Parent is Grid frame &&
                frame.Tag is uint framePaneId &&
                framePaneId == paneId &&
                frame.Children.OfType<Border>().Any(edge => edge.Tag is FocusFrameEdge))
            {
                foreach (var edge in frame.Children.OfType<Border>())
                {
                    if (edge.Tag is not FocusFrameEdge focusEdge || focusEdge.PaneId != paneId)
                        continue;

                    // HwndHost airspace can still cover WPF siblings on the
                    // right/bottom edge after physical-pixel rounding. The
                    // visible active border is therefore rendered inside the
                    // DX surface; this frame only reserves stable layout space.
                    edge.Background = Brushes.Transparent;
                }

                if (frame.Parent is Panel frameParent)
                    UpdatePaneProbeState(frameParent, paneId, host.SessionId, isFocused);
                continue;
            }

            if (host.Parent is Panel failedPaneParent)
            {
                var border = failedPaneParent.Children
                    .OfType<Border>()
                    .FirstOrDefault(candidate => candidate.Tag is uint id && id == paneId);

                if (border != null)
                {
                    border.BorderThickness = new Thickness(FocusFrameThickness);
                    border.SnapsToDevicePixels = true;
                    border.UseLayoutRounding = true;
                    if (isFocused)
                        border.SetResourceReference(Border.BorderBrushProperty, "Accent.Primary.Brush");
                    else
                        border.BorderBrush = Brushes.Transparent;
                    UpdatePaneProbeState(failedPaneParent, paneId, host.SessionId, isFocused);
                }
            }
        }
    }

    private static void UpdatePaneProbeState(Panel parent, uint paneId, uint sessionId, bool isFocused)
    {
        foreach (var probe in parent.Children.OfType<Button>())
        {
            if (probe.Tag is uint probePaneId && probePaneId == paneId)
                ApplyPaneAutomationProperties(probe, paneId, sessionId, isFocused);
        }
    }

    private static void ApplyPaneAutomationProperties(
        FrameworkElement element,
        uint paneId,
        uint sessionId,
        bool isFocused)
    {
        var helpText = $"paneId={paneId};sessionId={sessionId};isFocused={isFocused.ToString().ToLowerInvariant()}";
        System.Windows.Automation.AutomationProperties.SetAutomationId(element, AutomationIds.TerminalHost(paneId));
        System.Windows.Automation.AutomationProperties.SetName(element, helpText);
        System.Windows.Automation.AutomationProperties.SetHelpText(element, helpText);
    }

    // ── M-16-C Phase B2: ScrollBar bidirectional sync ──

    private void OnScrollPollTick(object? sender, EventArgs e)
    {
        if (_scrollService == null) return;

        bool forceMenu = _scrollService.ForceContextMenu;
        // M-16-D D-12: keep each host's ForceContextMenu in sync. Cheap O(N)
        // assignment runs only when the bool actually changed.
        foreach (var host in _hostControls.Values)
            if (host.ForceContextMenu != forceMenu)
                host.ForceContextMenu = forceMenu;

        foreach (var (paneId, bar) in _scrollBars)
        {
            if (!_hostControls.TryGetValue(paneId, out var host)) continue;
            if (host.SessionId == 0) continue;

            var state = _scrollService.GetState(host.SessionId);
            Visibility wanted = state.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            if (bar.Visibility != wanted)
                bar.Visibility = wanted;
            if (!state.IsVisible) continue;

            if (Math.Abs(bar.Maximum - state.Maximum) > 0.5 ||
                Math.Abs(bar.Value - state.Value) > 0.5 ||
                Math.Abs(bar.ViewportSize - state.ViewportSize) > 0.5)
            {
                _scrollSuppressed.Add(paneId);
                try
                {
                    bar.Maximum = state.Maximum;
                    bar.LargeChange = state.LargeChange;
                    bar.ViewportSize = state.ViewportSize;
                    bar.Value = state.Value;
                }
                finally
                {
                    // Drop suppression on the next dispatcher pass — the same
                    // 100 ms delay used by the M-12 SnapTab pattern, long
                    // enough to absorb the Scroll event WPF re-fires when
                    // Value changes programmatically.
                    var captured = paneId;
                    Dispatcher.BeginInvoke(
                        new Action(() => _scrollSuppressed.Remove(captured)),
                        DispatcherPriority.Background);
                }
            }
        }
    }

    private void OnScrollBarScroll(object? sender, ScrollEventArgs e)
    {
        if (_scrollService == null) return;
        if (sender is not ScrollBar bar || bar.Tag is not uint paneId) return;
        if (_scrollSuppressed.Contains(paneId)) return;
        if (!_hostControls.TryGetValue(paneId, out var host)) return;
        if (host.SessionId == 0) return;

        _scrollService.ScrollTo(host.SessionId, bar.Maximum, e.NewValue);
    }

    // ── M-16-D D-06: pane ContextMenu + ZoomPane ──

    private System.Windows.Controls.ContextMenu BuildPaneContextMenu(uint paneId)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var splitV = NewMenuItem("Split vertical", "E2E_Context_Pane_SplitVertical", "Split pane vertically", _ => SplitFromContext(paneId, SplitOrientation.Vertical));
        var splitH = NewMenuItem("Split horizontal", "E2E_Context_Pane_SplitHorizontal", "Split pane horizontally", _ => SplitFromContext(paneId, SplitOrientation.Horizontal));
        var close = NewMenuItem("Close pane", "E2E_Context_Pane_Close", "Close pane", _ => CloseFromContext(paneId));
        var zoom = NewMenuItem("Zoom pane", "E2E_Context_Pane_Zoom", "Zoom or unzoom pane", _ => ToggleZoom(paneId));

        menu.Items.Add(splitV);
        menu.Items.Add(splitH);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(zoom);
        menu.Items.Add(close);
        return menu;
    }

    private static System.Windows.Controls.MenuItem NewMenuItem(
        string header, string automationId, string automationName, Action<object?> click)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header };
        System.Windows.Automation.AutomationProperties.SetAutomationId(item, automationId);
        System.Windows.Automation.AutomationProperties.SetName(item, automationName);
        item.Click += (s, _) => click(s);
        return item;
    }

    private void SplitFromContext(uint paneId, SplitOrientation direction)
    {
        if (_activeWorkspaceId is not { } workspaceId) return;
        _paneCommands?.SplitPane(workspaceId, paneId, direction);
    }

    private void CloseFromContext(uint paneId)
    {
        if (_activeWorkspaceId is not { } workspaceId) return;
        _paneCommands?.ClosePane(workspaceId, paneId);
    }

    /// <summary>
    /// M-16-D D-15: zoom toggle. ghostty-style — when a pane is zoomed,
    /// every other host has Visibility=Collapsed so it occupies the full
    /// workspace area. The hosts are NOT destroyed, so M-14 reader safety
    /// (atlas swap, render thread stop/start) is unaffected.
    /// </summary>
    private void ToggleZoom(uint paneId)
    {
        if (_activeWorkspaceId is not { } workspaceId || _paneCommands == null)
            return;

        var zoomedPaneId = _paneCommands.ToggleZoom(workspaceId, paneId);
        ApplyZoomVisuals(zoomedPaneId);
    }

    private void ApplyZoomVisuals(uint? zoomedPaneId)
    {
        foreach (var (id, host) in _hostControls)
        {
            if (host.Parent is FrameworkElement frame)
                frame.Visibility = zoomedPaneId is null || id == zoomedPaneId
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    public void ApplyMouseCursorShape(uint sessionId, int mouseCursorShape)
    {
        foreach (var host in _hostControls.Values)
        {
            if (host.SessionId == sessionId)
                host.ApplyMouseCursorShape(mouseCursorShape);
        }

        foreach (var workspaceHosts in _hostsByWorkspace.Values)
        {
            foreach (var host in workspaceHosts.Values)
            {
                if (host.SessionId == sessionId)
                    host.ApplyMouseCursorShape(mouseCursorShape);
            }
        }
    }
}
