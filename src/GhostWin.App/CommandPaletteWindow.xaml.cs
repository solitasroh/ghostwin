using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GhostWin.Core.Models;

namespace GhostWin.App;

public partial class CommandPaletteWindow : Window
{
    private static readonly Duration PaletteAnimationDuration =
        new(TimeSpan.FromMilliseconds(140));

    private readonly List<CommandInfo> _allCommands;
    private bool _closeAfterAnimation;
    private bool _isClosingAnimation;

    public CommandPaletteWindow(List<CommandInfo> commands)
    {
        InitializeComponent();
        _allCommands = commands;
        ResultList.ItemsSource = _allCommands;
        Loaded += (_, _) =>
        {
            ApplyAdaptiveWidth();
            SearchBox.Focus();
        };
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        BeginOpenAnimation();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_closeAfterAnimation)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (!_isClosingAnimation)
            BeginCloseAnimation();
    }

    /// <summary>
    /// M-16-B FR-16: clamp the palette width to ~50% of the owner window,
    /// honoring MinWidth=400 / MaxWidth=700 from XAML. Falls back to the
    /// XAML default when no owner is attached (which happens in unit tests
    /// or when invoked outside of MainWindow).
    /// </summary>
    private void ApplyAdaptiveWidth()
    {
        if (Owner is null) return;
        var ownerWidth = Owner.ActualWidth;
        if (ownerWidth <= 0) return;
        Width = Math.Clamp(ownerWidth * 0.5, MinWidth, MaxWidth);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            ResultList.ItemsSource = _allCommands;
            return;
        }
        ResultList.ItemsSource = _allCommands
            .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        c.ActionId.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                if (ResultList.SelectedItem is CommandInfo cmd)
                {
                    Close();
                    cmd.Execute();
                }
                e.Handled = true;
                break;
            case Key.Down:
                ResultList.SelectedIndex = Math.Min(
                    ResultList.SelectedIndex + 1, ResultList.Items.Count - 1);
                e.Handled = true;
                break;
            case Key.Up:
                ResultList.SelectedIndex = Math.Max(
                    ResultList.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
        }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultList.SelectedItem is CommandInfo cmd)
        {
            Close();
            cmd.Execute();
        }
    }

    private void BeginOpenAnimation()
    {
        AnimatePalette(opacityTo: 1, scaleTo: 1);
    }

    private void BeginCloseAnimation()
    {
        _isClosingAnimation = true;
        AnimatePalette(opacityTo: 0, scaleTo: 0.96, CloseAfterAnimation);
    }

    private void CloseAfterAnimation(object? sender, EventArgs e)
    {
        _closeAfterAnimation = true;
        Close();
    }

    private void AnimatePalette(double opacityTo, double scaleTo, EventHandler? completed = null)
    {
        var opacity = CreateAnimation(PaletteShell.Opacity, opacityTo);
        ScaleTransform? scale = PaletteShell.RenderTransform as ScaleTransform;
        opacity.Completed += (_, args) =>
        {
            PaletteShell.BeginAnimation(OpacityProperty, null);
            PaletteShell.Opacity = opacityTo;

            if (scale != null)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = scaleTo;
                scale.ScaleY = scaleTo;
            }

            completed?.Invoke(this, args);
        };

        PaletteShell.BeginAnimation(OpacityProperty, opacity);

        if (scale == null)
            return;

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateAnimation(scale.ScaleX, scaleTo));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateAnimation(scale.ScaleY, scaleTo));
    }

    private static DoubleAnimation CreateAnimation(double from, double to) => new()
    {
        From = from,
        To = to,
        Duration = PaletteAnimationDuration,
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop,
    };
}
