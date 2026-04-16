namespace GhostWin.Core.Models;

public record HookMessage(
    string Event,
    string? SessionId,
    string? Cwd,
    HookData? Data);

public record HookData(
    string? StopHookReason,
    string? NotificationType,
    string? Message,
    string? Status);
