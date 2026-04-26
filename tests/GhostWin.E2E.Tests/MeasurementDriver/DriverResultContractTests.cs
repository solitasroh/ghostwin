using FluentAssertions;
using GhostWin.MeasurementDriver.Contracts;
using Xunit;

namespace GhostWin.E2E.Tests.MeasurementDriver;

public class DriverResultContractTests
{
    [Fact]
    public void Success_ResizeFourPane_SetsValidityFields()
    {
        var result = DriverResult.Success(
            scenario: "resize",
            mode: "4pane",
            observedPanes: 4);

        result.Valid.Should().BeTrue();
        result.ObservedPanes.Should().Be(4);
        result.Reason.Should().BeNull();
    }

    [Fact]
    public void Failure_ResizeFourPane_UsesExpectedMode()
    {
        var result = DriverResult.Failure(
            scenario: "resize",
            mode: "4pane",
            reason: "pane count mismatch (expected 4, observed 2)",
            observedPanes: 2);

        result.Mode.Should().Be("4pane");
        result.Valid.Should().BeFalse();
        result.ObservedPanes.Should().Be(2);
    }
}
