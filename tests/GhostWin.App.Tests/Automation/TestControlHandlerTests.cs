using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using GhostWin.App.Automation;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;
using System.Collections.ObjectModel;
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
    public void InjectNotification_UsesNotificationServiceAndUpdatesSnapshot()
    {
        var sessions = new FakeSessionManager { ActiveSessionIdValue = 44 };
        sessions.SessionsList.Add(new SessionInfo { Id = 44, IsActive = true });
        var notifications = new FakeNotificationService();
        var handler = new TestControlHandler(
            sessions,
            new FakeWorkspaceService(new FakePaneLayout()),
            notifications: notifications);

        var response = handler.Handle(new TestControlRequest(
            "inject-notification",
            SessionId: 44,
            Data: new TestControlPayload(Osc: "title", Message: "body")));

        response.Ok.Should().BeTrue();
        response.StateVersion.Should().Be(1);
        notifications.Notifications.Should().ContainSingle();
        notifications.Notifications[0].Title.Should().Be("title");
        var state = response.Data.Should().BeOfType<TestControlState>().Subject;
        state.NotificationCount.Should().Be(1);
    }

    [Fact]
    public void SetSettings_UpdatesSettingsAndSnapshot()
    {
        var settings = new FakeSettingsService();
        var receiver = new SettingsChangedReceiver();
        WeakReferenceMessenger.Default.Register<SettingsChangedMessage>(
            receiver,
            static (recipient, message) => ((SettingsChangedReceiver)recipient).Receive(message));
        var handler = new TestControlHandler(
            new FakeSessionManager(),
            new FakeWorkspaceService(new FakePaneLayout()),
            settings);

        try
        {
            var response = handler.Handle(new TestControlRequest(
                "set-settings",
                Data: new TestControlPayload(SettingName: "force-context-menu", Value: "true")));

            response.Ok.Should().BeTrue();
            response.StateVersion.Should().Be(1);
            settings.SaveCalls.Should().Be(1);
            settings.Current.Terminal.ForceContextMenu.Should().BeTrue();
            receiver.Messages.Should().ContainSingle()
                .Which.Terminal.ForceContextMenu.Should().BeTrue();
            var state = response.Data.Should().BeOfType<TestControlState>().Subject;
            state.ForceContextMenu.Should().BeTrue();
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(receiver);
        }
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

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string SettingsFilePath => string.Empty;
        public int SaveCalls { get; private set; }
        public void Load() { }
        public void Save() => SaveCalls++;
        public void StartWatching() { }
        public void StopWatching() { }
    }

    private sealed class FakeNotificationService : IOscNotificationService
    {
        public ObservableCollection<NotificationEntry> Notifications { get; } = [];
        public int UnreadCount => Notifications.Count(n => !n.IsRead);

        public void HandleOscEvent(uint sessionId, string title, string body)
        {
            Notifications.Add(new NotificationEntry
            {
                Id = (uint)(Notifications.Count + 1),
                SessionId = sessionId,
                Title = title,
                Body = body,
                IsRead = false
            });
        }

        public void DismissAttention(uint sessionId) { }
        public void MarkAsRead(NotificationEntry entry) => entry.IsRead = true;
        public void MarkAllAsRead()
        {
            foreach (var entry in Notifications)
                entry.IsRead = true;
        }
        public NotificationEntry? GetMostRecentUnread()
            => Notifications.FirstOrDefault(n => !n.IsRead);
    }

    private sealed class SettingsChangedReceiver
    {
        public List<AppSettings> Messages { get; } = [];

        public void Receive(SettingsChangedMessage message)
            => Messages.Add(message.Value);
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
