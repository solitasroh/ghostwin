using FluentAssertions;
using GhostWin.App.Automation;
using Xunit;

namespace GhostWin.App.Tests.Automation;

public sealed class AutomationIdsTests
{
    [Fact]
    public void DynamicIds_UseStablePhase3Formats()
    {
        AutomationIds.TerminalHost(7).Should().Be("E2E_TerminalHost_7");
        AutomationIds.WorkspaceItem(3).Should().Be("E2E_WorkspaceItem_3");
        AutomationIds.NotificationItem(11).Should().Be("E2E_NotificationItem_11");
        AutomationIds.NotificationRing(5).Should().Be("E2E_NotificationRing_5");
        AutomationIds.CommandPaletteItem("SplitVertical").Should().Be("E2E_CommandPaletteItem_SplitVertical");
    }

    [Fact]
    public void StaticIds_AreUnique()
    {
        AutomationIds.StaticIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void CommandPaletteItem_ReplacesUnsafeCharacters()
    {
        AutomationIds.CommandPaletteItem("open/settings, now")
            .Should().Be("E2E_CommandPaletteItem_open_settings__now");
    }
}
