using GhostWin.Core.Models;

namespace GhostWin.Core.Interfaces;

public interface IPaneLayoutService
{
    IReadOnlyPaneNode? Root { get; }
    uint? FocusedPaneId { get; }
    /// <summary>SessionId of the focused leaf, or null if no focus / branch focused.</summary>
    uint? FocusedSessionId { get; }
    int LeafCount { get; }

    void Initialize(uint initialSessionId, uint initialSurfaceId);
    (uint sessionId, uint newPaneId)? SplitFocused(SplitOrientation direction);
    void CloseFocused();
    void MoveFocus(FocusDirection direction);
    void SetFocused(uint paneId);

    void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx);
    void OnPaneResized(uint paneId, uint widthPx, uint heightPx);
}
