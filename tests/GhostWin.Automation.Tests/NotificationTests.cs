using FluentAssertions;
using GhostWin.Automation.Tests.Infrastructure;

namespace GhostWin.Automation.Tests;

[Trait("Category", "DailyE2E")]
public sealed class NotificationTests
{
    [Fact]
    public async Task Typed_notification_injection_updates_state_panel_item_and_mark_read()
    {
        using var app = await DailyApp.LaunchAsync(nameof(Typed_notification_injection_updates_state_panel_item_and_mark_read));
        if (app is null) return;

        var first = await app.WaitForReadyAsync();
        first.ActiveSessionId.Should().NotBeNull();

        await app.Client.ExecuteCommandAsync("new-workspace");
        var injected = await app.Client.InjectNotificationAsync(
            "GhostWin test",
            "daily notification",
            first.ActiveSessionId);

        injected.NotificationCount.Should().Be(1);
        injected.UnreadNotificationCount.Should().Be(1);

        await app.Client.ExecuteCommandAsync("toggle-notification-panel");
        await app.WaitForStateAsync("notification panel open", state => state.IsNotificationPanelOpen);
        await app.WaitForElementAsync(AutomationIds.NotificationItem(1));

        var afterRead = await app.Client.ExecuteCommandAsync("mark-all-read");
        afterRead.NotificationCount.Should().Be(1);
        afterRead.UnreadNotificationCount.Should().Be(0);
    }
}
