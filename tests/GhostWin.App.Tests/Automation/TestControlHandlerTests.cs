using FluentAssertions;
using GhostWin.App.Automation;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;
using Xunit;

namespace GhostWin.App.Tests.Automation;

public class TestControlHandlerTests
{
    [Fact]
    public void GetState_ReturnsWorkspaceSessionAndPaneCounts()
    {
        var sessions = new FakeSessionManager();
        sessions.SessionsList.Add(new SessionInfo { Id = 11, IsActive = true });
        sessions.ActiveSessionIdValue = 11;
        var panes = new FakePaneLayout { LeafCountValue = 2, FocusedPaneIdValue = 7, FocusedSessionIdValue = 11 };
        var workspaces = new FakeWorkspaceService(panes);
        workspaces.WorkspacesList.Add(new WorkspaceInfo { Id = 3, IsActive = true });
        workspaces.ActiveWorkspaceIdValue = 3;
        var handler = new TestControlHandler(sessions, workspaces);

        var response = handler.Handle(new TestControlRequest("get-state"));

        response.Ok.Should().BeTrue();
        response.StateVersion.Should().Be(0);
        var state = response.Data.Should().BeOfType<TestControlState>().Subject;
        state.SessionCount.Should().Be(1);
        state.WorkspaceCount.Should().Be(1);
        state.PaneCount.Should().Be(2);
        state.ActiveSessionId.Should().Be(11);
        state.ActiveWorkspaceId.Should().Be(3);
        state.FocusedPaneId.Should().Be(7);
    }

    [Fact]
    public void ExecuteCommand_SplitVertical_CallsActivePaneLayoutAndIncrementsVersion()
    {
        var sessions = new FakeSessionManager();
        var panes = new FakePaneLayout();
        var workspaces = new FakeWorkspaceService(panes);
        var handler = new TestControlHandler(sessions, workspaces);

        var response = handler.Handle(new TestControlRequest(
            "execute-command",
            Data: new TestControlPayload(CommandName: "split-vertical")));

        response.Ok.Should().BeTrue();
        response.StateVersion.Should().Be(1);
        panes.SplitDirections.Should().Equal(SplitOrientation.Vertical);
    }

    [Fact]
    public void InjectOsc_WritesEscapedSequenceToTargetSession()
    {
        var sessions = new FakeSessionManager { ActiveSessionIdValue = 44 };
        var workspaces = new FakeWorkspaceService(new FakePaneLayout());
        var handler = new TestControlHandler(sessions, workspaces);

        var response = handler.Handle(new TestControlRequest(
            "inject-osc",
            SessionId: 55,
            Data: new TestControlPayload(Osc: "22", Message: "text")));

        response.Ok.Should().BeTrue();
        response.StateVersion.Should().Be(1);
        sessions.Injected.Should().ContainSingle();
        sessions.Injected[0].SessionId.Should().Be(55);
        sessions.Injected[0].Text.Should().Be("\x1b]22;text\x1b\\");
    }

    [Fact]
    public void ExecuteCommand_WithoutActivePaneLayout_ReturnsStructuredFailure()
    {
        var handler = new TestControlHandler(
            new FakeSessionManager(),
            new FakeWorkspaceService(null));

        var response = handler.Handle(new TestControlRequest(
            "execute-command",
            Data: new TestControlPayload(CommandName: "split-vertical")));

        response.Ok.Should().BeFalse();
        response.StateVersion.Should().Be(0);
        response.Error.Should().Contain("active pane layout");
    }

    [Fact]
    public void UnknownCommand_ReturnsStructuredError()
    {
        var handler = new TestControlHandler(
            new FakeSessionManager(),
            new FakeWorkspaceService(new FakePaneLayout()));

        var response = handler.Handle(new TestControlRequest("missing-command"));

        response.Ok.Should().BeFalse();
        response.Error.Should().Contain("missing-command");
    }

    private sealed class FakeSessionManager : ISessionManager
    {
        public List<SessionInfo> SessionsList { get; } = [];
        public List<(uint SessionId, string Text)> Injected { get; } = [];
        public uint? ActiveSessionIdValue { get; set; }
        public IReadOnlyList<SessionInfo> Sessions => SessionsList;
        public uint? ActiveSessionId => ActiveSessionIdValue;
        public uint CreateSession(ushort cols = 80, ushort rows = 24) => throw new NotImplementedException();
        public uint CreateSession(string? cwd, ushort cols = 80, ushort rows = 24) => throw new NotImplementedException();
        public void CloseSession(uint id) { }
        public void ActivateSession(uint id) => ActiveSessionIdValue = id;
        public void UpdateTitle(uint id, string title) { }
        public void UpdateCwd(uint id, string cwd) { }
        public void UpdateMouseCursorShape(uint id, int mouseCursorShape) { }
        public void TestOnlyInjectBytes(uint sessionId, byte[] data)
            => Injected.Add((sessionId, System.Text.Encoding.UTF8.GetString(data)));
    }

    private sealed class FakeWorkspaceService(IPaneLayoutService? paneLayout) : IWorkspaceService
    {
        public List<WorkspaceInfo> WorkspacesList { get; } = [];
        public uint? ActiveWorkspaceIdValue { get; set; }
        public IReadOnlyList<WorkspaceInfo> Workspaces => WorkspacesList;
        public uint? ActiveWorkspaceId => ActiveWorkspaceIdValue;
        public IPaneLayoutService? ActivePaneLayout => paneLayout;
        public uint CreateWorkspace()
        {
            WorkspacesList.Add(new WorkspaceInfo { Id = (uint)(WorkspacesList.Count + 1) });
            ActiveWorkspaceIdValue = WorkspacesList[^1].Id;
            return ActiveWorkspaceIdValue.Value;
        }
        public void CloseWorkspace(uint workspaceId) { }
        public void ActivateWorkspace(uint workspaceId) => ActiveWorkspaceIdValue = workspaceId;
        public IPaneLayoutService? GetPaneLayout(uint workspaceId) => paneLayout;
        public void RestoreFromSnapshot(SessionSnapshot snapshot) { }
        public WorkspaceInfo? FindWorkspaceBySessionId(uint sessionId) => null;
        public void MoveWorkspace(uint workspaceId, int newIndex) { }
        public void RenameWorkspace(uint workspaceId, string newName) { }
    }

    private sealed class FakePaneLayout : IPaneLayoutService
    {
        public List<SplitOrientation> SplitDirections { get; } = [];
        public IReadOnlyPaneNode? Root => null;
        public uint? FocusedPaneIdValue { get; set; }
        public uint? FocusedPaneId => FocusedPaneIdValue;
        public uint? FocusedSessionIdValue { get; set; }
        public uint? FocusedSessionId => FocusedSessionIdValue;
        public int LeafCountValue { get; set; }
        public int LeafCount => LeafCountValue;
        public void Initialize(uint initialSessionId) { }
        public void InitializeFromTree(PaneSnapshot rootSnap, ISessionManager sessions) { }
        public (uint sessionId, uint newPaneId)? SplitFocused(SplitOrientation direction)
        {
            SplitDirections.Add(direction);
            return null;
        }
        public void CloseFocused() { }
        public void MoveFocus(FocusDirection direction) { }
        public void SetFocused(uint paneId) => FocusedPaneIdValue = paneId;
        public void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx) { }
        public void OnPaneResized(uint paneId, uint widthPx, uint heightPx) { }
    }
}
