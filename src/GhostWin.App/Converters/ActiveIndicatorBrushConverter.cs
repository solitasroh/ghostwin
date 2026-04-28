using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GhostWin.App.Converters;

public class ActiveIndicatorBrushConverter : IValueConverter
{
    // Brush is resolved per-call so theme swaps (Application.Resources
    // MergedDictionaries) take effect without rebuilding the binding.
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? Application.Current.FindResource("Accent.Primary.Brush")
            : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
