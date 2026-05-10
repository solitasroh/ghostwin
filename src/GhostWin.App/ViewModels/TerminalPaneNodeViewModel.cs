using GhostWin.Core.Models;

namespace GhostWin.App.ViewModels;

public sealed class TerminalPaneNodeViewModel
{
    private TerminalPaneNodeViewModel(
        uint paneId,
        uint? sessionId,
        SplitOrientation? splitDirection,
        double ratio,
        bool isFocused,
        TerminalPaneSurfaceState? surfaceState,
        TerminalPaneNodeViewModel? left,
        TerminalPaneNodeViewModel? right)
    {
        PaneId = paneId;
        SessionId = sessionId;
        SplitDirection = splitDirection;
        Ratio = ratio;
        IsFocused = isFocused;
        SurfaceState = surfaceState;
        Left = left;
        Right = right;
    }

    public uint PaneId { get; }
    public uint? SessionId { get; }
    public SplitOrientation? SplitDirection { get; }
    public double Ratio { get; }
    public bool IsFocused { get; }
    public TerminalPaneSurfaceState? SurfaceState { get; }
    public TerminalPaneNodeViewModel? Left { get; }
    public TerminalPaneNodeViewModel? Right { get; }
    public bool IsLeaf => Left == null && Right == null;

    public static TerminalPaneNodeViewModel FromReadOnlyNode(
        IReadOnlyPaneNode node,
        uint? focusedPaneId,
        Func<uint, TerminalPaneSurfaceState?>? getSurfaceState = null)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.IsLeaf)
        {
            return new TerminalPaneNodeViewModel(
                node.Id,
                node.SessionId,
                splitDirection: null,
                node.Ratio,
                isFocused: node.Id == focusedPaneId,
                surfaceState: getSurfaceState?.Invoke(node.Id),
                left: null,
                right: null);
        }

        if (node.Left == null || node.Right == null || node.SplitDirection == null)
            throw new InvalidOperationException("Split pane node must have Left, Right, and SplitDirection");

        return new TerminalPaneNodeViewModel(
            node.Id,
            sessionId: null,
            node.SplitDirection,
            node.Ratio,
            isFocused: node.Id == focusedPaneId,
            surfaceState: null,
            FromReadOnlyNode(node.Left, focusedPaneId, getSurfaceState),
            FromReadOnlyNode(node.Right, focusedPaneId, getSurfaceState));
    }
}
