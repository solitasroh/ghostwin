using GhostWin.Core.Models;

namespace GhostWin.Core.Interfaces;

/// <summary>
/// Manages workspaces and their associated pane layouts. Each workspace owns
/// an independent pane tree (its own <see cref="IPaneLayoutService"/>).
/// Sidebar entries map 1:1 to workspaces (cmux model).
/// </summary>
public interface IWorkspaceService
{
    IReadOnlyList<WorkspaceInfo> Workspaces { get; }
    uint? ActiveWorkspaceId { get; }
    IPaneLayoutService? ActivePaneLayout { get; }

    /// <summary>
    /// Creates a new workspace with a single initial session and pane.
    /// Emits WorkspaceCreatedMessage and activates the workspace.
    /// </summary>
    uint CreateWorkspace();

    /// <summary>
    /// Closes a workspace and all sessions/panes within it.
    /// Emits WorkspaceClosedMessage. If the closed workspace was active,
    /// activates the most recently used remaining workspace.
    /// </summary>
    void CloseWorkspace(uint workspaceId);

    /// <summary>
    /// Switches the active workspace. Emits WorkspaceActivatedMessage so views
    /// can rebind to the new workspace's pane layout.
    /// </summary>
    void ActivateWorkspace(uint workspaceId);

    /// <summary>
    /// Returns the pane layout for a specific workspace, or null if not found.
    /// </summary>
    IPaneLayoutService? GetPaneLayout(uint workspaceId);
}
