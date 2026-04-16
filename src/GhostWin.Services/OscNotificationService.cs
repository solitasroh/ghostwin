using CommunityToolkit.Mvvm.Messaging;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;

namespace GhostWin.Services;

public class OscNotificationService : IOscNotificationService
{
    private readonly ISessionManager _sessionManager;
    private readonly IWorkspaceService _workspaceService;
    private readonly ISettingsService _settings;
    private readonly IMessenger _messenger;
    private DateTimeOffset _lastNotifyTime = DateTimeOffset.MinValue;
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(100);

    public OscNotificationService(
        ISessionManager sessionManager,
        IWorkspaceService workspaceService,
        ISettingsService settings,
        IMessenger messenger)
    {
        _sessionManager = sessionManager;
        _workspaceService = workspaceService;
        _settings = settings;
        _messenger = messenger;
    }

    public void HandleOscEvent(uint sessionId, string title, string body)
    {
        if (!_settings.Current.Notifications.RingEnabled) return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastNotifyTime < DebounceInterval) return;
        _lastNotifyTime = now;

        var session = _sessionManager.Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null || session.IsActive) return;

        session.NeedsAttention = true;
        session.LastOscMessage = string.IsNullOrEmpty(body) ? title : body;

        var ws = _workspaceService.FindWorkspaceBySessionId(sessionId);
        if (ws is not null)
        {
            ws.NeedsAttention = true;
            ws.LastOscMessage = session.LastOscMessage;
        }

        _messenger.Send(new OscNotificationMessage(sessionId, title, body));
    }

    public void DismissAttention(uint sessionId)
    {
        var session = _sessionManager.Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) return;
        session.NeedsAttention = false;
        session.LastOscMessage = string.Empty;

        var ws = _workspaceService.FindWorkspaceBySessionId(sessionId);
        if (ws is not null)
        {
            ws.NeedsAttention = false;
            ws.LastOscMessage = string.Empty;
        }
    }
}
