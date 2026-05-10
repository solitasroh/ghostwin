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
        TerminalPaneNodeViewModel? left,
        TerminalPaneNodeViewModel? right)
    {
        PaneId = paneId;
        SessionId = sessionId;
        SplitDirection = splitDirection;
        Ratio = ratio;
        IsFocused = isFocused;
        Left = left;
        Right = right;
    }

    public uint PaneId { get; }
    public uint? SessionId { get; }
    public SplitOrientation? SplitDirection { get; }
    public double Ratio { get; }
    public bool IsFocused { get; }
    public TerminalPaneNodeViewModel? Left { get; }
    public TerminalPaneNodeViewModel? Right { get; }
    public bool IsLeaf => Left == null && Right == null;

    public static TerminalPaneNodeViewModel FromReadOnlyNode(
        IReadOnlyPaneNode node,
        uint? focusedPaneId)
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
            FromReadOnlyNode(node.Left, focusedPaneId),
            FromReadOnlyNode(node.Right, focusedPaneId));
    }
}
