using FluentAssertions;
using GhostWin.Automation.Tests.Infrastructure;

namespace GhostWin.Automation.Tests;

[Trait("Category", "DailyE2E")]
public sealed class CommandTests
{
    [Fact]
    public async Task Ipc_commands_drive_workspace_pane_and_settings_state()
    {
        using var app = await DailyApp.LaunchAsync(nameof(Ipc_commands_drive_workspace_pane_and_settings_state));
        if (app is null) return;

        var initial = await app.WaitForReadyAsync();
        initial.WorkspaceCount.Should().Be(1);
        initial.PaneCount.Should().Be(1);

        var afterWorkspace = await app.Client.ExecuteCommandAsync("new-workspace");
        afterWorkspace.WorkspaceCount.Should().Be(2);
        afterWorkspace.ActiveWorkspaceId.Should().Be(2);
        await app.WaitForElementAsync(AutomationIds.WorkspaceItem(2));

        var afterVertical = await app.Client.ExecuteCommandAsync("split-vertical");
        afterVertical.PaneCount.Should().Be(2);
        await app.WaitForElementAsync(AutomationIds.TerminalHost(2));

        var afterHorizontal = await app.Client.ExecuteCommandAsync("split-horizontal");
        afterHorizontal.PaneCount.Should().Be(3);
        afterHorizontal.FocusedPaneId.Should().NotBeNull();
        await app.WaitForElementAsync(AutomationIds.TerminalHost(afterHorizontal.FocusedPaneId!.Value));

        var afterClose = await app.Client.ExecuteCommandAsync("close-pane");
        afterClose.PaneCount.Should().Be(2);

        var openSettings = await app.Client.ExecuteCommandAsync("open-settings");
        openSettings.IsSettingsOpen.Should().BeTrue();

        var closeSettings = await app.Client.ExecuteCommandAsync("close-settings");
        closeSettings.IsSettingsOpen.Should().BeFalse();
    }
}
