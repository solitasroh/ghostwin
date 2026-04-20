using FluentAssertions;
using GhostWin.App.Input;
using Xunit;

namespace GhostWin.App.Tests.Input;

public class MouseCursorShapeMapperTests
{
    [Theory]
    [InlineData(0, 32512)] // DEFAULT -> IDC_ARROW
    [InlineData(6, 32512)] // CELL -> IDC_ARROW
    [InlineData(8, 32513)] // TEXT -> IDC_IBEAM
    [InlineData(9, 32513)] // VERTICAL_TEXT -> IDC_IBEAM
    [InlineData(3, 32649)] // POINTER -> IDC_HAND
    [InlineData(12, 32649)] // MOVE -> IDC_HAND (pragmatic fallback)
    [InlineData(18, 32644)] // COL_RESIZE -> IDC_SIZEWE
    [InlineData(19, 32645)] // ROW_RESIZE -> IDC_SIZENS
    [InlineData(20, 32645)] // N_RESIZE -> IDC_SIZENS
    [InlineData(21, 32644)] // E_RESIZE -> IDC_SIZEWE
    [InlineData(24, 32643)] // NE_RESIZE -> IDC_SIZENESW
    [InlineData(25, 32642)] // NW_RESIZE -> IDC_SIZENWSE
    [InlineData(32, 32512)] // ZOOM_IN -> IDC_ARROW fallback
    [InlineData(33, 32512)] // ZOOM_OUT -> IDC_ARROW fallback
    public void MapToCursorId_ReturnsExpectedWin32CursorId(int ghosttyShape, int expectedCursorId)
    {
        var cursorId = MouseCursorShapeMapper.MapToCursorId(ghosttyShape);

        cursorId.Should().Be(expectedCursorId);
    }
}
