using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.App.Controls;

/// <summary>
/// View for the active workspace's pane tree. Subscribes to <see cref="IWorkspaceService"/>
/// and rebuilds the visual tree whenever the active workspace switches or its
/// pane layout changes. Per-workspace host dictionaries are kept so that
/// switching workspaces preserves each workspace's terminal HwndHost instances.
/// </summary>
public class PaneContainerControl : ContentControl,
    IRecipient<PaneLayoutChangedMessage>,
    IRecipient<PaneFocusChangedMessage>,
    IRecipient<WorkspaceActivatedMessage>
{
    private IWorkspaceService? _workspaces;
    private uint? _activeWorkspaceId;
    private uint? _focusedPaneId;
    private ISessionManager? _sessionManager;

    // Per-workspace host caches: workspaceId → (paneId → host).
    private readonly Dictionary<uint, Dictionary<uint, TerminalHostControl>> _hostsByWorkspace = new();

    // The active workspace's host dictionary (mirror of _hostsByWorkspace[_activeWorkspaceId]).
    private readonly Dictionary<uint, TerminalHostControl> _hostControls = new();

    // M-16-C Phase B2: ScrollBar per pane (only visible in active workspace).
    private readonly Dictionary<uint, ScrollBar> _scrollBars = new();
    // Suppress feedback while we update Value programmatically from the timer.
    private readonly HashSet<uint> _scrollSuppressed = new();
    private DispatcherTimer? _scrollPollTimer;
    private IEngineService? _engine;
    // M-16-C Phase B4: ScrollBar visibility policy ("system", "always", "never").
    private ISettingsService? _settings;

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
        // HC-4 (first-pane-render-failure Option B): Messenger subscription is
        // no longer bound to the Loaded event — Initialize() below subscribes
        // synchronously so that WorkspaceActivatedMessage published by
        // WorkspaceService.CreateWorkspace is guaranteed to be delivered even
        // when InitializeRenderer calls CreateWorkspace before PaneContainer
        // receives its Loaded event. Unloaded unregister is still wired here
        // to release resources on control teardown.
        Unloaded += (_, _) =>
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
            // M-16-C Phase B2: stop the scrollback poll timer.
            if (_scrollPollTimer != null)
            {
                _scrollPollTimer.Stop();
                _scrollPollTimer.Tick -= OnScrollPollTick;
                _scrollPollTimer = null;
            }
        };
    }

    public void Initialize(IWorkspaceService workspaces)
    {
        _workspaces = workspaces;
        _sessionManager = Ioc.Default.GetService<ISessionManager>();
        _engine = Ioc.Default.GetService<IEngineService>();
        _settings = Ioc.Default.GetService<ISettingsService>();
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
        // HC-4: subscribe to messenger immediately (sync). Previously done in
        // the Loaded event handler, which could fire *after* CreateWorkspace
        // published WorkspaceActivatedMessage, causing the initial workspace
        // to miss its SwitchToWorkspace call.
        //
        // ⚠️ DO NOT move this RegisterAll back into a Loaded event handler.
        // first-pane-render-failure §4.3 + design.md §0.1 C-8 / HC-4 lock this
        // ordering: MainWindow.InitializeRenderer calls Initialize() first,
        // *then* CreateWorkspace publishes WorkspaceActivatedMessage. If
        // RegisterAll is deferred to a Loaded event, the message can be
        // published before the recipient is registered, the very first
        // workspace's SwitchToWorkspace never runs, BuildElement never creates
        // the first TerminalHostControl, and the initial pane renders blank.
        // The Unloaded handler in the constructor still unregisters cleanly
        // on teardown, so resource hygiene is preserved.
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    // Note: AdoptInitialHost was removed in first-pane-render-failure Option B.
    // The initial pane is now created by BuildElement via the normal
    // WorkspaceActivatedMessage -> SwitchToWorkspace -> BuildGrid path, using
    // the same code path as split panes. MainWindow no longer owns any host
    // lifecycle; PaneContainerControl is the single owner.

    public void Receive(WorkspaceActivatedMessage msg)
    {
        SwitchToWorkspace(msg.Value);
    }

    public void Receive(PaneLayoutChangedMessage msg)
    {
        // Always rebuild from the active workspace's current root — the message
        // payload is informational; the workspace service is the single source of truth.
        var activeRoot = _workspaces?.ActivePaneLayout?.Root;
        if (activeRoot != null)
            BuildGrid(activeRoot);
    }

    public void Receive(PaneFocusChangedMessage msg)
    {
        _focusedPaneId = msg.Value.PaneId;
        UpdateFocusVisuals();
    }

    private void SwitchToWorkspace(uint workspaceId)
    {
        if (_workspaces == null) return;
        if (_activeWorkspaceId == workspaceId) return;

        // Save current workspace's hosts.
        if (_activeWorkspaceId is { } prevId)
        {
            var prevHosts = GetHostsForWorkspace(prevId);
            prevHosts.Clear();
            foreach (var kv in _hostControls) prevHosts[kv.Key] = kv.Value;
        }

        _activeWorkspaceId = workspaceId;

        // Restore new workspace's hosts.
        _hostControls.Clear();
        if (_hostsByWorkspace.TryGetValue(workspaceId, out var saved))
        {
            foreach (var kv in saved) _hostControls[kv.Key] = kv.Value;
        }

        // Track focus from the new workspace.
        var paneLayout = _workspaces.GetPaneLayout(workspaceId);
        _focusedPaneId = paneLayout?.FocusedPaneId;

        // Rebuild from the new workspace's root.
        var root = paneLayout?.Root;
        if (root != null)
            BuildGrid(root);
        else
            Content = null;
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

    private void BuildGrid(IReadOnlyPaneNode root)
    {
        // Detach events from old hosts that won't be reused
        var oldHosts = new Dictionary<uint, TerminalHostControl>(_hostControls);
        _hostControls.Clear();

        // M-16-C Phase B2: drop the previous ScrollBar map. The Scroll handler
        // is unhooked when the bar leaves the visual tree (no other strong
        // references), and BuildElement repopulates the dict for live leaves.
        foreach (var oldBar in _scrollBars.Values)
            oldBar.Scroll -= OnScrollBarScroll;
        _scrollBars.Clear();
        _scrollSuppressed.Clear();

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
            {
                host.HostReady -= OnHostReady;
                host.PaneResizeRequested -= OnPaneResized;
                host.PaneClicked -= OnPaneClicked;

                var hostToDispose = host;
                Dispatcher.BeginInvoke(
                    new Action(() => hostToDispose.Dispose()),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
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

    private UIElement BuildElement(IReadOnlyPaneNode node, Dictionary<uint, TerminalHostControl> oldHosts)
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

            if (oldHosts.TryGetValue(node.Id, out var byPaneId))
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
                        host.PaneId = node.Id;
                        break;
                    }
                }
            }

            if (host != null)
            {
                // Detach from previous parent before re-parenting. WPF forbids
                // a UIElement being the logical child of two parents simultaneously.
                // Host is directly inside a Border (M-10c: Grid overlay removed).
                if (host.Parent is Border previousBorder)
                {
                    previousBorder.Child = null;
                }
            }
            else
            {
                host = new TerminalHostControl
                {
                    PaneId = node.Id,
                    SessionId = node.SessionId ?? 0,
                };
                // M-15 Stage A: expose host to UIA so the MeasurementDriver
                // can count panes after Alt+V/Alt+H splits. Metadata-only —
                // does not affect rendering or input.
                System.Windows.Automation.AutomationProperties.SetAutomationId(host, "E2E_TerminalHost");
                host.HostReady += OnHostReady;
                host.PaneResizeRequested += OnPaneResized;
                host.PaneClicked += OnPaneClicked;
            }
            // Inject engine service for direct WndProc mouse P/Invoke (Design v1.0, T-5)
            host._engine ??= Ioc.Default.GetService<IEngineService>();
            if (node.SessionId is { } sid)
            {
                var mouseShape = _sessionManager?.Sessions.FirstOrDefault(s => s.Id == sid)?.MouseCursorShape ?? 0;
                host.ApplyMouseCursorShape(mouseShape);
            }

            _hostControls[node.Id] = host;

            var border = new Border
            {
                Child = host,
                BorderThickness = new Thickness(0),
                Tag = node.Id,
            };

            // M-16-C Phase B2: ScrollBar overlay container.
            // Layout: Grid with two columns —
            //   col 0 (*)    : Border + host (terminal)
            //   col 1 (Auto) : ScrollBar (vertical, right edge)
            // host.Parent stays Border, so existing re-parenting and focus
            // visual logic continue to work unchanged.
            var leafGrid = new Grid { Tag = node.Id };
            leafGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            leafGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(border, 0);
            leafGrid.Children.Add(border);

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
                Tag = node.Id,
            };
            scrollBar.Scroll += OnScrollBarScroll;
            Grid.SetColumn(scrollBar, 1);
            leafGrid.Children.Add(scrollBar);
            _scrollBars[node.Id] = scrollBar;

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
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
            };
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
                Width = 4,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
            };
            Grid.SetColumn(splitter, 1);
            grid.Children.Add(splitter);

            var right = BuildElement(node.Right!, oldHosts);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }

        return grid;
    }

    private IPaneLayoutService? ActiveLayout => _workspaces?.ActivePaneLayout;

    private void OnHostReady(object? sender, HostReadyEventArgs e)
    {
        ActiveLayout?.OnHostReady(e.PaneId, e.Hwnd, e.WidthPx, e.HeightPx);
    }

    private void OnPaneResized(object? sender, PaneResizeEventArgs e)
    {
        ActiveLayout?.OnPaneResized(e.PaneId, e.WidthPx, e.HeightPx);
    }

    private void OnPaneClicked(object? sender, PaneClickedEventArgs e)
    {
        ActiveLayout?.SetFocused(e.PaneId);
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
        // active pane. Now the Border is ALWAYS Thickness(2); only BorderBrush
        // changes. Inactive panes get a transparent border (same metrics, no
        // visible color), so the child HWND geometry stays constant.
        foreach (var (paneId, host) in _hostControls)
        {
            // host is directly inside a Border (M-10c: Grid+Canvas overlay removed).
            Border? border = host.Parent as Border;
            if (border != null)
            {
                bool isFocused = paneId == _focusedPaneId;
                border.BorderThickness = new Thickness(2);
                border.BorderBrush = isFocused
                    ? new SolidColorBrush(Color.FromRgb(0x00, 0x91, 0xFF))
                    : Brushes.Transparent;
            }
        }
    }

    // ── M-16-C Phase B2: ScrollBar bidirectional sync ──

    private void OnScrollPollTick(object? sender, EventArgs e)
    {
        if (_engine == null || !_engine.IsInitialized) return;

        string policy = _settings?.Current.Terminal.Scrollbar ?? "system";

        foreach (var (paneId, bar) in _scrollBars)
        {
            if (!_hostControls.TryGetValue(paneId, out var host)) continue;
            if (host.SessionId == 0) continue;

            // M-16-C Phase B4: "never" hides the bar regardless of scrollback,
            // wheel/keyboard scrollback continues to work.
            if (policy == "never")
            {
                if (bar.Visibility != Visibility.Collapsed)
                    bar.Visibility = Visibility.Collapsed;
                continue;
            }

            ScrollbackInfo? info = _engine.GetScrollbackInfo(host.SessionId);
            if (info is not { } sb) continue;

            // "system" auto-hide: bar disappears when scrollback is empty.
            // "always" keeps the track visible at zero range so users get
            // an unambiguous affordance even on a fresh session.
            bool shouldShow = policy == "always" || sb.ScrollbackRows > 0;
            Visibility wanted = shouldShow ? Visibility.Visible : Visibility.Collapsed;
            if (bar.Visibility != wanted)
                bar.Visibility = wanted;
            if (!shouldShow) continue;

            // Map the offset hint onto the bar:
            //   value=0       → top of history    (offset = scrollback)
            //   value=Maximum → bottom (live)     (offset = 0)
            int offset = Math.Clamp(sb.ViewportOffsetFromBottom, 0, (int)sb.ScrollbackRows);
            double newMax = sb.ScrollbackRows;
            double newValue = newMax - offset;

            if (Math.Abs(bar.Maximum - newMax) > 0.5 ||
                Math.Abs(bar.Value - newValue) > 0.5)
            {
                _scrollSuppressed.Add(paneId);
                try
                {
                    bar.Maximum = newMax;
                    bar.LargeChange = Math.Max(1, sb.ViewportRows);
                    bar.ViewportSize = sb.ViewportRows;
                    bar.Value = newValue;
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
        if (_engine == null || !_engine.IsInitialized) return;
        if (sender is not ScrollBar bar || bar.Tag is not uint paneId) return;
        if (_scrollSuppressed.Contains(paneId)) return;
        if (!_hostControls.TryGetValue(paneId, out var host)) return;
        if (host.SessionId == 0) return;

        // Convert bar coordinates back to a scroll delta.
        //   targetOffset = Maximum - newValue
        //   delta_rows   = currentOffset - targetOffset
        // ghostty interprets positive delta as "scroll toward present".
        ScrollbackInfo? info = _engine.GetScrollbackInfo(host.SessionId);
        if (info is not { } sb) return;

        int targetOffset = (int)Math.Round(bar.Maximum - e.NewValue);
        int currentOffset = sb.ViewportOffsetFromBottom;
        int delta = currentOffset - targetOffset;
        if (delta == 0) return;

        _engine.ScrollViewport(host.SessionId, delta);
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
