using CommunityToolkit.Mvvm.Messaging;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.Services;

public class WorkspaceService : IWorkspaceService
{
    private readonly IEngineService _engine;
    private readonly ISessionManager _sessions;
    private readonly IMessenger _messenger;

    private sealed class WorkspaceEntry
    {
        public required WorkspaceInfo Info { get; init; }
        public required PaneLayoutService PaneLayout { get; init; }
        public required uint InitialSessionId { get; init; }
    }

    private readonly Dictionary<uint, WorkspaceEntry> _entries = new();
    private readonly List<WorkspaceInfo> _orderedWorkspaces = new();
    private uint _nextWorkspaceId = 1;

    public WorkspaceService(IEngineService engine, ISessionManager sessions, IMessenger messenger)
    {
        _engine = engine;
        _sessions = sessions;
        _messenger = messenger;
    }

    public IReadOnlyList<WorkspaceInfo> Workspaces => _orderedWorkspaces;
    public uint? ActiveWorkspaceId { get; private set; }

    public IPaneLayoutService? ActivePaneLayout =>
        ActiveWorkspaceId is { } id && _entries.TryGetValue(id, out var entry)
            ? entry.PaneLayout
            : null;

    public uint CreateWorkspace()
    {
        var workspaceId = _nextWorkspaceId++;

        // Each workspace owns its own PaneLayoutService instance.
        var paneLayout = new PaneLayoutService(_engine, _sessions, _messenger);

        // Create the workspace's initial session.
        var sessionId = _sessions.CreateSession();
        paneLayout.Initialize(sessionId, 0); // BISECT: surfaceId=0 (legacy single-swap-chain path)

        var sessionInfo = _sessions.Sessions.FirstOrDefault(s => s.Id == sessionId);
        var info = new WorkspaceInfo
        {
            Id = workspaceId,
            Name = $"Workspace {workspaceId}",
            Title = sessionInfo?.Title ?? "Terminal",
            Cwd = sessionInfo?.Cwd ?? "",
            IsActive = true,
        };

        // Mirror session title/cwd updates onto the workspace info.
        if (sessionInfo != null)
        {
            sessionInfo.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SessionInfo.Title))
                    info.Title = sessionInfo.Title;
                else if (e.PropertyName == nameof(SessionInfo.Cwd))
                    info.Cwd = sessionInfo.Cwd;
            };
        }

        _entries[workspaceId] = new WorkspaceEntry
        {
            Info = info,
            PaneLayout = paneLayout,
            InitialSessionId = sessionId,
        };
        _orderedWorkspaces.Add(info);

        // Mark all others inactive.
        foreach (var w in _orderedWorkspaces)
            w.IsActive = w.Id == workspaceId;

        ActiveWorkspaceId = workspaceId;
        _messenger.Send(new WorkspaceCreatedMessage(workspaceId));
        _messenger.Send(new WorkspaceActivatedMessage(workspaceId));
        return workspaceId;
    }

    public void CloseWorkspace(uint workspaceId)
    {
        if (!_entries.TryGetValue(workspaceId, out var entry)) return;

        // Close every session belonging to this workspace by repeatedly closing
        // the focused pane until the layout is empty. PaneLayoutService.CloseFocused
        // handles single-pane vs multi-pane internally and calls _sessions.CloseSession.
        var safety = 64;
        while (entry.PaneLayout.Root != null && safety-- > 0)
        {
            entry.PaneLayout.CloseFocused();
        }

        _entries.Remove(workspaceId);
        _orderedWorkspaces.Remove(entry.Info);

        _messenger.Send(new WorkspaceClosedMessage(workspaceId));

        if (ActiveWorkspaceId == workspaceId)
        {
            // Activate the next workspace (most recently created remaining).
            if (_orderedWorkspaces.Count > 0)
            {
                var next = _orderedWorkspaces[^1];
                ActivateWorkspace(next.Id);
            }
            else
            {
                ActiveWorkspaceId = null;
                // Caller (MainWindowViewModel) is responsible for shutting down
                // the application when the last workspace closes.
            }
        }
    }

    public void ActivateWorkspace(uint workspaceId)
    {
        if (!_entries.TryGetValue(workspaceId, out var entry)) return;
        if (ActiveWorkspaceId == workspaceId) return;

        foreach (var w in _orderedWorkspaces)
            w.IsActive = w.Id == workspaceId;

        ActiveWorkspaceId = workspaceId;

        // Switch the engine's active session to the focused pane of the new workspace.
        var sessionToActivate = entry.PaneLayout.FocusedSessionId ?? entry.InitialSessionId;
        _sessions.ActivateSession(sessionToActivate);

        _messenger.Send(new WorkspaceActivatedMessage(workspaceId));
    }

    public IPaneLayoutService? GetPaneLayout(uint workspaceId) =>
        _entries.TryGetValue(workspaceId, out var entry) ? entry.PaneLayout : null;
}
