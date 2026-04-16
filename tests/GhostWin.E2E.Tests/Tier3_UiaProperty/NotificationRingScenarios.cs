using FluentAssertions;
using GhostWin.E2E.Tests.Infrastructure;
using Xunit;

namespace GhostWin.E2E.Tests.Tier3_UiaProperty;

[Trait("Tier", "3")]
[Trait("Category", "E2E")]
[Collection("GhostWin-App")]
public class NotificationRingScenarios : IClassFixture<GhostWinAppFixture>
{
    private readonly GhostWinAppFixture _fixture;

    public NotificationRingScenarios(GhostWinAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Skip = "Requires interactive session for ConPTY stdin injection + OSC round-trip timing")]
    public void OscNotification_ShowsDot()
    {
        // Phase 6-A E2E: OscInjector.InjectOsc9 → dot Visibility == Visible
        // This test requires:
        //   1. App running with active ConPTY session
        //   2. TestOnlyInjectBytes writing OSC 9 to stdin
        //   3. ghostty libvt parsing → callback → C# → XAML dot
        //   4. FlaUI finding the dot via AutomationId = E2E_NotificationRing_{id}
        //
        // Currently skipped: ConPTY stdin injection timing in disconnected
        // session is unreliable. Enable when interactive test runner available.
        var dot = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByAutomationId("E2E_NotificationRing_1"));
        dot.Should().NotBeNull("NotificationRing AutomationId should exist in UIA tree");
    }

    [Fact(Skip = "Requires interactive session")]
    public void TabSwitch_DismissesDot()
    {
        // Phase 6-A E2E: dot visible → switch tab → dot collapsed
    }
}
