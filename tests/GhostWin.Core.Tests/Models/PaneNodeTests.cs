using FluentAssertions;
using GhostWin.Core.Models;
using Xunit;

namespace GhostWin.Core.Tests.Models;

public class PaneNodeTests
{
    // ── T-1 ──────────────────────────────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void Split_OnLeafNode_ProducesCorrectBranchStructure()
    {
        // Arrange
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);

        // Act
        var (oldLeaf, newLeaf) = root.Split(
            SplitOrientation.Vertical,
            newSessionId: 20,
            oldLeafId: 2,
            newLeafId: 3);

        // Assert — tuple is (oldLeaf, newLeaf)
        root.Left.Should().BeSameAs(oldLeaf);
        root.Right.Should().BeSameAs(newLeaf);

        // Branch node invariants
        root.IsLeaf.Should().BeFalse();
        root.SessionId.Should().BeNull();
        root.SplitDirection.Should().Be(SplitOrientation.Vertical);

        // Leaf node states
        oldLeaf.Id.Should().Be(2u);
        oldLeaf.SessionId.Should().Be(10u);
        oldLeaf.IsLeaf.Should().BeTrue();

        newLeaf.Id.Should().Be(3u);
        newLeaf.SessionId.Should().Be(20u);
        newLeaf.IsLeaf.Should().BeTrue();
    }

    // ── T-2 ──────────────────────────────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void Split_OnBranchNode_ThrowsInvalidOperationException()
    {
        // Arrange
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
        root.Split(SplitOrientation.Vertical, newSessionId: 20, oldLeafId: 2, newLeafId: 3);

        // Act
        Action act = () => root.Split(
            SplitOrientation.Horizontal,
            newSessionId: 30,
            oldLeafId: 4,
            newLeafId: 5);

        // Assert — lock-in the literal message from the source
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("Cannot split branch node");
    }

    // ── T-3a (2-pane, root as parent) ────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void RemoveLeaf_WhenParentIsRoot_ReturnsReparentedSurvivingSibling()
    {
        // Arrange
        //   root(id=1)
        //   /       \
        // left(2)   right(3, sid=20)
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
        root.Split(SplitOrientation.Vertical, newSessionId: 20, oldLeafId: 2, newLeafId: 3);
        var left = root.Left!;    // id=2, sid=10
        var right = root.Right!;  // id=3, sid=20

        // Act — remove left
        var newRoot = root.RemoveLeaf(left);

        // Assert — surviving sibling becomes the new root
        newRoot.Should().BeSameAs(right);   // reference equality: paneId preserved
        newRoot!.Id.Should().Be(3u);
        newRoot.SessionId.Should().Be(20u);
    }

    // ── T-3b (grandparent splice — critical invariant) ───────────
    [Fact]
    [Trait("Category", "Unit")]
    public void RemoveLeaf_GrandparentSplice_PreservesSurvivingSiblingPaneId()
    {
        // Arrange — 3-level tree
        //            root(id=1, sid=99 initially)
        //           /                        \
        //    branch(id=2)                 leafC(id=5, sid=30)
        //       /      \
        //  leafA(3,99) leafB(4,20)
        //
        // After 2 splits:
        //   1) root(sid=99).Split → root becomes branch with Left=old(id=2,sid=99), Right=new(id=5,sid=30)
        //   2) root.Left(sid=99).Split → becomes branch with Left=old(id=3,sid=99), Right=new(id=4,sid=20)
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 99);
        root.Split(SplitOrientation.Vertical, newSessionId: 30, oldLeafId: 2, newLeafId: 5);
        root.Left!.Split(SplitOrientation.Horizontal, newSessionId: 20, oldLeafId: 3, newLeafId: 4);

        var leafA = root.Left!.Left!;   // id=3
        var leafB = root.Left!.Right!;  // id=4 — surviving sibling

        // Act — remove leafA. Expectation: grandparent(root).Left should splice to leafB
        var newRoot = root.RemoveLeaf(leafA);

        // Assert — root itself is unchanged
        newRoot.Should().BeSameAs(root);

        // CRITICAL: root.Left is exactly leafB (same object, NOT a copy)
        root.Left.Should().BeSameAs(leafB);
        root.Left!.Id.Should().Be(4u);          // paneId preserved — no reallocation
        root.Left.SessionId.Should().Be(20u);

        // leafC (Right) is untouched
        root.Right!.Id.Should().Be(5u);
        root.Right.SessionId.Should().Be(30u);
    }

    // ── T-3c (root removal) ─────────────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void RemoveLeaf_WhenLeafIsRoot_ReturnsNull()
    {
        // Arrange
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);

        // Act
        var newRoot = root.RemoveLeaf(root);

        // Assert — empty tree
        newRoot.Should().BeNull();
    }

    // ── T-4 (DFS 3-level) ───────────────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void GetLeaves_OnThreeLevelTree_ReturnsDfsOrderLeftFirst()
    {
        // Arrange
        //            root(id=1, sid=99 initially)
        //           /                        \
        //       branch(id=2)              leafR(id=5, sid=40)
        //       /       \
        //   leafLL(3,99) leafLR(4,20)
        //
        // Expected DFS: [id=3, id=4, id=5], [sid=99, sid=20, sid=40]
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 99);
        root.Split(SplitOrientation.Vertical, newSessionId: 40, oldLeafId: 2, newLeafId: 5);
        root.Left!.Split(SplitOrientation.Horizontal, newSessionId: 20, oldLeafId: 3, newLeafId: 4);

        // Act
        var leaves = root.GetLeaves().ToArray();

        // Assert
        leaves.Should().HaveCount(3);
        leaves.Select(n => n.Id).Should().ContainInOrder(3u, 4u, 5u);
        leaves.Select(n => n.SessionId).Should().ContainInOrder(99u, 20u, 40u);
    }

    // ── T-4b (single leaf) ─────────────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void GetLeaves_OnSingleLeafTree_ReturnsSelfOnly()
    {
        // Arrange
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);

        // Act
        var leaves = root.GetLeaves().ToArray();

        // Assert
        leaves.Should().HaveCount(1);
        leaves[0].Should().BeSameAs(root);
    }

    // ── T-5a (positive) ────────────────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void FindLeaf_WithExistingSessionId_ReturnsCorrectLeaf()
    {
        // Arrange
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
        root.Split(SplitOrientation.Vertical, newSessionId: 20, oldLeafId: 2, newLeafId: 3);

        // Act
        var found = root.FindLeaf(20);

        // Assert
        found.Should().NotBeNull();
        found!.SessionId.Should().Be(20u);
        found.Id.Should().Be(3u);
    }

    // ── T-5b (negative — Plan's original T-5) ──────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void FindLeaf_WithNonExistentSessionId_ReturnsNull()
    {
        // Arrange
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
        root.Split(SplitOrientation.Vertical, newSessionId: 20, oldLeafId: 2, newLeafId: 3);

        // Act
        var found = root.FindLeaf(999);

        // Assert
        found.Should().BeNull();
    }
}
