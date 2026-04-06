using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.App.Controls;

/// <summary>
/// Dynamic Grid that renders a PaneNode tree as split panes.
/// Each leaf pane gets its own TerminalHostControl (HwndHost).
/// </summary>
public class PaneContainerControl : ContentControl
{
    private PaneNode? _root;
    private PaneNode? _focusedLeaf;
    private IEngineService? _engine;
    private readonly Dictionary<uint, TerminalHostControl> _hostControls = [];
    private readonly Dictionary<uint, uint> _surfaceIds = []; // paneId → surfaceId
    private bool _useSurfaces; // true after first split

    public PaneNode? Root => _root;
    public PaneNode? FocusedLeaf => _focusedLeaf;

    public event Action<uint>? PaneFocusChanged;

    public void Initialize(IEngineService engine)
    {
        _engine = engine;
    }

    public TerminalHostControl SetInitialPane(uint sessionId)
    {
        _root = PaneNode.CreateLeaf(sessionId);
        _focusedLeaf = _root;
        RebuildVisualTree();
        return _hostControls.Values.First();
    }

    public void SplitFocused(SplitOrientation direction, uint newSessionId)
    {
        if (_focusedLeaf == null || _engine == null) return;

        var newLeaf = _focusedLeaf.Split(direction, newSessionId);

        // First split: migrate original pane to surface mode
        if (!_useSurfaces)
        {
            _useSurfaces = true;
        }

        _focusedLeaf = newLeaf;
        RebuildVisualTree();
    }

    public void CloseFocusedPane()
    {
        if (_root == null || _focusedLeaf == null || _engine == null) return;
        if (_root.IsLeaf) return; // last pane — don't close here (tab close handles it)

        var closingLeaf = _focusedLeaf;

        // Find adjacent leaf for focus transfer
        var leaves = _root.GetLeaves().ToList();
        var idx = leaves.IndexOf(closingLeaf);
        var nextFocus = idx > 0 ? leaves[idx - 1] : leaves[Math.Min(idx + 1, leaves.Count - 1)];
        _focusedLeaf = nextFocus;

        // Destroy surface
        if (_surfaceIds.TryGetValue(closingLeaf.Id, out var surfId))
        {
            _engine.SurfaceDestroy(surfId);
            _surfaceIds.Remove(closingLeaf.Id);
        }

        // Remove from tree
        _root.RemoveLeaf(closingLeaf);

        // If tree collapsed to single leaf, disable surface mode
        if (_root.IsLeaf)
        {
            // Destroy remaining surface — go back to default render path
            if (_surfaceIds.TryGetValue(_root.Id, out var remainId))
            {
                _engine.SurfaceDestroy(remainId);
                _surfaceIds.Remove(_root.Id);
            }
            _useSurfaces = false;
        }

        RebuildVisualTree();
    }

    public void MoveFocus(FocusDirection direction)
    {
        if (_root == null || _focusedLeaf == null) return;

        var leaves = _root.GetLeaves().ToList();
        var idx = leaves.IndexOf(_focusedLeaf);
        if (idx < 0) return;

        // Simple linear navigation for now
        int newIdx = direction switch
        {
            FocusDirection.Left or FocusDirection.Up => Math.Max(0, idx - 1),
            FocusDirection.Right or FocusDirection.Down => Math.Min(leaves.Count - 1, idx + 1),
            _ => idx,
        };

        if (newIdx != idx)
        {
            _focusedLeaf = leaves[newIdx];
            UpdateFocusVisuals();

            if (_focusedLeaf.SessionId.HasValue)
            {
                if (_surfaceIds.TryGetValue(_focusedLeaf.Id, out var surfId))
                    _engine?.SurfaceFocus(surfId);

                PaneFocusChanged?.Invoke(_focusedLeaf.SessionId.Value);
            }
        }
    }

    private void RebuildVisualTree()
    {
        // Clean up old controls
        foreach (var host in _hostControls.Values)
        {
            host.RenderResizeRequested -= OnPaneResized;
        }
        _hostControls.Clear();

        if (_root == null) { Content = null; return; }

        Content = BuildElement(_root);

        // After visual tree is built, set up surfaces for new leaves
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, SetupSurfaces);
    }

    private void SetupSurfaces()
    {
        if (_engine == null || !_useSurfaces) return;

        foreach (var leaf in _root!.GetLeaves())
        {
            if (_surfaceIds.ContainsKey(leaf.Id)) continue;
            if (!leaf.SessionId.HasValue) continue;
            if (!_hostControls.TryGetValue(leaf.Id, out var host)) continue;
            if (host.ChildHwnd == IntPtr.Zero) continue;

            var dpi = VisualTreeHelper.GetDpi(host);
            var w = (uint)Math.Max(1, host.ActualWidth * dpi.DpiScaleX);
            var h = (uint)Math.Max(1, host.ActualHeight * dpi.DpiScaleY);

            var surfId = _engine.SurfaceCreate(host.ChildHwnd, leaf.SessionId.Value, w, h);
            if (surfId != 0)
            {
                _surfaceIds[leaf.Id] = surfId;
                leaf.SurfaceId = surfId;
            }
        }

        UpdateFocusVisuals();
    }

    private UIElement BuildElement(PaneNode node)
    {
        if (node.IsLeaf)
        {
            var host = new TerminalHostControl();
            host.Tag = node.Id;
            host.RenderResizeRequested += OnPaneResized;
            _hostControls[node.Id] = host;

            // Wrap in border for focus indicator
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

            var left = BuildElement(node.Left!);
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

            var right = BuildElement(node.Right!);
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

            var left = BuildElement(node.Left!);
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

            var right = BuildElement(node.Right!);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }

        return grid;
    }

    private void OnPaneResized(uint widthPx, uint heightPx)
    {
        // Find which host control fired this
        // Since TerminalHostControl doesn't carry pane ID in the event, we need to check all
        if (_engine == null) return;

        foreach (var (paneId, host) in _hostControls)
        {
            if (!_surfaceIds.TryGetValue(paneId, out var surfId)) continue;

            var dpi = VisualTreeHelper.GetDpi(host);
            var w = (uint)Math.Max(1, host.ActualWidth * dpi.DpiScaleX);
            var h = (uint)Math.Max(1, host.ActualHeight * dpi.DpiScaleY);

            _engine.SurfaceResize(surfId, w, h);
        }
    }

    private void UpdateFocusVisuals()
    {
        if (_root == null) return;

        foreach (var leaf in _root.GetLeaves())
        {
            if (!_hostControls.TryGetValue(leaf.Id, out var host)) continue;
            if (host.Parent is Border border)
            {
                bool isFocused = leaf == _focusedLeaf;
                border.BorderBrush = isFocused
                    ? new SolidColorBrush(Color.FromRgb(0x00, 0x91, 0xFF))
                    : Brushes.Transparent;
                border.BorderThickness = isFocused
                    ? new Thickness(2)
                    : new Thickness(0);
            }
        }
    }

    public TerminalHostControl? GetHostForLeaf(PaneNode? leaf)
    {
        if (leaf == null) return null;
        return _hostControls.GetValueOrDefault(leaf.Id);
    }
}

public enum FocusDirection { Left, Right, Up, Down }
