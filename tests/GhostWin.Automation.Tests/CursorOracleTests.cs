using FluentAssertions;
using GhostWin.Automation.Tests.Infrastructure;

namespace GhostWin.Automation.Tests;

[Trait("Category", "DailyE2E")]
public sealed class CursorOracleTests
{
    [Fact]
    public async Task Osc22_updates_cursor_oracle_uia_surface()
    {
        using var app = await DailyApp.LaunchAsync(nameof(Osc22_updates_cursor_oracle_uia_surface));
        if (app is null) return;

        var ready = await app.WaitForReadyAsync();
        ready.ActiveSessionId.Should().NotBeNull();

        await app.Client.InjectOscAsync("22", "text", ready.ActiveSessionId);
        await WaitForProbeAsync(app, AutomationIds.MouseCursorShape, "shape=8 (TEXT)");
        await WaitForProbeAsync(app, AutomationIds.MouseCursorId, "cursorId=32513 (IDC_IBEAM)");
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
