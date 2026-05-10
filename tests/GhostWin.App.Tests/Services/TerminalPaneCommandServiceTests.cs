using FluentAssertions;
using GhostWin.App.Services;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;
using Xunit;

namespace GhostWin.App.Tests.Services;

public class TerminalPaneCommandServiceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void SplitPane_FocusesTargetPaneThenSplits()
    {
        var layout = new FakePaneLayout();
        var service = new TerminalPaneCommandService(new FakeWorkspaceService(1, layout));

        service.SplitPane(1, 5, SplitOrientation.Vertical);

        layout.Calls.Should().Equal("focus:5", "split:Vertical");
        service.GetZoomedPaneId(1).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SplitFocused_UsesActiveLayoutAndClearsZoom()
    {
        var layout = new FakePaneLayout();
        var service = new TerminalPaneCommandService(new FakeWorkspaceService(1, layout));
        service.ToggleZoom(1, 5);

        service.SplitFocused(SplitOrientation.Horizontal);

        layout.Calls.Should().Equal("focus:5", "split:Horizontal");
        service.GetZoomedPaneId(1).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClosePane_FocusesTargetPaneThenClosesFocusedPane()
    {
        var layout = new FakePaneLayout();
        var service = new TerminalPaneCommandService(new FakeWorkspaceService(1, layout));

        service.ClosePane(1, 5);

        layout.Calls.Should().Equal("focus:5", "close");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CloseFocused_UsesActiveLayoutAndClearsZoom()
    {
        var layout = new FakePaneLayout();
        var service = new TerminalPaneCommandService(new FakeWorkspaceService(1, layout));
        service.ToggleZoom(1, 5);

        service.CloseFocused();

        layout.Calls.Should().Equal("focus:5", "close");
        service.GetZoomedPaneId(1).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MoveFocus_UsesActiveLayout()
    {
        var layout = new FakePaneLayout();
        var service = new TerminalPaneCommandService(new FakeWorkspaceService(1, layout));

        service.MoveFocus(FocusDirection.Right);

        layout.Calls.Should().Equal("move:Right");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToggleZoom_TogglesAndFocusesTargetPane()
    {
        var layout = new FakePaneLayout();
        var service = new TerminalPaneCommandService(new FakeWorkspaceService(1, layout));

        service.ToggleZoom(1, 5).Should().Be(5);
        service.ToggleZoom(1, 5).Should().BeNull();

        layout.Calls.Should().Equal("focus:5");
        service.GetZoomedPaneId(1).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToggleZoom_WhenAnotherPaneZoomed_SwitchesZoomTarget()
    {
        var layout = new FakePaneLayout();
        var service = new TerminalPaneCommandService(new FakeWorkspaceService(1, layout));

        service.ToggleZoom(1, 5);
        service.ToggleZoom(1, 9);

        service.GetZoomedPaneId(1).Should().Be(9);
        layout.Calls.Should().Equal("focus:5", "focus:9");
    }

    private sealed class FakeWorkspaceService(uint workspaceId, IPaneLayoutService layout)
        : IWorkspaceService
    {
        public IReadOnlyList<WorkspaceInfo> Workspaces => [];
        public uint? ActiveWorkspaceId => workspaceId;
        public IPaneLayoutService? ActivePaneLayout => layout;
        public uint CreateWorkspace() => 0;
        public void CloseWorkspace(uint workspaceId) { }
        public void ActivateWorkspace(uint workspaceId) { }
        public IPaneLayoutService? GetPaneLayout(uint id) => id == workspaceId ? layout : null;
        public void RestoreFromSnapshot(SessionSnapshot snapshot) { }
        public WorkspaceInfo? FindWorkspaceBySessionId(uint sessionId) => null;
        public void MoveWorkspace(uint workspaceId, int newIndex) { }
        public void RenameWorkspace(uint workspaceId, string newName) { }
    }

    private sealed class FakePaneLayout : IPaneLayoutService
    {
        public List<string> Calls { get; } = [];
        public IReadOnlyPaneNode? Root => null;
        public uint? FocusedPaneId => null;
        public uint? FocusedSessionId => null;
        public int LeafCount => 0;
        public void Initialize(uint initialSessionId) { }
        public void InitializeFromTree(PaneSnapshot rootSnap, ISessionManager sessions) { }
        public (uint sessionId, uint newPaneId)? SplitFocused(SplitOrientation direction)
        {
            Calls.Add($"split:{direction}");
            return null;
        }

        public void CloseFocused() => Calls.Add("close");
        public void MoveFocus(FocusDirection direction) => Calls.Add($"move:{direction}");
        public void SetFocused(uint paneId) => Calls.Add($"focus:{paneId}");
        public void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx) { }
        public void OnPaneResized(uint paneId, uint widthPx, uint heightPx) { }
    }
}
