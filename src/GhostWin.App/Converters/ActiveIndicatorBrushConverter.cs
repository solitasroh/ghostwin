using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GhostWin.App.Converters;

public class ActiveIndicatorBrushConverter : IValueConverter
{
    // cmux accent color (dark mode): rgb(0, 145, 255) = #0091FF
    private static readonly SolidColorBrush ActiveBrush = new(Color.FromRgb(0x00, 0x91, 0xFF));
    private static readonly SolidColorBrush InactiveBrush = Brushes.Transparent;

    static ActiveIndicatorBrushConverter()
    {
        ActiveBrush.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? ActiveBrush : InactiveBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
