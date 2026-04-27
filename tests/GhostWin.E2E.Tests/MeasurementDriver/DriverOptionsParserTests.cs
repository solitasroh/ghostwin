using FluentAssertions;
using GhostWin.MeasurementDriver.Contracts;
using Xunit;

namespace GhostWin.E2E.Tests.MeasurementDriver;

public class DriverOptionsParserTests
{
    [Fact]
    public void Parse_ResizeFourPane_ParsesPidAndOutputPath()
    {
        var args = new[]
        {
            "--scenario", "resize-4pane",
            "--pid", "4242",
            "--output-json", "C:\\temp\\m15-driver.json"
        };

        var options = DriverOptions.Parse(args);

        options.Scenario.Should().Be("resize-4pane");
        options.GhostWinPid.Should().Be(4242);
        options.OutputJsonPath.Should().Be("C:\\temp\\m15-driver.json");
    }

    [Fact]
    public void Parse_LoadWithoutWorkload_UsesDefaultSystem32Workload()
    {
        var args = new[]
        {
            "--scenario", "load",
            "--pid", "5151",
            "--output-json", "C:\\temp\\driver.json"
        };

        var options = DriverOptions.Parse(args);

        options.Workload.Should().Be("Get-ChildItem -Recurse C:\\Windows\\System32 | Format-List");
    }
}
