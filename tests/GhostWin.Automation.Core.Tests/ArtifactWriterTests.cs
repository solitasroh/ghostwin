using FluentAssertions;
using GhostWin.Automation.Core;

namespace GhostWin.Automation.Core.Tests;

public sealed class ArtifactWriterTests
{
    [Fact]
    public void Constructor_creates_artifact_directory()
    {
        var artifactDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "artifacts");

        _ = new ArtifactWriter(artifactDir);

        Directory.Exists(artifactDir).Should().BeTrue();
    }

    [Fact]
    public void WriteText_writes_named_artifact_inside_artifact_directory()
    {
        var artifactDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "artifacts");
        var writer = new ArtifactWriter(artifactDir);

        var path = writer.WriteText("uia-tree.tsv", "ControlType\tAutomationId");

        path.Should().Be(Path.Combine(artifactDir, "uia-tree.tsv"));
        File.ReadAllText(path).Should().Be("ControlType\tAutomationId");
    }

    [Fact]
    public void WriteTestResult_writes_structured_json_result()
    {
        var artifactDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "artifacts");
        var writer = new ArtifactWriter(artifactDir);

        var path = writer.WriteTestResult("Timeout", false, "main window missing");

        File.ReadAllText(path).Should().Contain("\"name\": \"Timeout\"");
        File.ReadAllText(path).Should().Contain("\"passed\": false");
        File.ReadAllText(path).Should().Contain("\"message\": \"main window missing\"");
    }

    [Fact]
    public void Standard_artifact_helpers_write_expected_file_names()
    {
        var artifactDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "artifacts");
        var writer = new ArtifactWriter(artifactDir);

        var uiaTree = writer.WriteUiaTree("ControlType\tAutomationId");
        var appState = writer.WriteAppState(new { Ready = true });
        var log = writer.WriteGhostWinLog("log line");
        var screenshot = writer.WriteScreenshotPng([0x89, 0x50, 0x4E, 0x47]);

        uiaTree.Should().Be(Path.Combine(artifactDir, "uia-tree.tsv"));
        appState.Should().Be(Path.Combine(artifactDir, "app-state.json"));
        log.Should().Be(Path.Combine(artifactDir, "ghostwin.log"));
        screenshot.Should().Be(Path.Combine(artifactDir, "screenshot.png"));
        File.ReadAllBytes(screenshot).Should().Equal(0x89, 0x50, 0x4E, 0x47);
    }

    [Fact]
    public void WriteText_rejects_paths_that_escape_to_sibling_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var writer = new ArtifactWriter(Path.Combine(root, "artifacts"));

        var act = () => writer.WriteText(@"..\artifacts2\leak.txt", "leak");

        act.Should().Throw<ArgumentException>()
            .WithParameterName("fileName");
    }
}
