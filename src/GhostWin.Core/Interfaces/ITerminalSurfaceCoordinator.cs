namespace GhostWin.Core.Interfaces;

/// <summary>
/// Routes terminal host lifecycle events to the pane layout that owns the host.
/// The WPF view supplies raw host facts only; workspace/pane ownership decisions
/// live here so inactive cached hosts cannot mutate the active layout by mistake.
/// </summary>
public interface ITerminalSurfaceCoordinator
{
    void OnHostReady(uint workspaceId, uint paneId, nint hwnd, uint widthPx, uint heightPx);
    void OnHostResized(uint workspaceId, uint paneId, uint widthPx, uint heightPx);
    void RetryHostSurface(uint workspaceId, uint paneId);
    void FocusPane(uint workspaceId, uint paneId);
}
