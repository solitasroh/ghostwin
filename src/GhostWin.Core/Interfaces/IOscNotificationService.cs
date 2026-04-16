namespace GhostWin.Core.Interfaces;

public interface IOscNotificationService
{
    void HandleOscEvent(uint sessionId, string title, string body);
    void DismissAttention(uint sessionId);
}
