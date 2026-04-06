namespace GhostWin.Core.Models;

public enum SplitOrientation { Horizontal, Vertical }

public class PaneNode
{
    private static uint _nextId = 1;

    public uint Id { get; init; } = _nextId++;
    public uint? SessionId { get; set; }
    public uint? SurfaceId { get; set; }
    public SplitOrientation? SplitDirection { get; set; }
    public PaneNode? Left { get; set; }
    public PaneNode? Right { get; set; }
    public double Ratio { get; set; } = 0.5;

    public bool IsLeaf => Left == null && Right == null;

    public static PaneNode CreateLeaf(uint sessionId) => new() { SessionId = sessionId };

    public PaneNode Split(SplitOrientation direction, uint newSessionId)
    {
        if (!IsLeaf) return this;

        var oldLeaf = new PaneNode
        {
            SessionId = SessionId,
            SurfaceId = SurfaceId,
        };

        var newLeaf = PaneNode.CreateLeaf(newSessionId);

        SplitDirection = direction;
        Left = oldLeaf;
        Right = newLeaf;
        SessionId = null;
        SurfaceId = null;

        return newLeaf;
    }

    public IEnumerable<PaneNode> GetLeaves()
    {
        if (IsLeaf) { yield return this; yield break; }
        if (Left != null) foreach (var l in Left.GetLeaves()) yield return l;
        if (Right != null) foreach (var l in Right.GetLeaves()) yield return l;
    }

    public PaneNode? FindLeaf(uint sessionId)
    {
        if (IsLeaf && SessionId == sessionId) return this;
        return Left?.FindLeaf(sessionId) ?? Right?.FindLeaf(sessionId);
    }

    public PaneNode? FindParent(PaneNode target)
    {
        if (Left == target || Right == target) return this;
        return Left?.FindParent(target) ?? Right?.FindParent(target);
    }

    public bool RemoveLeaf(PaneNode leaf)
    {
        var parent = FindParent(leaf);
        if (parent == null) return false;

        var surviving = parent.Left == leaf ? parent.Right : parent.Left;
        if (surviving == null) return false;

        // Replace parent's contents with surviving child
        parent.SessionId = surviving.SessionId;
        parent.SurfaceId = surviving.SurfaceId;
        parent.SplitDirection = surviving.SplitDirection;
        parent.Left = surviving.Left;
        parent.Right = surviving.Right;
        parent.Ratio = surviving.Ratio;

        return true;
    }
}
