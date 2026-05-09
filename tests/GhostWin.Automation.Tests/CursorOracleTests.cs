using FluentAssertions;
using GhostWin.Automation.Tests.Infrastructure;

namespace GhostWin.Automation.Tests;

[Trait("Category", "DailyE2E")]
public sealed class CursorOracleTests
{
    [Theory]
    [InlineData("text", 32513)]
    [InlineData("pointer", 32649)]
    [InlineData("ew-resize", 32644)]
    [InlineData("default", 32512)]
    public async Task Osc22_updates_cursor_oracle_uia_surface(string value, int expectedCursorId)
    {
        using var app = await DailyApp.LaunchAsync($"{nameof(Osc22_updates_cursor_oracle_uia_surface)}_{value}");
        if (app is null) return;

        var ready = await app.WaitForReadyAsync();
        ready.ActiveSessionId.Should().NotBeNull();

        await app.Client.InjectOscAsync("22", value, ready.ActiveSessionId);
        await WaitForProbeAsync(app, AutomationIds.MouseCursorId, $"cursorId={expectedCursorId}");
        await WaitForProbeAsync(app, AutomationIds.MouseCursorSession, $"sessionId={ready.ActiveSessionId}");
        await WaitForProbeAsync(app, AutomationIds.MouseCursorVersion, "version=");
        await WaitForProbeAsync(app, AutomationIds.MouseCursorUpdatedAt, "updatedAt=");
    }

    private static async Task WaitForProbeAsync(DailyApp app, string automationId, string expected)
    {
        await app.WaitForStateAsync(
            $"probe {automationId} contains {expected}",
            _ => app.ReadElementText(automationId).Contains(expected, StringComparison.Ordinal));
    }
}
