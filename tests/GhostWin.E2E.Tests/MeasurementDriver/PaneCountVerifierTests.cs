using FluentAssertions;
using GhostWin.MeasurementDriver.Verification;
using Xunit;

namespace GhostWin.E2E.Tests.MeasurementDriver;

public class PaneCountVerifierTests
{
    [Fact]
    public void Evaluate_ReturnsFailure_WhenObservedPaneCountDiffers()
    {
        var result = PaneCountVerifier.Evaluate(expected: 4, observed: 2);

        result.Valid.Should().BeFalse();
        result.Reason.Should().Be("pane count mismatch (expected 4, observed 2)");
    }

    [Fact]
    public void Evaluate_ReturnsSuccess_WhenObservedPaneCountMatches()
    {
        var result = PaneCountVerifier.Evaluate(expected: 4, observed: 4);

        result.Valid.Should().BeTrue();
        result.Reason.Should().BeNull();
    }
}
