using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;
using GhostWin.Services;
using Xunit;

namespace GhostWin.App.Tests.Automation;

public sealed class NotificationAutomationSurfaceTests
{
    [Fact]
    public void HandleOscEvent_AssignsStableNotificationIds()
    {
        var sessions = new FakeSessionManager();
        sessions.SessionsList.Add(new SessionInfo { Id = 7, IsActive = false, Title = "pane" });
        var workspaces = new FakeWorkspaceService();
        workspaces.WorkspacesList.Add(new WorkspaceInfo { Id = 2, Title = "workspace" });
        var settings = new FakeSettingsService();
        var service = new OscNotificationService(
            sessions,
            workspaces,
            settings,
            new WeakReferenceMessenger());

        service.HandleOscEvent(7, "first", "one");
        Thread.Sleep(120);
        service.HandleOscEvent(7, "second", "two");

        service.Notifications.Select(n => n.Id).Should().Equal(2, 1);
    }

    private sealed class FakeSessionManager : ISessionManager
    {
        public List<SessionInfo> SessionsList { get; } = [];
        public IReadOnlyList<SessionInfo> Sessions => SessionsList;
        public uint? ActiveSessionId => Sessions.FirstOrDefault(s => s.IsActive)?.Id;
        public uint CreateSession(ushort cols = 80, ushort rows = 24) => throw new NotImplementedException();
        public uint CreateSession(string? cwd, ushort cols = 80, ushort rows = 24) => throw new NotImplementedException();
        public void CloseSession(uint id) { }
        public void ActivateSession(uint id) { }
        public void UpdateTitle(uint id, string title) { }
        public void UpdateCwd(uint id, string cwd) { }
        public void UpdateMouseCursorShape(uint id, int mouseCursorShape) { }
        public void TestOnlyInjectBytes(uint sessionId, byte[] data) { }
    }

    private sealed class FakeWorkspaceService : IWorkspaceService
    {
        public List<WorkspaceInfo> WorkspacesList { get; } = [];
        public IReadOnlyList<WorkspaceInfo> Workspaces => WorkspacesList;
        public uint? ActiveWorkspaceId => Workspaces.FirstOrDefault(w => w.IsActive)?.Id;
        public IPaneLayoutService? ActivePaneLayout => null;
        public uint CreateWorkspace() => throw new NotImplementedException();
        public void CloseWorkspace(uint workspaceId) { }
        public void ActivateWorkspace(uint workspaceId) { }
        public IPaneLayoutService? GetPaneLayout(uint workspaceId) => null;
        public void RestoreFromSnapshot(SessionSnapshot snapshot) { }
        public WorkspaceInfo? FindWorkspaceBySessionId(uint sessionId) => WorkspacesList.FirstOrDefault();
        public void MoveWorkspace(uint workspaceId, int newIndex) { }
        public void RenameWorkspace(uint workspaceId, string newName) { }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string SettingsFilePath => string.Empty;
        public void Load() { }
        public void Save() { }
        public void StartWatching() { }
        public void StopWatching() { }
    }
}
