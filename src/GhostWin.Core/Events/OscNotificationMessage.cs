namespace GhostWin.Core.Events;

public record OscNotificationMessage(uint SessionId, string Title, string Body);
