using GhostWin.Core.Models;

namespace GhostWin.Core.Interfaces;

public interface IPaneLayoutService
{
    IReadOnlyPaneNode? Root { get; }
    uint? FocusedPaneId { get; }
    int LeafCount { get; }

    void Initialize(uint initialSessionId, uint initialSurfaceId);
    (uint sessionId, uint newPaneId)? SplitFocused(SplitOrientation direction);
    void CloseFocused();
    void MoveFocus(FocusDirection direction);

    void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx);
    void OnPaneResized(uint paneId, uint widthPx, uint heightPx);
}
