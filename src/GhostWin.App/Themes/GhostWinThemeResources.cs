using System.Windows;
using System.Windows.Media;

namespace GhostWin.App.Themes;

public static class GhostWinThemeResources
{
    private const string DarkColors = "/GhostWin.App;component/Themes/Colors.Dark.xaml";
    private const string LightColors = "/GhostWin.App;component/Themes/Colors.Light.xaml";
    private const string HighContrastColors = "/GhostWin.App;component/Themes/Colors.HighContrast.xaml";

    public static string ResolveColorDictionarySource(bool isLight, bool highContrast)
    {
        if (highContrast)
            return HighContrastColors;

        return isLight ? LightColors : DarkColors;
    }

    public static uint ResolveTerminalClearColor(bool isLight, bool highContrast)
    {
        if (highContrast)
            return ToRgb(SystemColors.WindowColor);

        return isLight ? 0xFBFBFBu : 0x1E1E2Eu;
    }

    public static void ApplyColorDictionary(
        ResourceDictionary resources,
        bool isLight,
        bool highContrast)
    {
        var source = ResolveColorDictionarySource(isLight, highContrast);
        var next = new ResourceDictionary
        {
            Source = new Uri(source, UriKind.RelativeOrAbsolute),
        };

        var oldDictionaries = resources.MergedDictionaries
            .Where(IsGhostWinColorDictionary)
            .ToList();

        resources.MergedDictionaries.Insert(0, next);
        foreach (var old in oldDictionaries)
            resources.MergedDictionaries.Remove(old);
    }

    private static bool IsGhostWinColorDictionary(ResourceDictionary dictionary) =>
        dictionary.Source?.OriginalString.IndexOf(
            "Themes/Colors.",
            StringComparison.Ordinal) >= 0;

    private static uint ToRgb(Color color) =>
        ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
}
