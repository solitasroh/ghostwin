namespace GhostWin.Core.Models;

public enum TerminalPaneSurfaceStatus
{
    Pending,
    Attached,
    Failed,
}

public sealed record TerminalPaneSurfaceFailure(
    uint PaneId,
    uint SessionId,
    uint WidthPx,
    uint HeightPx,
    int Attempt,
    string Reason);

public sealed record TerminalPaneSurfaceState(
    TerminalPaneSurfaceStatus Status,
    uint SurfaceId,
    nint LastHwnd,
    uint LastWidthPx,
    uint LastHeightPx,
    TerminalPaneSurfaceFailure? Failure)
{
    public static TerminalPaneSurfaceState Pending { get; } =
        new(TerminalPaneSurfaceStatus.Pending, 0, 0, 0, 0, null);
}
