using GhostWin.Core.Interfaces;

namespace GhostWin.Services;

public sealed class TerminalSurfaceCoordinator : ITerminalSurfaceCoordinator
{
    private readonly IWorkspaceService _workspaces;

    public TerminalSurfaceCoordinator(IWorkspaceService workspaces)
    {
        _workspaces = workspaces;
    }

    public void OnHostReady(uint workspaceId, uint paneId, nint hwnd, uint widthPx, uint heightPx)
    {
        GetSurfaceLayout(workspaceId)?.AttachHostSurface(paneId, hwnd, widthPx, heightPx);
    }

    public void OnHostResized(uint workspaceId, uint paneId, uint widthPx, uint heightPx)
    {
        GetSurfaceLayout(workspaceId)?.ResizeHostSurface(paneId, widthPx, heightPx);
    }

    public void FocusPane(uint workspaceId, uint paneId)
    {
        if (workspaceId == 0 || workspaceId != _workspaces.ActiveWorkspaceId)
            return;

        _workspaces.GetPaneLayout(workspaceId)?.SetFocused(paneId);
    }

    private IPaneLayoutService? GetLayout(uint workspaceId)
    {
        if (workspaceId != 0)
            return _workspaces.GetPaneLayout(workspaceId);

        return _workspaces.ActivePaneLayout;
    }

    private ITerminalSurfaceLayout? GetSurfaceLayout(uint workspaceId) =>
        GetLayout(workspaceId) as ITerminalSurfaceLayout;
}
