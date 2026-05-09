using System.Globalization;
using System.Windows.Data;

namespace GhostWin.App.Automation;

public sealed class CommandPaletteItemAutomationIdConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string actionId && !string.IsNullOrWhiteSpace(actionId)
            ? AutomationIds.CommandPaletteItem(actionId)
            : string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
