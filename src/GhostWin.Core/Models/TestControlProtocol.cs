namespace GhostWin.Core.Models;

public sealed record TestControlRequest(
    string Command,
    string? RequestId = null,
    uint? SessionId = null,
    TestControlPayload? Data = null);

public sealed record TestControlPayload(
    string? CommandName = null,
    string? Osc = null,
    string? Message = null,
    string? Value = null);

public sealed record TestControlResponse(
    bool Ok,
    long StateVersion,
    object? Data = null,
    string? Error = null,
    string? RequestId = null)
{
    public static TestControlResponse Success(
        long stateVersion,
        object? data = null,
        string? requestId = null)
        => new(true, stateVersion, data, null, requestId);

    public static TestControlResponse Failure(
        long stateVersion,
        string error,
        string? requestId = null)
        => new(false, stateVersion, null, error, requestId);
}

public sealed record TestControlState(
    uint? ActiveSessionId,
    uint? ActiveWorkspaceId,
    uint? FocusedPaneId,
    uint? FocusedSessionId,
    int SessionCount,
    int WorkspaceCount,
    int PaneCount);
