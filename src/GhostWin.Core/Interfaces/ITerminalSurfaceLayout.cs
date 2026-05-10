namespace GhostWin.Core.Interfaces;

/// <summary>
/// Coordinator-only surface lifecycle contract for a pane layout.
/// General pane commands use <see cref="IPaneLayoutService"/>; HWND/surface
/// attach and resize flow through <see cref="ITerminalSurfaceCoordinator"/>.
/// </summary>
public interface ITerminalSurfaceLayout
{
    void AttachHostSurface(uint paneId, nint hwnd, uint widthPx, uint heightPx);
    void ResizeHostSurface(uint paneId, uint widthPx, uint heightPx);
    bool RetryHostSurface(uint paneId);
}
