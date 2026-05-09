using System.Text.Json;
using FluentAssertions;
using GhostWin.Automation.Tests.Infrastructure;

namespace GhostWin.Automation.Tests;

[Trait("Category", "DailyE2E")]
public sealed class SettingsTests
{
    [Fact]
    public async Task Settings_changes_are_saved_in_isolated_profile_and_reloaded()
    {
        using var app = await DailyApp.LaunchAsync(nameof(Settings_changes_are_saved_in_isolated_profile_and_reloaded));
        if (app is null) return;

        await app.Client.SetSettingAsync("appearance", "light");
        await app.Client.SetSettingAsync("sidebar-visible", "false");
        await app.Client.SetSettingAsync("sidebar-width", "260");
        var state = await app.Client.SetSettingAsync("force-context-menu", "true");

        state.Appearance.Should().Be("light");
        state.SidebarVisible.Should().BeFalse();
        state.SidebarWidth.Should().Be(260);
        state.ForceContextMenu.Should().BeTrue();

        var settingsPath = Path.Combine(app.Session.ProfileDir, "GhostWin", "ghostwin.json");
        File.Exists(settingsPath).Should().BeTrue();

        using (var doc = JsonDocument.Parse(File.ReadAllText(settingsPath)))
        {
            var appNode = doc.RootElement.GetProperty("app");
            appNode.GetProperty("appearance").GetString().Should().Be("light");
            appNode.GetProperty("sidebar").GetProperty("visible").GetBoolean().Should().BeFalse();
            appNode.GetProperty("sidebar").GetProperty("width").GetInt32().Should().Be(260);
            appNode.GetProperty("terminal").GetProperty("force_context_menu").GetBoolean().Should().BeTrue();
        }

        await app.RelaunchAsync();
        var reloaded = await app.Client.GetStateAsync();
        reloaded.Appearance.Should().Be("light");
        reloaded.SidebarVisible.Should().BeFalse();
        reloaded.SidebarWidth.Should().Be(260);
        reloaded.ForceContextMenu.Should().BeTrue();
    }
}
