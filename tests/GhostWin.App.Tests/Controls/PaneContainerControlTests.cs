using System.Reflection;
using System.Runtime.ExceptionServices;
using FluentAssertions;
using GhostWin.App.Controls;
using GhostWin.App.ViewModels;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;
using Xunit;

namespace GhostWin.App.Tests.Controls;

public class PaneContainerControlTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void LayoutSetBeforeInitialize_DefersVisualTreeUntilServicesReady()
    {
        RunOnSta(() =>
        {
            var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
            var snapshot = new TerminalPaneLayoutSnapshot(
                WorkspaceId: 1,
                FocusedPaneId: 1,
                TerminalPaneNodeViewModel.FromReadOnlyNode(root, focusedPaneId: 1));
            var control = new PaneContainerControl();

            control.Layout = snapshot;

            control.Content.Should().BeNull();
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void HostReady_ForwardsHostWorkspaceToSurfaceCoordinator()
    {
        RunOnSta(() =>
        {
            var coordinator = new FakeSurfaceCoordinator();
            var control = new PaneContainerControl();
            SetPrivateField(control, "_surfaceCoordinator", coordinator);
            var host = new TerminalHostControl
            {
                WorkspaceId = 1,
                PaneId = 1,
                SessionId = 10,
            };

            InvokePrivate(
                control,
                "OnHostReady",
                host,
                new HostReadyEventArgs(PaneId: 1, Hwnd: 123, WidthPx: 80, HeightPx: 25));

            coordinator.HostReadyCalls.Should().ContainSingle()
                .Which.Should().Be((1u, 1u, (nint)123, 80u, 25u));
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PaneResized_ForwardsHostWorkspaceToSurfaceCoordinator()
    {
        RunOnSta(() =>
        {
            var coordinator = new FakeSurfaceCoordinator();
            var control = new PaneContainerControl();
            SetPrivateField(control, "_surfaceCoordinator", coordinator);
            var host = new TerminalHostControl
            {
                WorkspaceId = 1,
                PaneId = 1,
                SessionId = 10,
            };

            InvokePrivate(
                control,
                "OnPaneResized",
                host,
                new PaneResizeEventArgs(PaneId: 1, WidthPx: 120, HeightPx: 40));

            coordinator.ResizeCalls.Should().ContainSingle()
                .Which.Should().Be((1u, 1u, 120u, 40u));
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PaneClicked_ForwardsHostWorkspaceToSurfaceCoordinator()
    {
        RunOnSta(() =>
        {
            var coordinator = new FakeSurfaceCoordinator();
            var control = new PaneContainerControl();
            SetPrivateField(control, "_surfaceCoordinator", coordinator);
            var host = new TerminalHostControl
            {
                WorkspaceId = 1,
                PaneId = 1,
                SessionId = 10,
            };

            InvokePrivate(control, "OnPaneClicked", host, new PaneClickedEventArgs(PaneId: 1));

            coordinator.FocusCalls.Should().ContainSingle()
                .Which.Should().Be((1u, 1u));
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClosedWorkspaceId_RemovesInactiveWorkspaceHostCache()
    {
        RunOnSta(() =>
        {
            var control = new PaneContainerControl();
            var caches = GetPrivateField<Dictionary<uint, Dictionary<uint, TerminalHostControl>>>(
                control,
                "_hostsByWorkspace");
            caches[7] = new Dictionary<uint, TerminalHostControl>
            {
                [1] = new TerminalHostControl
                {
                    WorkspaceId = 7,
                    PaneId = 1,
                    SessionId = 10,
                },
            };

            control.ClosedWorkspaceId = 7;

            caches.ContainsKey(7).Should().BeFalse();
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }

    private static void SetPrivateField<T>(PaneContainerControl control, string name, T value)
    {
        typeof(PaneContainerControl)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(control, value);
    }

    private static T GetPrivateField<T>(PaneContainerControl control, string name)
    {
        return (T)typeof(PaneContainerControl)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(control)!;
    }

    private static void InvokePrivate(PaneContainerControl control, string name, params object[] args)
    {
        typeof(PaneContainerControl)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(control, args);
    }

    private sealed class FakeSurfaceCoordinator : ITerminalSurfaceCoordinator
    {
        public List<(uint WorkspaceId, uint PaneId, nint Hwnd, uint WidthPx, uint HeightPx)> HostReadyCalls { get; } = [];
        public List<(uint WorkspaceId, uint PaneId, uint WidthPx, uint HeightPx)> ResizeCalls { get; } = [];
        public List<(uint WorkspaceId, uint PaneId)> FocusCalls { get; } = [];

        public void OnHostReady(uint workspaceId, uint paneId, nint hwnd, uint widthPx, uint heightPx) =>
            HostReadyCalls.Add((workspaceId, paneId, hwnd, widthPx, heightPx));

        public void OnHostResized(uint workspaceId, uint paneId, uint widthPx, uint heightPx) =>
            ResizeCalls.Add((workspaceId, paneId, widthPx, heightPx));

        public void FocusPane(uint workspaceId, uint paneId) =>
            FocusCalls.Add((workspaceId, paneId));
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

    private sealed class FakePaneLayout : IPaneLayoutService
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
        public void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx) =>
            HostReadyCalls++;
        public void OnPaneResized(uint paneId, uint widthPx, uint heightPx) =>
            ResizeCalls++;
    }
}
