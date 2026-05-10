using FluentAssertions;
using GhostWin.App.ViewModels;
using GhostWin.Core.Models;
using Xunit;

namespace GhostWin.App.Tests.ViewModels;

public class TerminalPaneNodeViewModelTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void FromReadOnlyNode_ProjectsLeafPaneIdentityAndFocus()
    {
        var leaf = PaneNode.CreateLeaf(id: 7, sessionId: 42);

        var vm = TerminalPaneNodeViewModel.FromReadOnlyNode(leaf, focusedPaneId: 7);

        vm.IsLeaf.Should().BeTrue();
        vm.PaneId.Should().Be(7u);
        vm.SessionId.Should().Be(42u);
        vm.IsFocused.Should().BeTrue();
        vm.SplitDirection.Should().BeNull();
        vm.Left.Should().BeNull();
        vm.Right.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FromReadOnlyNode_ProjectsSplitTreeWithoutLosingPaneState()
    {
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
        root.Split(
            SplitOrientation.Vertical,
            newSessionId: 20,
            oldLeafId: 2,
            newLeafId: 3);
        root.Ratio = 0.35;

        var vm = TerminalPaneNodeViewModel.FromReadOnlyNode(root, focusedPaneId: 3);

        vm.IsLeaf.Should().BeFalse();
        vm.PaneId.Should().Be(1u);
        vm.SessionId.Should().BeNull();
        vm.SplitDirection.Should().Be(SplitOrientation.Vertical);
        vm.Ratio.Should().Be(0.35);

        vm.Left.Should().NotBeNull();
        vm.Left!.PaneId.Should().Be(2u);
        vm.Left.SessionId.Should().Be(10u);
        vm.Left.IsFocused.Should().BeFalse();

        vm.Right.Should().NotBeNull();
        vm.Right!.PaneId.Should().Be(3u);
        vm.Right.SessionId.Should().Be(20u);
        vm.Right.IsFocused.Should().BeTrue();
    }
}
