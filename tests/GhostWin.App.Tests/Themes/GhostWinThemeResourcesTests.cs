using FluentAssertions;
using GhostWin.App.Themes;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using Xunit;

namespace GhostWin.App.Tests.Themes;

public sealed class GhostWinThemeResourcesTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveColorDictionarySource_uses_high_contrast_override()
    {
        GhostWinThemeResources.ResolveColorDictionarySource(isLight: false, highContrast: true)
            .Should().Be("/GhostWin.App;component/Themes/Colors.HighContrast.xaml");

        GhostWinThemeResources.ResolveColorDictionarySource(isLight: true, highContrast: true)
            .Should().Be("/GhostWin.App;component/Themes/Colors.HighContrast.xaml");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveColorDictionarySource_preserves_light_dark_when_not_high_contrast()
    {
        GhostWinThemeResources.ResolveColorDictionarySource(isLight: true, highContrast: false)
            .Should().Be("/GhostWin.App;component/Themes/Colors.Light.xaml");

        GhostWinThemeResources.ResolveColorDictionarySource(isLight: false, highContrast: false)
            .Should().Be("/GhostWin.App;component/Themes/Colors.Dark.xaml");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void HighContrastDictionary_loads_and_exposes_core_tokens()
    {
        Exception? loadError = null;
        ResourceDictionary? dictionary = null;
        var thread = new Thread(() =>
        {
            try
            {
                var repoRoot = FindRepoRoot();
                var path = Path.Combine(
                    repoRoot,
                    "src",
                    "GhostWin.App",
                    "Themes",
                    "Colors.HighContrast.xaml");
                using var stream = File.OpenRead(path);
                dictionary = (ResourceDictionary)XamlReader.Load(stream);
            }
            catch (Exception ex)
            {
                loadError = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        loadError.Should().BeNull();
        dictionary.Should().NotBeNull();
        dictionary!.Contains("Window.Background.Brush").Should().BeTrue();
        dictionary.Contains("Text.Primary.Brush").Should().BeTrue();
        dictionary.Contains("Terminal.Background.Brush").Should().BeTrue();
        dictionary.Contains("Palette.Item.Selected.Brush").Should().BeTrue();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GhostWin.sln")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate GhostWin.sln");
    }
}
