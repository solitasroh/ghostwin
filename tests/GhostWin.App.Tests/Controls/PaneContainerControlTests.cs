using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GhostWin.App.Input;
using GhostWin.App.Services;
using GhostWin.App.Tests.Fakes;
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

    [Fact]
    [Trait("Category", "Unit")]
    public void FailedSurfaceLeaf_RendersRetryPlaceholderAndHidesHost()
    {
        RunOnSta(() =>
        {
            var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
            var surfaceState = new TerminalPaneSurfaceState(
                TerminalPaneSurfaceStatus.Failed,
                SurfaceId: 0,
                LastHwnd: 123,
                LastWidthPx: 640,
                LastHeightPx: 480,
                Failure: new TerminalPaneSurfaceFailure(
                    PaneId: 1,
                    SessionId: 10,
                    WidthPx: 640,
                    HeightPx: 480,
                    Attempt: 1,
                    Reason: "SurfaceCreate returned 0"));
            var snapshot = new TerminalPaneLayoutSnapshot(
                WorkspaceId: 7,
                FocusedPaneId: 1,
                TerminalPaneNodeViewModel.FromReadOnlyNode(
                    root,
                    focusedPaneId: 1,
                    _ => surfaceState));
            var coordinator = new FakeSurfaceCoordinator();
            var control = new PaneContainerControl();
            control.Initialize(
                new FakeSessionManager(),
                new FakeEngineService(),
                coordinator,
                new FakeScrollService(),
                new FakePaneCommands(),
                new FakeInputRouter());

            control.Layout = snapshot;

            var hosts = FindDescendants<TerminalHostControl>(control.Content!).ToList();
            hosts.Should().ContainSingle();
            hosts[0].Visibility.Should().Be(Visibility.Collapsed);

            var retry = FindDescendants<Button>(control.Content!)
                .Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "E2E_TerminalPane_Retry_1");
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            coordinator.RetryCalls.Should().ContainSingle().Which.Should().Be((7u, 1u));
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FailedSurfaceLeaf_WithNullHwndRetry_RecreatesHostInsteadOfNativeRetry()
    {
        RunOnSta(() =>
        {
            var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
            var surfaceState = new TerminalPaneSurfaceState(
                TerminalPaneSurfaceStatus.Failed,
                SurfaceId: 0,
                LastHwnd: 0,
                LastWidthPx: 640,
                LastHeightPx: 480,
                Failure: new TerminalPaneSurfaceFailure(
                    PaneId: 1,
                    SessionId: 10,
                    WidthPx: 640,
                    HeightPx: 480,
                    Attempt: 1,
                    Reason: "SurfaceCreate returned 0"));
            var snapshot = new TerminalPaneLayoutSnapshot(
                WorkspaceId: 7,
                FocusedPaneId: 1,
                TerminalPaneNodeViewModel.FromReadOnlyNode(
                    root,
                    focusedPaneId: 1,
                    _ => surfaceState));
            var coordinator = new FakeSurfaceCoordinator();
            var control = new PaneContainerControl();
            control.Initialize(
                new FakeSessionManager(),
                new FakeEngineService(),
                coordinator,
                new FakeScrollService(),
                new FakePaneCommands(),
                new FakeInputRouter());

            control.Layout = snapshot;
            var before = FindDescendants<TerminalHostControl>(control.Content!).Single();

            var retry = FindDescendants<Button>(control.Content!)
                .Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "E2E_TerminalPane_Retry_1");
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            coordinator.RetryCalls.Should().BeEmpty();
            var after = FindDescendants<TerminalHostControl>(control.Content!).Single();
            after.Should().NotBeSameAs(before);
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FocusOnlyLayoutChange_ReusesContentAndUpdatesProbeState()
    {
        RunOnSta(() =>
        {
            var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
            root.Split(SplitOrientation.Vertical, newSessionId: 20, oldLeafId: 2, newLeafId: 3);
            var control = CreateInitializedControl();

            control.Layout = Snapshot(workspaceId: 7, focusedPaneId: 2, root);
            var initialContent = control.Content;

            control.Layout = Snapshot(workspaceId: 7, focusedPaneId: 3, root);

            control.Content.Should().BeSameAs(initialContent);
            var probes = FindDescendants<Button>(control.Content!).ToList();
            probes.Should().Contain(
                b => System.Windows.Automation.AutomationProperties.GetHelpText(b)
                    == "paneId=3;sessionId=20;isFocused=true");
            probes.Should().Contain(
                b => System.Windows.Automation.AutomationProperties.GetHelpText(b)
                    == "paneId=2;sessionId=10;isFocused=false");
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void StructuralLayoutChange_RebuildsContentButReusesMatchingSessionHost()
    {
        RunOnSta(() =>
        {
            var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
            var control = CreateInitializedControl();

            control.Layout = Snapshot(workspaceId: 7, focusedPaneId: 1, root);
            var initialContent = control.Content;
            var initialHost = FindDescendants<TerminalHostControl>(control.Content!).Single();

            root.Split(SplitOrientation.Vertical, newSessionId: 20, oldLeafId: 2, newLeafId: 3);
            control.Layout = Snapshot(workspaceId: 7, focusedPaneId: 2, root);

            control.Content.Should().NotBeSameAs(initialContent);
            var hosts = FindDescendants<TerminalHostControl>(control.Content!).ToList();
            hosts.Should().HaveCount(2);
            hosts.Should().Contain(host => ReferenceEquals(host, initialHost));
            initialHost.PaneId.Should().Be(2u);
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void StopScrollPolling_ClearsDispatcherTimerBeforeEngineShutdown()
    {
        RunOnSta(() =>
        {
            var control = CreateInitializedControl();

            GetPrivateField<System.Windows.Threading.DispatcherTimer?>(
                control,
                "_scrollPollTimer").Should().NotBeNull();

            control.StopScrollPolling();

            GetPrivateField<System.Windows.Threading.DispatcherTimer?>(
                control,
                "_scrollPollTimer").Should().BeNull();
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

    private static PaneContainerControl CreateInitializedControl()
    {
        var control = new PaneContainerControl();
        control.Initialize(
            new FakeSessionManager(),
            new FakeEngineService(),
            new FakeSurfaceCoordinator(),
            new FakeScrollService(),
            new FakePaneCommands(),
            new FakeInputRouter());
        return control;
    }

    private static TerminalPaneLayoutSnapshot Snapshot(
        uint workspaceId,
        uint focusedPaneId,
        PaneNode root)
    {
        return new TerminalPaneLayoutSnapshot(
            workspaceId,
            focusedPaneId,
            TerminalPaneNodeViewModel.FromReadOnlyNode(root, focusedPaneId));
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

    private static IEnumerable<T> FindDescendants<T>(object root)
    {
        if (root is T match)
            yield return match;

        foreach (var child in GetChildren(root))
        {
            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }

    private static IEnumerable<object> GetChildren(object root)
    {
        switch (root)
        {
            case Panel panel:
                foreach (UIElement child in panel.Children)
                    yield return child;
                break;
            case Border border when border.Child != null:
                yield return border.Child;
                break;
            case Decorator decorator when decorator.Child != null:
                yield return decorator.Child;
                break;
            case ContentControl contentControl when contentControl.Content != null:
                yield return contentControl.Content;
                break;
        }
    }

    private sealed class FakeSurfaceCoordinator : ITerminalSurfaceCoordinator
    {
        public List<(uint WorkspaceId, uint PaneId, nint Hwnd, uint WidthPx, uint HeightPx)> HostReadyCalls { get; } = [];
        public List<(uint WorkspaceId, uint PaneId, uint WidthPx, uint HeightPx)> ResizeCalls { get; } = [];
        public List<(uint WorkspaceId, uint PaneId)> FocusCalls { get; } = [];
        public List<(uint WorkspaceId, uint PaneId)> RetryCalls { get; } = [];

        public void OnHostReady(uint workspaceId, uint paneId, nint hwnd, uint widthPx, uint heightPx) =>
            HostReadyCalls.Add((workspaceId, paneId, hwnd, widthPx, heightPx));

        public void OnHostResized(uint workspaceId, uint paneId, uint widthPx, uint heightPx) =>
            ResizeCalls.Add((workspaceId, paneId, widthPx, heightPx));

        public void FocusPane(uint workspaceId, uint paneId) =>
            FocusCalls.Add((workspaceId, paneId));

        public void RetryHostSurface(uint workspaceId, uint paneId) =>
            RetryCalls.Add((workspaceId, paneId));
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

    private sealed class FakeSessionManager : ISessionManager
    {
        public IReadOnlyList<SessionInfo> Sessions => [];
        public uint? ActiveSessionId => null;
        public uint CreateSession(ushort cols = 80, ushort rows = 24) => 0;
        public uint CreateSession(string? cwd, ushort cols = 80, ushort rows = 24) => 0;
        public void CloseSession(uint id) { }
        public void ActivateSession(uint id) { }
        public void UpdateTitle(uint id, string title) { }
        public void UpdateCwd(uint id, string cwd) { }
        public void UpdateMouseCursorShape(uint id, int mouseCursorShape) { }
        public void TestOnlyInjectBytes(uint sessionId, byte[] data) { }
    }

    private sealed class FakeScrollService : ITerminalPaneScrollService
    {
        public bool ForceContextMenu => false;
        public TerminalPaneScrollState GetState(uint sessionId) => TerminalPaneScrollState.Hidden;
        public void ScrollTo(uint sessionId, double maximum, double newValue) { }
    }

    private sealed class FakePaneCommands : ITerminalPaneCommandService
    {
        public void SplitFocused(SplitOrientation direction) { }
        public void CloseFocused() { }
        public void MoveFocus(FocusDirection direction) { }
        public void SplitPane(uint workspaceId, uint paneId, SplitOrientation direction) { }
        public void ClosePane(uint workspaceId, uint paneId) { }
        public uint? ToggleZoom(uint workspaceId, uint paneId) => null;
        public uint? GetZoomedPaneId(uint workspaceId) => null;
        public void ClearZoom(uint workspaceId) { }
    }

    private sealed class FakeInputRouter : ITerminalInputRouter
    {
        public int WriteMouseEvent(uint sessionId, float xPx, float yPx, uint button, uint action, uint mods) => 0;
        public void WriteInput(uint sessionId, ReadOnlySpan<byte> data) { }
        public void WriteTextInput(uint sessionId, string text) { }
        public void HandleCtrlWheel(short delta) { }
        public void HandleShiftWheel(uint sessionId, short delta) { }
        public void HandleUnreportedWheel(uint sessionId, short delta) { }
        public TerminalContextMenuState GetContextMenuState(uint sessionId, bool hasSelection) => default;
        public bool CopySelection(uint sessionId, SelectionRange? range) => false;
        public bool PasteClipboard(uint sessionId) => false;
        public void SelectAll(uint sessionId) { }
        public void ClearScrollback(uint sessionId) { }
        public void OpenExternal(uint sessionId, TerminalExternalTarget target) { }
    }
}
