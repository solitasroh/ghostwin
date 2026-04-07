using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    // Per-workspace host caches: workspaceId → (paneId → host).
    private readonly Dictionary<uint, Dictionary<uint, TerminalHostControl>> _hostsByWorkspace = new();

    // The active workspace's host dictionary (mirror of _hostsByWorkspace[_activeWorkspaceId]).
    private readonly Dictionary<uint, TerminalHostControl> _hostControls = new();

    public PaneContainerControl()
    {
        Loaded += (_, _) => WeakReferenceMessenger.Default.RegisterAll(this);
        Unloaded += (_, _) => WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    public void Initialize(IWorkspaceService workspaces)
    {
        _workspaces = workspaces;
    }

    /// <summary>
    /// Adopts an externally created host as the initial pane of the currently
    /// active workspace. Used by MainWindow.InitializeRenderer to bind the
    /// pre-created HWND that RenderInit set as the swap chain target.
    /// </summary>
    public void AdoptInitialHost(TerminalHostControl host, uint workspaceId, uint paneId, uint sessionId)
    {
        host.PaneId = paneId;
        host.SessionId = sessionId;
        host.HostReady += OnHostReady;
        host.PaneResizeRequested += OnPaneResized;
        host.PaneClicked += OnPaneClicked;

        var hosts = GetHostsForWorkspace(workspaceId);
        hosts[paneId] = host;

        if (_activeWorkspaceId == workspaceId || _activeWorkspaceId == null)
        {
            _activeWorkspaceId = workspaceId;
            _hostControls.Clear();
            foreach (var kv in hosts) _hostControls[kv.Key] = kv.Value;
            _focusedPaneId = paneId;

            // Wrap in a Border so host.Parent is always a Border (matches BuildElement
            // leaf branch). UpdateFocusVisuals and the host-reuse detach path in
            // BuildElement both depend on `host.Parent is Border`.
            Content = new Border
            {
                Child = host,
                BorderThickness = new Thickness(0),
                Tag = paneId,
            };
            UpdateFocusVisuals();
        }
    }

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
            else if (node.SessionId is { } sessionId && sessionId != 0)
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
                // Detach from previous Border before re-parenting. WPF forbids
                // a UIElement being the logical child of two parents simultaneously.
                if (host.Parent is Border previousBorder)
                    previousBorder.Child = null;
            }
            else
            {
                host = new TerminalHostControl
                {
                    PaneId = node.Id,
                    SessionId = node.SessionId ?? 0,
                };
                host.HostReady += OnHostReady;
                host.PaneResizeRequested += OnPaneResized;
                host.PaneClicked += OnPaneClicked;
            }
            _hostControls[node.Id] = host;

            var border = new Border
            {
                Child = host,
                BorderThickness = new Thickness(0),
                Tag = node.Id,
            };

            return border;
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

    private void UpdateFocusVisuals()
    {
        foreach (var (paneId, host) in _hostControls)
        {
            if (host.Parent is Border border)
            {
                bool isFocused = paneId == _focusedPaneId;
                border.BorderBrush = isFocused
                    ? new SolidColorBrush(Color.FromRgb(0x00, 0x91, 0xFF))
                    : Brushes.Transparent;
                border.BorderThickness = isFocused
                    ? new Thickness(2)
                    : new Thickness(0);
            }
        }
    }
}
