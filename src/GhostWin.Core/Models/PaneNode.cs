namespace GhostWin.Core.Models;

public enum SplitOrientation { Horizontal, Vertical }

public class PaneNode : IReadOnlyPaneNode
{
    public uint Id { get; init; }
    public uint? SessionId { get; set; }
    public SplitOrientation? SplitDirection { get; set; }
    public PaneNode? Left { get; set; }
    public PaneNode? Right { get; set; }
    public double Ratio { get; set; } = 0.5;

    public bool IsLeaf => Left == null && Right == null;

    // Explicit interface implementation for covariant return types
    IReadOnlyPaneNode? IReadOnlyPaneNode.Left => Left;
    IReadOnlyPaneNode? IReadOnlyPaneNode.Right => Right;

    public static PaneNode CreateLeaf(uint id, uint sessionId) => new() { Id = id, SessionId = sessionId };

    public (PaneNode oldLeaf, PaneNode newLeaf) Split(
        SplitOrientation direction, uint newSessionId, uint oldLeafId, uint newLeafId)
    {
        if (!IsLeaf) throw new InvalidOperationException("Cannot split branch node");

        var oldLeaf = new PaneNode { Id = oldLeafId, SessionId = SessionId };
        var newLeaf = new PaneNode { Id = newLeafId, SessionId = newSessionId };

        SplitDirection = direction;
        Left = oldLeaf;
        Right = newLeaf;
        SessionId = null;

        return (oldLeaf, newLeaf);
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

    public PaneNode? FindLeafById(uint paneId)
    {
        if (IsLeaf && Id == paneId) return this;
        return Left?.FindLeafById(paneId) ?? Right?.FindLeafById(paneId);
    }

    public PaneNode? FindParent(PaneNode target)
    {
        if (Left == target || Right == target) return this;
        return Left?.FindParent(target) ?? Right?.FindParent(target);
    }

    /// <summary>
    /// Removes <paramref name="leaf"/> from the tree by replacing its parent with the surviving sibling.
    /// Returns the new root (may differ from <c>this</c> when the original root's parent is removed,
    /// or <c>null</c> when the leaf to remove is the root itself).
    /// Crucially, leaf paneIds are preserved (sibling subtree is reparented intact, not copied),
    /// so external state keyed by paneId (e.g. surface dictionaries, host caches) remains valid.
    /// </summary>
    public PaneNode? RemoveLeaf(PaneNode leaf)
    {
        // Root is the leaf being removed → tree becomes empty.
        if (this == leaf) return null;

        var parent = FindParent(leaf);
        if (parent == null) return this;

        var surviving = parent.Left == leaf ? parent.Right : parent.Left;
        if (surviving == null) return this;

        // Parent is root → surviving becomes the new root, preserving its paneId.
        if (parent == this) return surviving;

        // Otherwise splice surviving into grandparent in place of parent.
        var grandparent = FindParent(parent);
        if (grandparent == null) return this; // unreachable: non-root parent must have a parent

        if (grandparent.Left == parent) grandparent.Left = surviving;
        else grandparent.Right = surviving;

        return this;
    }
}
