using FluentAssertions;
using GhostWin.Automation.Tests.Infrastructure;

namespace GhostWin.Automation.Tests;

[Trait("Category", "DailyE2E")]
public sealed class StructureTests
{
    [Fact]
    public async Task MainWindow_exposes_core_uia_surfaces()
    {
        using var app = await DailyApp.LaunchAsync(nameof(MainWindow_exposes_core_uia_surfaces));
        if (app is null) return;

        await app.WaitForElementAsync(AutomationIds.WorkspaceItem(1));
        await app.WaitForElementAsync(AutomationIds.TerminalHost(1));
        await app.WaitForElementAsync(AutomationIds.MouseCursorShape);
        await app.WaitForElementAsync(AutomationIds.MouseCursorId);
        await app.WaitForElementAsync(AutomationIds.MouseCursorSession);
        await app.WaitForElementAsync(AutomationIds.MouseCursorVersion);
        await app.WaitForElementAsync(AutomationIds.MouseCursorUpdatedAt);
    }

    [Fact]
    public async Task Settings_notification_panel_and_command_palette_surfaces_are_reachable()
    {
        using var app = await DailyApp.LaunchAsync(nameof(Settings_notification_panel_and_command_palette_surfaces_are_reachable));
        if (app is null) return;

        await app.Client.ExecuteCommandAsync("open-settings");
        await app.WaitForStateAsync("settings open", state => state.IsSettingsOpen);
        await app.WaitForElementAsync(AutomationIds.SettingsPage);
        await app.WaitForElementAsync(AutomationIds.ThemeCombo);
        await app.WaitForElementAsync(AutomationIds.FontFamily);
        await app.WaitForElementAsync(AutomationIds.FontSize);
        await app.WaitForElementAsync(AutomationIds.ForceContextMenu);
        await app.WaitForElementAsync(AutomationIds.SidebarVisible);
        await app.WaitForElementAsync(AutomationIds.SidebarWidth);
        await app.WaitForElementAsync(AutomationIds.ShowCwd);
        await app.Client.ExecuteCommandAsync("close-settings");
        await app.WaitForStateAsync("settings closed", state => !state.IsSettingsOpen);

        await app.Client.ExecuteCommandAsync("toggle-notification-panel");
        await app.WaitForStateAsync("notification panel open", state => state.IsNotificationPanelOpen);
        await app.WaitForElementAsync(AutomationIds.NotificationPanel);
        await app.WaitForElementAsync(AutomationIds.NotificationList);
        await app.WaitForElementAsync(AutomationIds.MarkAllRead);

        var openPalette = await app.WaitForElementAsync(AutomationIds.OpenCommandPalette);
        openPalette.Patterns.Invoke.Pattern.Invoke();
        await app.WaitForElementAsync(AutomationIds.CommandPalette);
        await app.WaitForElementAsync(AutomationIds.CommandPaletteResults);
        await app.WaitForElementAsync(AutomationIds.CommandPaletteItem("CreateWorkspace"));
    }
}
