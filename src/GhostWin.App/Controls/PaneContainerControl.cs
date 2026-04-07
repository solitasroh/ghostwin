using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.App.Controls;

public class PaneContainerControl : ContentControl,
    IRecipient<PaneLayoutChangedMessage>,
    IRecipient<PaneFocusChangedMessage>
{
    private IPaneLayoutService? _paneLayout;
    private readonly Dictionary<uint, TerminalHostControl> _hostControls = [];
    private uint? _focusedPaneId;

    public PaneContainerControl()
    {
        Loaded += (_, _) => WeakReferenceMessenger.Default.RegisterAll(this);
        Unloaded += (_, _) => WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    public void Initialize(IPaneLayoutService paneLayout)
    {
        _paneLayout = paneLayout;
    }

    public void AdoptInitialHost(TerminalHostControl host, uint paneId)
    {
        host.PaneId = paneId;
        host.HostReady += OnHostReady;
        host.PaneResizeRequested += OnPaneResized;
        _hostControls[paneId] = host;
        _focusedPaneId = paneId;
    }

    public void Receive(PaneLayoutChangedMessage msg)
    {
        BuildGrid(msg.Value);
    }

    public void Receive(PaneFocusChangedMessage msg)
    {
        _focusedPaneId = msg.Value.PaneId;
        UpdateFocusVisuals();
    }

    private void BuildGrid(IReadOnlyPaneNode root)
    {
        // Detach events from old hosts that won't be reused
        var oldHosts = new Dictionary<uint, TerminalHostControl>(_hostControls);
        _hostControls.Clear();

        Content = BuildElement(root, oldHosts);

        // Dispose hosts no longer in the tree
        foreach (var (id, host) in oldHosts)
        {
            if (!_hostControls.ContainsKey(id))
            {
                host.HostReady -= OnHostReady;
                host.PaneResizeRequested -= OnPaneResized;
            }
        }

        UpdateFocusVisuals();
    }

    private UIElement BuildElement(IReadOnlyPaneNode node, Dictionary<uint, TerminalHostControl> oldHosts)
    {
        if (node.IsLeaf)
        {
            // Reuse existing host if available
            TerminalHostControl host;
            if (oldHosts.TryGetValue(node.Id, out var existing))
            {
                host = existing;
            }
            else
            {
                host = new TerminalHostControl { PaneId = node.Id };
                host.HostReady += OnHostReady;
                host.PaneResizeRequested += OnPaneResized;
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

    private void OnHostReady(object? sender, HostReadyEventArgs e)
    {
        _paneLayout?.OnHostReady(e.PaneId, e.Hwnd, e.WidthPx, e.HeightPx);
    }

    private void OnPaneResized(object? sender, PaneResizeEventArgs e)
    {
        _paneLayout?.OnPaneResized(e.PaneId, e.WidthPx, e.HeightPx);
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
