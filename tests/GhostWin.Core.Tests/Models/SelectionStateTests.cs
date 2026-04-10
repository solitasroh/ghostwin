using FluentAssertions;
using GhostWin.Core.Models;
using Xunit;

namespace GhostWin.Core.Tests.Models;

public class SelectionStateTests
{
    // ── CellCoord.Compare ──

    [Fact]
    [Trait("Category", "Unit")]
    public void CellCoord_Compare_SameCell_ReturnsZero()
    {
        var a = new CellCoord(5, 10);
        var b = new CellCoord(5, 10);
        CellCoord.Compare(a, b).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CellCoord_Compare_DifferentRow_ComparesByRow()
    {
        var early = new CellCoord(2, 10);
        var late = new CellCoord(5, 3);
        CellCoord.Compare(early, late).Should().BeNegative();
        CellCoord.Compare(late, early).Should().BePositive();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CellCoord_Compare_SameRow_ComparesByCol()
    {
        var left = new CellCoord(3, 2);
        var right = new CellCoord(3, 8);
        CellCoord.Compare(left, right).Should().BeNegative();
        CellCoord.Compare(right, left).Should().BePositive();
    }

    // ── SelectionRange.Contains ──

    [Fact]
    [Trait("Category", "Unit")]
    public void SelectionRange_Contains_CellInRange_ReturnsTrue()
    {
        var range = new SelectionRange(new CellCoord(1, 5), new CellCoord(3, 10));
        range.Contains(2, 0).Should().BeTrue();   // middle row, any col
        range.Contains(1, 5).Should().BeTrue();   // start cell
        range.Contains(3, 10).Should().BeTrue();  // end cell
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SelectionRange_Contains_CellOutOfRange_ReturnsFalse()
    {
        var range = new SelectionRange(new CellCoord(1, 5), new CellCoord(3, 10));
        range.Contains(0, 0).Should().BeFalse();  // before start
        range.Contains(4, 0).Should().BeFalse();  // after end
        range.Contains(1, 4).Should().BeFalse();  // same row, before start col
    }

    // ── SelectionState lifecycle ──

    [Fact]
    [Trait("Category", "Unit")]
    public void SelectionState_InitialState_IsInactive()
    {
        var state = new SelectionState();
        state.IsActive.Should().BeFalse();
        state.Mode.Should().Be(SelectionMode.None);
        state.CurrentRange.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SelectionState_Start_SetsActiveState()
    {
        var state = new SelectionState();
        state.Start(5, 10, SelectionMode.Cell);

        state.IsActive.Should().BeTrue();
        state.Mode.Should().Be(SelectionMode.Cell);
        state.Anchor.Should().Be(new CellCoord(5, 10));
        state.End.Should().Be(new CellCoord(5, 10));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SelectionState_Extend_UpdatesEnd()
    {
        var state = new SelectionState();
        state.Start(5, 10, SelectionMode.Cell);
        state.Extend(7, 3);

        state.End.Should().Be(new CellCoord(7, 3));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SelectionState_CurrentRange_NormalizesOrder()
    {
        var state = new SelectionState();
        // Start at (7, 3), end at (5, 10) — reversed
        state.Start(7, 3, SelectionMode.Cell);
        state.Extend(5, 10);

        var range = state.CurrentRange;
        range.Should().NotBeNull();
        range!.Value.Start.Should().Be(new CellCoord(5, 10));
        range!.Value.End.Should().Be(new CellCoord(7, 3));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SelectionState_Clear_ResetsToInactive()
    {
        var state = new SelectionState();
        state.Start(5, 10, SelectionMode.Word);
        state.Extend(5, 20);
        state.Clear();

        state.IsActive.Should().BeFalse();
        state.Mode.Should().Be(SelectionMode.None);
        state.CurrentRange.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SelectionState_Extend_WhileNone_DoesNothing()
    {
        var state = new SelectionState();
        state.Extend(5, 10); // no Start() call

        state.IsActive.Should().BeFalse();
        state.CurrentRange.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SelectionState_WordMode_PreservesMode()
    {
        var state = new SelectionState();
        state.Start(3, 0, SelectionMode.Word);
        state.Extend(3, 15);

        state.Mode.Should().Be(SelectionMode.Word);
        var range = state.CurrentRange;
        range!.Value.Start.Col.Should().Be(0);
        range!.Value.End.Col.Should().Be(15);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SelectionState_LineMode_PreservesMode()
    {
        var state = new SelectionState();
        state.Start(3, 0, SelectionMode.Line);
        state.Extend(5, 79);

        state.Mode.Should().Be(SelectionMode.Line);
        var range = state.CurrentRange;
        range!.Value.Start.Row.Should().Be(3);
        range!.Value.End.Row.Should().Be(5);
    }
}
