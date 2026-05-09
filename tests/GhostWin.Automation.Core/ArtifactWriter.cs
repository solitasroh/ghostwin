using System.Text.Json;

namespace GhostWin.Automation.Core;

public sealed class ArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ArtifactWriter(string artifactDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        ArtifactDirectory = artifactDirectory;
        Directory.CreateDirectory(ArtifactDirectory);
    }

    public string ArtifactDirectory { get; }

    public string WriteText(string fileName, string contents)
    {
        var path = GetArtifactPath(fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    public string WriteJson<T>(string fileName, T value)
    {
        return WriteText(fileName, JsonSerializer.Serialize(value, JsonOptions));
    }

    public string WriteUiaTree(string tsv)
    {
        return WriteText("uia-tree.tsv", tsv);
    }

    public string WriteAppState<T>(T value)
    {
        return WriteJson("app-state.json", value);
    }

    public string WriteGhostWinLog(string contents)
    {
        return WriteText("ghostwin.log", contents);
    }

    public string WriteScreenshotPng(byte[] bytes)
    {
        var path = GetArtifactPath("screenshot.png");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public string WriteTestResult(string name, bool passed, string? message = null)
    {
        return WriteJson("test-result.json", new
        {
            name,
            passed,
            message,
            writtenAtUtc = DateTimeOffset.UtcNow
        });
    }

    private string GetArtifactPath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.IsPathRooted(fileName))
        {
            throw new ArgumentException("Artifact file name must be relative.", nameof(fileName));
        }

        var root = Path.GetFullPath(ArtifactDirectory);
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." ||
            relative.StartsWith("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new ArgumentException("Artifact file name must stay inside the artifact directory.", nameof(fileName));
        }

        return path;
    }
}
