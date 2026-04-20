using FluentAssertions;
using GhostWin.App.Input;
using Xunit;

namespace GhostWin.App.Tests.Input;

public class MouseCursorOracleFormatterTests
{
    [Fact]
    public void FormatShape_ReturnsExpectedShapeString()
    {
        var value = MouseCursorOracleFormatter.FormatShape(8);

        value.Should().Be("shape=8 (TEXT)");
    }

    [Fact]
    public void FormatCursorId_ReturnsExpectedCursorString()
    {
        var value = MouseCursorOracleFormatter.FormatCursorId(32513);

        value.Should().Be("cursorId=32513 (IDC_IBEAM)");
    }

    [Fact]
    public void FormatSessionId_ReturnsExpectedSessionString()
    {
        var value = MouseCursorOracleFormatter.FormatSessionId(3);

        value.Should().Be("sessionId=3");
    }

    [Fact]
    public void FormatShape_FallsBackToUnknownName()
    {
        var value = MouseCursorOracleFormatter.FormatShape(999);

        value.Should().Be("shape=999 (UNKNOWN)");
    }

    [Fact]
    public void OracleStateUpdate_RefreshesAllProbeStrings()
    {
        var state = new MouseCursorOracleState();

        state.Update(sessionId: 3, shape: 8, cursorId: 32513);

        state.ShapeText.Should().Be("shape=8 (TEXT)");
        state.CursorIdText.Should().Be("cursorId=32513 (IDC_IBEAM)");
        state.SessionText.Should().Be("sessionId=3");
    }
}
