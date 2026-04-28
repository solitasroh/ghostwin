using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GhostWin.Core.Models;

namespace GhostWin.App;

public partial class CommandPaletteWindow : Window
{
    private readonly List<CommandInfo> _allCommands;

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
}
