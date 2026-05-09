namespace GhostWin.Automation.Core;

public sealed record AppSession
{
    public required string RunId { get; init; }

    public required string ProfileDir { get; init; }

    public required string ArtifactDir { get; init; }

    public int? Pid { get; init; }

    public IntPtr MainWindowHandle { get; init; }

    public void Terminate(AppProcessTerminator terminator, TimeSpan gracefulTimeout)
    {
        ArgumentNullException.ThrowIfNull(terminator);
        if (Pid is { } pid)
        {
            terminator.TerminateByPid(pid, gracefulTimeout);
        }
    }

    public static AppSession Create(string runId, string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        if (Path.IsPathRooted(runId) ||
            runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            runId.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part == ".."))
        {
            throw new ArgumentException("Run id must be a relative directory name inside the automation root.", nameof(runId));
        }

        var runRoot = Path.Combine(rootDirectory, runId);
        var profileDir = Path.Combine(runRoot, "profile");
        var artifactDir = Path.Combine(runRoot, "artifacts");

        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(artifactDir);

        return new AppSession
        {
            RunId = runId,
            ProfileDir = profileDir,
            ArtifactDir = artifactDir,
            MainWindowHandle = IntPtr.Zero
        };
    }
}
