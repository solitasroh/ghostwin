namespace GhostWin.Core.Models;

public interface IReadOnlyPaneNode
{
    uint Id { get; }
    uint? SessionId { get; }
    SplitOrientation? SplitDirection { get; }
    IReadOnlyPaneNode? Left { get; }
    IReadOnlyPaneNode? Right { get; }
    double Ratio { get; }
    bool IsLeaf { get; }
}
