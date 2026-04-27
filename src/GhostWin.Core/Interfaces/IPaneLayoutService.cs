using GhostWin.Core.Models;

namespace GhostWin.Core.Interfaces;

public interface IPaneLayoutService
{
    IReadOnlyPaneNode? Root { get; }
    uint? FocusedPaneId { get; }
    /// <summary>SessionId of the focused leaf, or null if no focus / branch focused.</summary>
    uint? FocusedSessionId { get; }
    int LeafCount { get; }

    /// <summary>
    /// Creates the root pane leaf bound to <paramref name="initialSessionId"/>.
    /// The SurfaceId is a placeholder (0) until <see cref="OnHostReady"/> fires
    /// and the engine creates the real per-pane swapchain.
    /// </summary>
    void Initialize(uint initialSessionId);

    /// <summary>
    /// PaneSnapshot 트리로부터 재귀적으로 PaneNode 트리를 재구성.
    /// 각 leaf 에 대해 <paramref name="sessions"/>.<see cref="ISessionManager.CreateSession(string?, ushort, ushort)"/>
    /// 로 새 세션을 발급하고 내부 <c>_leaves</c> 상태를 채운다.
    /// M-11 Session Restore 복원 전용 — <see cref="Initialize"/> 와 배타적 (한 인스턴스에 둘 중 하나만 호출).
    /// </summary>
    /// <param name="rootSnap">스냅샷 최상위 PaneSnapshot (Leaf 또는 Split).</param>
    /// <param name="sessions">세션 매니저. CWD 포함 세션 발급에 사용.</param>
    void InitializeFromTree(PaneSnapshot rootSnap, ISessionManager sessions);

    (uint sessionId, uint newPaneId)? SplitFocused(SplitOrientation direction);
    void CloseFocused();
    void MoveFocus(FocusDirection direction);
    void SetFocused(uint paneId);

    void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx);
    void OnPaneResized(uint paneId, uint widthPx, uint heightPx);
}
