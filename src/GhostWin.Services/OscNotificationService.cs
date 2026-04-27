using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.Services;

public partial class OscNotificationService : ObservableObject, IOscNotificationService
{
    private readonly ISessionManager _sessionManager;
    private readonly IWorkspaceService _workspaceService;
    private readonly ISettingsService _settings;
    private readonly IMessenger _messenger;
    private DateTimeOffset _lastNotifyTime = DateTimeOffset.MinValue;
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(100);
    private const int MaxNotifications = 100;

    public ObservableCollection<NotificationEntry> Notifications { get; } = [];

    [ObservableProperty]
    private int _unreadCount;

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
        if (session is null) return;

        bool isActiveSession = session.IsActive;
        var message = string.IsNullOrEmpty(body) ? title : body;

        if (!isActiveSession)
        {
            session.NeedsAttention = true;
            session.AgentState = AgentState.WaitingForInput;
        }
        session.LastOscMessage = message;

        var ws = _workspaceService.FindWorkspaceBySessionId(sessionId);
        if (ws is not null)
        {
            if (!isActiveSession)
            {
                ws.NeedsAttention = true;
                ws.AgentState = AgentState.WaitingForInput;
            }
            ws.LastOscMessage = message;
        }

        var entry = new NotificationEntry
        {
            SessionId = sessionId,
            SessionTitle = ws?.Title ?? session.Title,
            Title = title,
            Body = message,
            ReceivedAt = now,
            IsRead = isActiveSession
        };
        Notifications.Insert(0, entry);
        if (Notifications.Count > MaxNotifications)
            Notifications.RemoveAt(Notifications.Count - 1);
        UpdateUnreadCount();

        _messenger.Send(new OscNotificationMessage(sessionId, title, body));
    }

    public void DismissAttention(uint sessionId)
    {
        var session = _sessionManager.Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) return;
        session.NeedsAttention = false;
        session.LastOscMessage = string.Empty;
        if (session.AgentState == AgentState.WaitingForInput)
            session.AgentState = AgentState.Idle;

        var ws = _workspaceService.FindWorkspaceBySessionId(sessionId);
        if (ws is not null)
        {
            ws.NeedsAttention = false;
            ws.LastOscMessage = string.Empty;
            if (ws.AgentState == AgentState.WaitingForInput)
                ws.AgentState = AgentState.Idle;
        }
    }

    public void MarkAsRead(NotificationEntry entry)
    {
        entry.IsRead = true;
        UpdateUnreadCount();
    }

    public void MarkAllAsRead()
    {
        foreach (var n in Notifications)
            n.IsRead = true;
        UpdateUnreadCount();
    }

    public NotificationEntry? GetMostRecentUnread()
        => Notifications.FirstOrDefault(n => !n.IsRead);

    private void UpdateUnreadCount()
        => UnreadCount = Notifications.Count(n => !n.IsRead);
}
