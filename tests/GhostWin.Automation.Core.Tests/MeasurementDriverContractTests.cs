using FluentAssertions;
using GhostWin.Automation.Runner.Measurement.Contracts;
using GhostWin.Automation.Runner.Measurement.Verification;

namespace GhostWin.Automation.Core.Tests;

public sealed class MeasurementDriverContractTests
{
    [Fact]
    public void DriverOptions_parse_resize_four_pane_pid_and_output_path()
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
    public void DriverOptions_parse_pane_split_churn_artifact_dir()
    {
        var args = new[]
        {
            "--scenario", "pane-split-churn",
            "--pid", "6262",
            "--output-json", "C:\\temp\\driver.json",
            "--artifact-dir", "C:\\temp\\artifacts"
        };

        var options = DriverOptions.Parse(args);

        options.Scenario.Should().Be("pane-split-churn");
        options.ArtifactDir.Should().Be("C:\\temp\\artifacts");
    }

    [Fact]
    public void DriverOptions_parse_load_without_workload_uses_default_system32_workload()
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

    [Fact]
    public void DriverResult_success_sets_validity_fields()
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
    public void DriverResult_success_can_include_actions_and_artifacts()
    {
        var result = DriverResult.Success(
            scenario: "pane-split-churn",
            mode: "4pane",
            observedPanes: 4,
            observedActions: 3,
            artifacts: ["driver-events.csv", "pane-geometry.json"]);

        result.Valid.Should().BeTrue();
        result.ObservedActions.Should().Be(3);
        result.Artifacts.Should().ContainInOrder("driver-events.csv", "pane-geometry.json");
    }

    [Fact]
    public void DriverResult_failure_uses_expected_mode()
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

    [Fact]
    public void PaneCountVerifier_returns_failure_when_observed_pane_count_differs()
    {
        var result = PaneCountVerifier.Evaluate(expected: 4, observed: 2);

        result.Valid.Should().BeFalse();
        result.Reason.Should().Be("pane count mismatch (expected 4, observed 2)");
    }

    [Fact]
    public void PaneCountVerifier_returns_success_when_observed_pane_count_matches()
    {
        var result = PaneCountVerifier.Evaluate(expected: 4, observed: 4);

        result.Valid.Should().BeTrue();
        result.Reason.Should().BeNull();
    }
}
