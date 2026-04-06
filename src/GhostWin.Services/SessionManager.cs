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
    {
        var id = _engine.CreateSession(null, null, cols, rows);
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
}
