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
}
