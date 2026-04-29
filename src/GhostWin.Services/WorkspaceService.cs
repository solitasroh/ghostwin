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
        public SessionInfo? SessionInfo { get; init; }
        public System.ComponentModel.PropertyChangedEventHandler? PropertyChangedHandler { get; init; }
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

        // Create the workspace's initial session. SurfaceId is assigned later
        // in PaneLayoutService.OnHostReady once the TerminalHostControl binds
        // its child HWND.
        var sessionId = _sessions.CreateSession();
        paneLayout.Initialize(sessionId);

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
        // HookTitleMirror helper 로 추출 (Design §9.3 — CreateWorkspace / RestoreFromSnapshot 공용).
        var handler = HookTitleMirror(info, sessionInfo);

        _entries[workspaceId] = new WorkspaceEntry
        {
            Info = info,
            PaneLayout = paneLayout,
            InitialSessionId = sessionId,
            SessionInfo = sessionInfo,
            PropertyChangedHandler = handler,
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

        // Detach PropertyChanged handler to break SessionInfo → WorkspaceInfo reference.
        if (entry.SessionInfo != null && entry.PropertyChangedHandler != null)
            entry.SessionInfo.PropertyChanged -= entry.PropertyChangedHandler;

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

    // ──────────────────────────────────────────────────────────
    // M-11 Session Restore — 복원 경로 (Design §12.3)
    // ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void RestoreFromSnapshot(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_orderedWorkspaces.Count > 0)
            throw new InvalidOperationException(
                "RestoreFromSnapshot must be called on empty state (no existing workspaces)");

        if (snapshot.Workspaces.Count == 0)
            return; // 빈 스냅샷 — 호출자(App)가 CreateWorkspace 폴백 필요 여부 판단

        foreach (var ws in snapshot.Workspaces)
        {
            var workspaceId = _nextWorkspaceId++;
            var paneLayout = new PaneLayoutService(_engine, _sessions, _messenger);

            // PaneSnapshot 트리 재귀 복원 — 각 leaf 마다 CreateSession(cwd) 호출.
            paneLayout.InitializeFromTree(ws.Root, _sessions);

            // 초기 세션 = 재구성된 트리의 포커스된 첫 leaf.
            // InitializeFromTree 는 항상 첫 leaf 에 FocusedPaneId 를 설정하므로 null 이면 트리 파손.
            var initialSessionId = paneLayout.FocusedSessionId
                ?? throw new InvalidOperationException(
                    "InitializeFromTree produced a tree without a focused leaf");

            var sessionInfo = _sessions.Sessions.FirstOrDefault(s => s.Id == initialSessionId);
            var info = new WorkspaceInfo
            {
                Id = workspaceId,
                Name = ws.Name,
                Title = sessionInfo?.Title ?? "Terminal",
                Cwd = sessionInfo?.Cwd ?? string.Empty,
                IsActive = false,  // 마지막에 ActivateWorkspace 로 일괄 설정
            };

            // 타이틀 미러 구독 (CreateWorkspace 와 동일 로직 — Design §9.3).
            var handler = HookTitleMirror(info, sessionInfo);

            _entries[workspaceId] = new WorkspaceEntry
            {
                Info = info,
                PaneLayout = paneLayout,
                InitialSessionId = initialSessionId,
                SessionInfo = sessionInfo,
                PropertyChangedHandler = handler,
            };
            _orderedWorkspaces.Add(info);

            _messenger.Send(new WorkspaceCreatedMessage(workspaceId));
        }

        // 활성 워크스페이스 설정. 범위 벗어나면 첫 번째 활성화 (Design §3.2 필드 표).
        var idx = snapshot.ActiveWorkspaceIndex;
        if (idx < 0 || idx >= _orderedWorkspaces.Count) idx = 0;
        ActivateWorkspace(_orderedWorkspaces[idx].Id);
    }

    /// <summary>
    /// SessionInfo.Title / Cwd 변화를 WorkspaceInfo 로 미러링하는 PropertyChanged 핸들러 등록.
    /// CreateWorkspace / RestoreFromSnapshot 공용 (Design §9.3 HookTitleMirror helper).
    /// sessionInfo 가 null 이면 null 반환 (예: 세션 조회 실패 시 방어).
    /// </summary>
    private static System.ComponentModel.PropertyChangedEventHandler? HookTitleMirror(
        WorkspaceInfo info, SessionInfo? sessionInfo)
    {
        if (sessionInfo == null) return null;

        System.ComponentModel.PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(SessionInfo.Title))
                info.Title = sessionInfo.Title;
            else if (e.PropertyName == nameof(SessionInfo.Cwd))
                info.Cwd = sessionInfo.Cwd;
        };
        sessionInfo.PropertyChanged += handler;
        return handler;
    }

    public WorkspaceInfo? FindWorkspaceBySessionId(uint sessionId)
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.InitialSessionId == sessionId)
                return entry.Info;
        }
        return null;
    }

    /// <inheritdoc/>
    public void MoveWorkspace(uint workspaceId, int newIndex)
    {
        if (!_entries.TryGetValue(workspaceId, out var entry)) return;
        int oldIndex = _orderedWorkspaces.IndexOf(entry.Info);
        if (oldIndex < 0) return;
        if (_orderedWorkspaces.Count == 0) return;

        int clamped = Math.Clamp(newIndex, 0, _orderedWorkspaces.Count - 1);
        if (oldIndex == clamped) return;

        // Preserve the entry instance — only the position in
        // _orderedWorkspaces changes. HwndHost children survive untouched
        // because PaneContainerControl keys its host caches by workspaceId.
        _orderedWorkspaces.RemoveAt(oldIndex);
        _orderedWorkspaces.Insert(clamped, entry.Info);

        _messenger.Send(new WorkspaceReorderedMessage(workspaceId, clamped));
    }

    /// <inheritdoc/>
    public void RenameWorkspace(uint workspaceId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        if (!_entries.TryGetValue(workspaceId, out var entry)) return;
        if (entry.Info.Name == newName) return;
        entry.Info.Name = newName;
    }
}
