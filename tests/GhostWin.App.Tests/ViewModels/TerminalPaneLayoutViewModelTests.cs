using FluentAssertions;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.App.ViewModels;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;
using Xunit;

namespace GhostWin.App.Tests.ViewModels;

public class TerminalPaneLayoutViewModelTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void RebuildFromActiveLayout_ProjectsActiveRootWithFocusedPane()
    {
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
        root.Split(
            SplitOrientation.Horizontal,
            newSessionId: 20,
            oldLeafId: 2,
            newLeafId: 3);
        var layout = new FakePaneLayout(root, focusedPaneId: 3);
        var workspaces = new FakeWorkspaceService(layout);
        var vm = new TerminalPaneLayoutViewModel(workspaces);

        vm.RebuildFromActiveLayout();

        vm.Root.Should().NotBeNull();
        vm.Root!.SplitDirection.Should().Be(SplitOrientation.Horizontal);
        vm.Root.Left!.IsFocused.Should().BeFalse();
        vm.Root.Right!.IsFocused.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReceivePaneFocusChanged_ReprojectsActiveLayoutFocus()
    {
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
        root.Split(
            SplitOrientation.Vertical,
            newSessionId: 20,
            oldLeafId: 2,
            newLeafId: 3);
        var layout = new FakePaneLayout(root, focusedPaneId: 2);
        var workspaces = new FakeWorkspaceService(layout);
        var vm = new TerminalPaneLayoutViewModel(workspaces);
        vm.RebuildFromActiveLayout();

        layout.FocusedPaneIdValue = 3;
        vm.Receive(new PaneFocusChangedMessage(paneId: 3, sessionId: 20));

        vm.Root.Should().NotBeNull();
        vm.Root!.Left!.IsFocused.Should().BeFalse();
        vm.Root.Right!.IsFocused.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RebuildFromActiveLayout_ExposesWorkspaceSnapshotForViewBinding()
    {
        var root = PaneNode.CreateLeaf(id: 11, sessionId: 90);
        var layout = new FakePaneLayout(root, focusedPaneId: 11);
        var workspaces = new FakeWorkspaceService(layout) { ActiveWorkspaceIdValue = 9 };
        var vm = new TerminalPaneLayoutViewModel(workspaces);

        vm.RebuildFromActiveLayout();

        vm.Current.Should().NotBeNull();
        vm.Current!.WorkspaceId.Should().Be(9u);
        vm.Current.FocusedPaneId.Should().Be(11u);
        vm.Current.Root.Should().BeSameAs(vm.Root);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RebuildFromActiveLayout_ProjectsPaneSurfaceFailureIntoSnapshot()
    {
        var root = PaneNode.CreateLeaf(id: 11, sessionId: 90);
        var layout = new FakePaneLayout(root, focusedPaneId: 11)
        {
            SurfaceState = new TerminalPaneSurfaceState(
                TerminalPaneSurfaceStatus.Failed,
                SurfaceId: 0,
                LastHwnd: 123,
                LastWidthPx: 640,
                LastHeightPx: 480,
                Failure: new TerminalPaneSurfaceFailure(
                    PaneId: 11,
                    SessionId: 90,
                    WidthPx: 640,
                    HeightPx: 480,
                    Attempt: 1,
                    Reason: "SurfaceCreate returned 0")),
        };
        var workspaces = new FakeWorkspaceService(layout) { ActiveWorkspaceIdValue = 9 };
        var vm = new TerminalPaneLayoutViewModel(workspaces);

        vm.RebuildFromActiveLayout();

        vm.Current.Should().NotBeNull();
        vm.Current!.Root.SurfaceState.Should().NotBeNull();
        vm.Current.Root.SurfaceState!.Status.Should().Be(TerminalPaneSurfaceStatus.Failed);
        vm.Current.Root.SurfaceState.Failure.Should().NotBeNull();
        vm.Current.Root.SurfaceState.Failure!.Reason.Should().Be("SurfaceCreate returned 0");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReceivePaneFocusChanged_IgnoresStaleMessageFocus()
    {
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
        root.Split(
            SplitOrientation.Vertical,
            newSessionId: 20,
            oldLeafId: 2,
            newLeafId: 3);
        var layout = new FakePaneLayout(root, focusedPaneId: 2);
        var workspaces = new FakeWorkspaceService(layout);
        var vm = new TerminalPaneLayoutViewModel(workspaces);
        vm.RebuildFromActiveLayout();

        vm.Receive(new PaneFocusChangedMessage(paneId: 3, sessionId: 20));

        vm.Root.Should().NotBeNull();
        vm.Root!.Left!.IsFocused.Should().BeTrue();
        vm.Root.Right!.IsFocused.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReceiveWorkspaceClosed_ExposesClosedWorkspaceSignalAndClearsCurrent()
    {
        var root = PaneNode.CreateLeaf(id: 11, sessionId: 90);
        var layout = new FakePaneLayout(root, focusedPaneId: 11);
        var workspaces = new FakeWorkspaceService(layout) { ActiveWorkspaceIdValue = 9 };
        var vm = new TerminalPaneLayoutViewModel(workspaces);
        vm.RebuildFromActiveLayout();

        workspaces.ActivePaneLayoutValue = null;
        workspaces.ActiveWorkspaceIdValue = null;
        vm.Receive(new WorkspaceClosedMessage(9));

        vm.ClosedWorkspaceId.Should().Be(9u);
        vm.Current.Should().BeNull();
        vm.Root.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EnsureRegistered_IsIdempotentForSameMessenger()
    {
        var workspaces = new FakeWorkspaceService(activePaneLayout: null);
        var vm = new TerminalPaneLayoutViewModel(workspaces, new WeakReferenceMessenger());

        var act = () =>
        {
            vm.EnsureRegistered();
            vm.EnsureRegistered();
        };

        act.Should().NotThrow();
    }

    private sealed class FakeWorkspaceService(IPaneLayoutService? activePaneLayout) : IWorkspaceService
    {
        public IReadOnlyList<WorkspaceInfo> Workspaces => [];
        public IPaneLayoutService? ActivePaneLayoutValue { get; set; } = activePaneLayout;
        public uint? ActiveWorkspaceIdValue { get; set; } = 1;
        public uint? ActiveWorkspaceId => ActiveWorkspaceIdValue;
        public IPaneLayoutService? ActivePaneLayout => ActivePaneLayoutValue;
        public uint CreateWorkspace() => 1;
        public void CloseWorkspace(uint workspaceId) { }
        public void ActivateWorkspace(uint workspaceId) { }
        public IPaneLayoutService? GetPaneLayout(uint workspaceId) => ActivePaneLayoutValue;
        public void RestoreFromSnapshot(SessionSnapshot snapshot) { }
        public WorkspaceInfo? FindWorkspaceBySessionId(uint sessionId) => null;
        public void MoveWorkspace(uint workspaceId, int newIndex) { }
        public void RenameWorkspace(uint workspaceId, string newName) { }
    }

    private sealed class FakePaneLayout(IReadOnlyPaneNode? root, uint? focusedPaneId) : IPaneLayoutService, IPaneSurfaceStateProvider
    {
        public TerminalPaneSurfaceState? SurfaceState { get; init; }
        public IReadOnlyPaneNode? Root => root;
        public uint? FocusedPaneIdValue { get; set; } = focusedPaneId;
        public uint? FocusedPaneId => FocusedPaneIdValue;
        public uint? FocusedSessionId => null;
        public int LeafCount => 0;
        public void Initialize(uint initialSessionId) { }
        public void InitializeFromTree(PaneSnapshot rootSnap, ISessionManager sessions) { }
        public (uint sessionId, uint newPaneId)? SplitFocused(SplitOrientation direction) => null;
        public void CloseFocused() { }
        public void MoveFocus(FocusDirection direction) { }
        public void SetFocused(uint paneId) { }
        public void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx) { }
        public void OnPaneResized(uint paneId, uint widthPx, uint heightPx) { }
        public TerminalPaneSurfaceState GetPaneSurfaceState(uint paneId) =>
            SurfaceState ?? TerminalPaneSurfaceState.Pending;
    }
}
