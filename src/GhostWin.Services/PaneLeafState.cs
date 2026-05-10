using GhostWin.Core.Models;

namespace GhostWin.Services;

public record PaneLeafState(
    uint PaneId,
    uint SessionId,
    uint SurfaceId,
    nint LastHwnd = default,
    uint LastWidthPx = 0,
    uint LastHeightPx = 0,
    int SurfaceCreateAttempts = 0,
    TerminalPaneSurfaceFailure? SurfaceFailure = null)
{
    public TerminalPaneSurfaceState ToSurfaceState()
    {
        var status = SurfaceFailure != null
            ? TerminalPaneSurfaceStatus.Failed
            : SurfaceId != 0
                ? TerminalPaneSurfaceStatus.Attached
                : TerminalPaneSurfaceStatus.Pending;

        return new TerminalPaneSurfaceState(
            status,
            SurfaceId,
            LastHwnd,
            LastWidthPx,
            LastHeightPx,
            SurfaceFailure);
    }
}
