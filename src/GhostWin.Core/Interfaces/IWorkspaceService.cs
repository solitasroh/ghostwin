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

    /// <summary>
    /// M-11 Session Restore — 스냅샷으로부터 워크스페이스 목록을 일괄 복원한다.
    /// 호출 전 <see cref="Workspaces"/> 는 비어 있어야 한다 (비어있지 않으면 <see cref="InvalidOperationException"/>).
    /// 복원 전용 — <c>App.OnStartup</c> 에서만 호출. <c>MainWindow.Show()</c> 이전 UI 스레드 단독 실행.
    /// 각 leaf 는 새 sessionId 를 발급받으며, 타이틀 미러 배선은 <see cref="CreateWorkspace"/> 와 동일.
    /// </summary>
    /// <param name="snapshot">로드된 세션 스냅샷. <c>Workspaces</c> 가 비어있으면 noop.</param>
    void RestoreFromSnapshot(SessionSnapshot snapshot);

    WorkspaceInfo? FindWorkspaceBySessionId(uint sessionId);

    /// <summary>
    /// M-16-D D-08: reorder a workspace within the sidebar list.
    /// The underlying entry instance is preserved (only the position in
    /// <see cref="Workspaces"/> changes), so HwndHost children survive
    /// without being recreated. Emits WorkspaceReorderedMessage.
    /// newIndex is clamped to [0, Count-1]. No-op when the move is idempotent.
    /// </summary>
    void MoveWorkspace(uint workspaceId, int newIndex);

    /// <summary>
    /// M-16-D D-02: rename a workspace via the sidebar context menu.
    /// Updates <see cref="WorkspaceInfo.Name"/> and emits the standard
    /// PropertyChanged so the sidebar reflects the new label without
    /// rebuilding the visual tree.
    /// </summary>
    void RenameWorkspace(uint workspaceId, string newName);
}
