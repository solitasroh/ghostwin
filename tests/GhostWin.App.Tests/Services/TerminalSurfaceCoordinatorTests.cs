using FluentAssertions;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;
using GhostWin.Services;
using Xunit;

namespace GhostWin.App.Tests.Services;

public class TerminalSurfaceCoordinatorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void HostReady_RoutesToWorkspaceLayoutInsteadOfActiveLayout()
    {
        var workspaceOne = new FakePaneLayout();
        var workspaceTwo = new FakePaneLayout();
        var coordinator = new TerminalSurfaceCoordinator(
            new FakeWorkspaceService(
                activeWorkspaceId: 2,
                layouts: new Dictionary<uint, IPaneLayoutService>
                {
                    [1] = workspaceOne,
                    [2] = workspaceTwo,
                }));

        coordinator.OnHostReady(1, 5, 123, 80, 25);

        workspaceOne.HostReadyCalls.Should().Be(1);
        workspaceTwo.HostReadyCalls.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FocusPane_IgnoresInactiveWorkspaceHost()
    {
        var workspaceOne = new FakePaneLayout();
        var workspaceTwo = new FakePaneLayout();
        var coordinator = new TerminalSurfaceCoordinator(
            new FakeWorkspaceService(
                activeWorkspaceId: 2,
                layouts: new Dictionary<uint, IPaneLayoutService>
                {
                    [1] = workspaceOne,
                    [2] = workspaceTwo,
                }));

        coordinator.FocusPane(1, 5);

        workspaceOne.SetFocusedCalls.Should().Be(0);
        workspaceTwo.SetFocusedCalls.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void HostResized_DropsUnknownWorkspace()
    {
        var workspaceOne = new FakePaneLayout();
        var coordinator = new TerminalSurfaceCoordinator(
            new FakeWorkspaceService(
                activeWorkspaceId: 1,
                layouts: new Dictionary<uint, IPaneLayoutService> { [1] = workspaceOne }));

        coordinator.OnHostResized(99, 5, 80, 25);

        workspaceOne.ResizeCalls.Should().Be(0);
    }

    private sealed class FakeWorkspaceService(
        uint? activeWorkspaceId,
        IReadOnlyDictionary<uint, IPaneLayoutService> layouts)
        : IWorkspaceService
    {
        public IReadOnlyList<WorkspaceInfo> Workspaces => [];
        public uint? ActiveWorkspaceId => activeWorkspaceId;
        public IPaneLayoutService? ActivePaneLayout =>
            activeWorkspaceId is { } id && layouts.TryGetValue(id, out var layout)
                ? layout
                : null;

        public uint CreateWorkspace() => 0;
        public void CloseWorkspace(uint workspaceId) { }
        public void ActivateWorkspace(uint workspaceId) { }
        public IPaneLayoutService? GetPaneLayout(uint workspaceId) =>
            layouts.TryGetValue(workspaceId, out var layout) ? layout : null;
        public void RestoreFromSnapshot(SessionSnapshot snapshot) { }
        public WorkspaceInfo? FindWorkspaceBySessionId(uint sessionId) => null;
        public void MoveWorkspace(uint workspaceId, int newIndex) { }
        public void RenameWorkspace(uint workspaceId, string newName) { }
    }

    private sealed class FakePaneLayout : IPaneLayoutService, ITerminalSurfaceLayout
    {
        public int HostReadyCalls { get; private set; }
        public int ResizeCalls { get; private set; }
        public int SetFocusedCalls { get; private set; }
        public IReadOnlyPaneNode? Root => null;
        public uint? FocusedPaneId => null;
        public uint? FocusedSessionId => null;
        public int LeafCount => 0;
        public void Initialize(uint initialSessionId) { }
        public void InitializeFromTree(PaneSnapshot rootSnap, ISessionManager sessions) { }
        public (uint sessionId, uint newPaneId)? SplitFocused(SplitOrientation direction) => null;
        public void CloseFocused() { }
        public void MoveFocus(FocusDirection direction) { }
        public void SetFocused(uint paneId) => SetFocusedCalls++;
        public void AttachHostSurface(uint paneId, nint hwnd, uint widthPx, uint heightPx) => HostReadyCalls++;
        public void ResizeHostSurface(uint paneId, uint widthPx, uint heightPx) => ResizeCalls++;
    }
}
