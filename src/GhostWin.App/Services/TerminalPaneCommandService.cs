using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.App.Services;

public interface ITerminalPaneCommandService
{
    void SplitFocused(SplitOrientation direction);
    void CloseFocused();
    void MoveFocus(FocusDirection direction);
    void SplitPane(uint workspaceId, uint paneId, SplitOrientation direction);
    void ClosePane(uint workspaceId, uint paneId);
    uint? ToggleZoom(uint workspaceId, uint paneId);
    uint? GetZoomedPaneId(uint workspaceId);
    void ClearZoom(uint workspaceId);
}

public sealed class TerminalPaneCommandService : ITerminalPaneCommandService
{
    private readonly IWorkspaceService _workspaces;
    private readonly Dictionary<uint, uint> _zoomedPaneByWorkspace = [];

    public TerminalPaneCommandService(IWorkspaceService workspaces)
    {
        _workspaces = workspaces;
    }

    public void SplitFocused(SplitOrientation direction)
    {
        if (_workspaces.ActiveWorkspaceId is { } workspaceId)
            ClearZoom(workspaceId);

        _workspaces.ActivePaneLayout?.SplitFocused(direction);
    }

    public void CloseFocused()
    {
        if (_workspaces.ActiveWorkspaceId is { } workspaceId)
            ClearZoom(workspaceId);

        _workspaces.ActivePaneLayout?.CloseFocused();
    }

    public void MoveFocus(FocusDirection direction)
    {
        _workspaces.ActivePaneLayout?.MoveFocus(direction);
    }

    public void SplitPane(uint workspaceId, uint paneId, SplitOrientation direction)
    {
        var layout = _workspaces.GetPaneLayout(workspaceId);
        if (layout == null)
            return;

        ClearZoom(workspaceId);
        layout.SetFocused(paneId);
        layout.SplitFocused(direction);
    }

    public void ClosePane(uint workspaceId, uint paneId)
    {
        var layout = _workspaces.GetPaneLayout(workspaceId);
        if (layout == null)
            return;

        ClearZoom(workspaceId);
        layout.SetFocused(paneId);
        layout.CloseFocused();
    }

    public uint? ToggleZoom(uint workspaceId, uint paneId)
    {
        if (_zoomedPaneByWorkspace.TryGetValue(workspaceId, out var current) &&
            current == paneId)
        {
            _zoomedPaneByWorkspace.Remove(workspaceId);
            return null;
        }

        _zoomedPaneByWorkspace[workspaceId] = paneId;
        _workspaces.GetPaneLayout(workspaceId)?.SetFocused(paneId);
        return paneId;
    }

    public uint? GetZoomedPaneId(uint workspaceId) =>
        _zoomedPaneByWorkspace.TryGetValue(workspaceId, out var paneId) ? paneId : null;

    public void ClearZoom(uint workspaceId)
    {
        _zoomedPaneByWorkspace.Remove(workspaceId);
    }
}
