using CommunityToolkit.Mvvm.Messaging;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.Services;

public class SessionManager : ISessionManager
{
    private readonly IEngineService _engine;
    private readonly List<SessionInfo> _sessions = [];

    public IReadOnlyList<SessionInfo> Sessions => _sessions;
    public uint? ActiveSessionId { get; private set; }

    public SessionManager(IEngineService engine)
    {
        _engine = engine;
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

    // ─────────────────────────────────────────────────────────────────────
    // [TEST-ONLY] Phase 6-A 선행 stub
    //
    // 현재: NotImplementedException — Phase 6-A 구현 전까지 호출 불가.
    // Phase 6-A 에서: ConPTY 입력 파이프 핸들을 통해 byte[] 직접 쓰기.
    // ─────────────────────────────────────────────────────────────────────
#pragma warning disable CA1707, CS0618  // Test-only; Obsolete intentional
    [Obsolete("TEST-ONLY: Phase 6-A ConPTY stdin injection — production code must not call this")]
    public void TestOnlyInjectBytes(uint sessionId, byte[] data)
    {
        // TODO Phase 6-A: IEngineService 에 InjectBytes(uint sessionId, byte[] data) 추가 후 위임.
        // ConPTY 입력 파이프 → stdin 에 data 기록 → shell 이 OSC 시퀀스 수신.
        throw new NotImplementedException(
            $"TestOnlyInjectBytes: Phase 6-A 에서 구현 예정. sessionId={sessionId}, " +
            $"data.Length={data?.Length ?? 0}");
    }
#pragma warning restore CA1707, CS0618
}
