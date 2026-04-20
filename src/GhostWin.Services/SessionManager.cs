using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.Services;

public class SessionManager : ISessionManager
{
    private readonly IEngineService _engine;
    private readonly List<SessionInfo> _sessions = [];
    private IOscNotificationService? _oscService;
    private IWorkspaceService? _workspaceService;

    public IReadOnlyList<SessionInfo> Sessions => _sessions;
    public uint? ActiveSessionId { get; private set; }

    public SessionManager(IEngineService engine)
    {
        _engine = engine;
    }

    public void SetOscService(IOscNotificationService oscService) =>
        _oscService = oscService;

    public void SetWorkspaceService(IWorkspaceService workspaceService) =>
        _workspaceService = workspaceService;

    public void NotifySessionOutput(uint sessionId)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null) return;

        session.LastOutputTime = DateTimeOffset.UtcNow;

        if (session.AgentState is AgentState.Idle or AgentState.Completed or AgentState.Error)
        {
            session.AgentState = AgentState.Running;
            var ws = _workspaceService?.FindWorkspaceBySessionId(sessionId);
            if (ws != null) ws.AgentState = AgentState.Running;
        }
    }

    public void NotifyChildExit(uint sessionId, uint exitCode)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null) return;
        session.AgentState = exitCode == 0 ? AgentState.Completed : AgentState.Error;
        var ws = _workspaceService?.FindWorkspaceBySessionId(sessionId);
        if (ws != null) ws.AgentState = session.AgentState;
    }

    public void TickAgentStateTimer()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-5);
        foreach (var s in _sessions)
        {
            if (s.AgentState == AgentState.Running && s.LastOutputTime < cutoff)
            {
                s.AgentState = AgentState.Idle;
                var ws = _workspaceService?.FindWorkspaceBySessionId(s.Id);
                if (ws != null) ws.AgentState = AgentState.Idle;
            }
        }
    }

    public void TickGitStatus()
    {
        foreach (var s in _sessions)
        {
            if (string.IsNullOrEmpty(s.Cwd)) continue;
            try
            {
                var psi = new ProcessStartInfo("git")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = s.Cwd
                };
                psi.ArgumentList.Add("branch");
                psi.ArgumentList.Add("--show-current");
                using var proc = Process.Start(psi);
                if (proc == null) continue;
                var branch = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(1000);
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(branch))
                {
                    if (s.GitBranch != branch)
                    {
                        s.GitBranch = branch;
                        var ws = _workspaceService?.FindWorkspaceBySessionId(s.Id);
                        if (ws != null) ws.GitBranch = branch;
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(s.GitBranch))
                    {
                        s.GitBranch = "";
                        var ws = _workspaceService?.FindWorkspaceBySessionId(s.Id);
                        if (ws != null) ws.GitBranch = "";
                    }
                }
            }
            catch { /* git not installed or not a git directory */ }
        }
    }

    public uint CreateSession(ushort cols = 80, ushort rows = 24)
        => CreateSession(cwd: null, cols, rows);

    /// <summary>
    /// 지정한 CWD 로 세션 생성. <see cref="ISessionManager.CreateSession(string?, ushort, ushort)"/>
    /// M-11 Session Restore 복원 경로에서 호출. 기존 <c>CreateSession(cols, rows)</c> 는 이 오버로드로 위임 (DRY).
    /// </summary>
    public uint CreateSession(string? cwd, ushort cols = 80, ushort rows = 24)
    {
        // IEngineService.CreateSession 의 두 번째 인자 initialDir 이 cwd 역할
        // (IEngineService.cs:24 — "shellPath, initialDir, cols, rows").
        var id = _engine.CreateSession(null, cwd, cols, rows);
        var session = new SessionInfo { Id = id, Title = "Terminal", IsActive = true };

        foreach (var s in _sessions)
            s.IsActive = false;

        _sessions.Add(session);
        ActiveSessionId = id;
        _engine.ActivateSession(id);

        WeakReferenceMessenger.Default.Send(new SessionCreatedMessage(id));
        return id;
    }

    public void CloseSession(uint sessionId)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null) return;

        _engine.CloseSession(sessionId);
        _sessions.Remove(session);

        if (ActiveSessionId == sessionId)
        {
            if (_sessions.Count > 0)
            {
                var next = _sessions[^1];
                ActiveSessionId = next.Id;
                next.IsActive = true;
                _engine.ActivateSession(next.Id);
            }
            else
            {
                ActiveSessionId = null;
            }
        }

        WeakReferenceMessenger.Default.Send(new SessionClosedMessage(sessionId));
    }

    public void ActivateSession(uint sessionId)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null) return;

        foreach (var s in _sessions)
            s.IsActive = s.Id == sessionId;

        ActiveSessionId = sessionId;
        _engine.ActivateSession(sessionId);
        _oscService?.DismissAttention(sessionId);

        WeakReferenceMessenger.Default.Send(new SessionActivatedMessage(sessionId));
    }

    public void UpdateTitle(uint sessionId, string title)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null) return;
        session.Title = title;

        WeakReferenceMessenger.Default.Send(
            new SessionTitleChangedMessage((sessionId, title)));
    }

    public void UpdateCwd(uint sessionId, string cwd)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null) return;
        session.Cwd = cwd;

        WeakReferenceMessenger.Default.Send(
            new SessionCwdChangedMessage((sessionId, cwd)));
    }

    public void UpdateMouseCursorShape(uint sessionId, int mouseCursorShape)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null) return;
        session.MouseCursorShape = mouseCursorShape;
    }

    // ─────────────────────────────────────────────────────────────────────
    // [TEST-ONLY] Phase 6-A: ConPTY stdin injection
    // gw_session_write 를 통해 ConPTY 입력 파이프에 직접 쓰기.
    // ─────────────────────────────────────────────────────────────────────
#pragma warning disable CA1707, CS0618
    [Obsolete("TEST-ONLY: Phase 6-A ConPTY stdin injection — production code must not call this")]
    public void TestOnlyInjectBytes(uint sessionId, byte[] data)
    {
        _engine.TestOnlyInjectVt(sessionId, data);
    }
#pragma warning restore CA1707, CS0618
}
