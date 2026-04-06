using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace GhostWin.App.Converters;

public class ActiveIndicatorBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush InactiveBrush = Brushes.Transparent;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true)
            return InactiveBrush;

        // WPF-UI가 관리하는 시스템 Accent Color 사용
        var accent = ApplicationAccentColorManager.SystemAccent;
        if (accent == default)
            accent = Color.FromRgb(0x00, 0x78, 0xD4); // fallback

        var brush = new SolidColorBrush(accent);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
