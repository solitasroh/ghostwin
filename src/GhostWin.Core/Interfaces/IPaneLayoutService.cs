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
    (uint sessionId, uint newPaneId)? SplitFocused(SplitOrientation direction);
    void CloseFocused();
    void MoveFocus(FocusDirection direction);
    void SetFocused(uint paneId);

    void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx);
    void OnPaneResized(uint paneId, uint widthPx, uint heightPx);
}
