using FluentAssertions;
using GhostWin.Automation.Tests.Infrastructure;

namespace GhostWin.Automation.Tests;

[Trait("Category", "DailyE2E")]
public sealed class StateTests
{
    [Fact]
    public async Task State_snapshot_tracks_active_workspace_session_and_focused_pane()
    {
        using var app = await DailyApp.LaunchAsync(nameof(State_snapshot_tracks_active_workspace_session_and_focused_pane));
        if (app is null) return;

        var state = await app.WaitForReadyAsync();
        state.ActiveWorkspaceId.Should().Be(1);
        state.ActiveSessionId.Should().NotBeNull();
        state.FocusedPaneId.Should().Be(1);
        state.FocusedSessionId.Should().Be(state.ActiveSessionId);

        var afterSplit = await app.Client.ExecuteCommandAsync("split-vertical");
        afterSplit.ActiveWorkspaceId.Should().Be(1);
        afterSplit.PaneCount.Should().Be(2);
        afterSplit.FocusedPaneId.Should().NotBeNull();
        afterSplit.FocusedSessionId.Should().NotBeNull();
    }
}
