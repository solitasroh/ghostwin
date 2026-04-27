using System.Collections.ObjectModel;
using GhostWin.Core.Models;

namespace GhostWin.Core.Interfaces;

public interface IOscNotificationService
{
    void HandleOscEvent(uint sessionId, string title, string body);
    void DismissAttention(uint sessionId);

    ObservableCollection<NotificationEntry> Notifications { get; }
    int UnreadCount { get; }
    void MarkAsRead(NotificationEntry entry);
    void MarkAllAsRead();
    NotificationEntry? GetMostRecentUnread();
}
