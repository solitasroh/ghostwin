using System.Diagnostics;
using FlaUI.UIA3;
using FlaUIApplication = FlaUI.Core.Application;

namespace GhostWin.Automation.Core;

public sealed class AppLauncher
{
    public const string AppExeEnvironmentVariable = "GHOSTWIN_APP_EXE";

    private static readonly string[] RequiredNativeDlls =
    [
        "ghostwin_engine.dll",
        "ghostty-vt.dll"
    ];

    private readonly string _repoRoot;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, DateTimeOffset> _getLastWriteTimeUtc;
    private readonly Func<ProcessStartInfo, ILaunchedApplication> _launchApplication;

    public AppLauncher(
        string repoRoot,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, bool>? fileExists = null,
        Func<string, DateTimeOffset>? getLastWriteTimeUtc = null,
        Func<ProcessStartInfo, ILaunchedApplication>? launchApplication = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        _repoRoot = repoRoot;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _fileExists = fileExists ?? File.Exists;
        _getLastWriteTimeUtc = getLastWriteTimeUtc ??
            (path => new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
        _launchApplication = launchApplication ?? (startInfo => new FlaUiLaunchedApplication(FlaUIApplication.Launch(startInfo)));
    }

    public string ResolveExecutable()
    {
        var envPath = _getEnvironmentVariable(AppExeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envPath) && _fileExists(envPath))
        {
            return envPath;
        }

        var candidates = GetCandidatePaths();
        var existingExecutableCandidates = candidates
            .Where(_fileExists)
            .Select((path, index) => new
            {
                Path = path,
                Index = index,
                LastWriteTimeUtc = _getLastWriteTimeUtc(path)
            })
            .ToList();

        var runnableCandidates = existingExecutableCandidates
            .Where(candidate => HasRequiredNativeDlls(candidate.Path))
            .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
            .ThenBy(candidate => candidate.Index)
            .ToList();

        if (runnableCandidates.Count > 0)
            return runnableCandidates[0].Path;

        if (existingExecutableCandidates.Count > 0)
        {
            throw new FileNotFoundException(
                "GhostWin.App.exe was found, but no candidate directory contains the required native DLLs " +
                $"({string.Join(", ", RequiredNativeDlls)}). Rebuild GhostWin.App or set {AppExeEnvironmentVariable} " +
                "to a runnable output directory.",
                string.Join(Environment.NewLine, existingExecutableCandidates.Select(candidate => candidate.Path)));
        }

        throw new FileNotFoundException(
            $"GhostWin.App.exe not found. Set {AppExeEnvironmentVariable} or build GhostWin.App first.",
            string.Join(Environment.NewLine, candidates));
    }

    public ProcessStartInfo CreateStartInfo(AppSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var exePath = ResolveExecutable();
        var startInfo = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? _repoRoot,
            UseShellExecute = false
        };

        startInfo.Environment["GHOSTWIN_AUTOMATION"] = "1";
        startInfo.Environment["GHOSTWIN_AUTOMATION_RUN_ID"] = session.RunId;
        startInfo.Environment["GHOSTWIN_PROFILE_DIR"] = session.ProfileDir;
        startInfo.Environment["GHOSTWIN_ARTIFACT_DIR"] = session.ArtifactDir;
        startInfo.Environment["GHOSTWIN_HOOK_PIPE_NAME"] = GetHookPipeName(session);
        startInfo.Environment.Remove("GHOSTWIN_KEYDIAG");
        startInfo.Environment.Remove("GHOSTWIN_IMEDIAG");
        startInfo.Environment.Remove("GHOSTWIN_RENDERDIAG");

        return startInfo;
    }

    public static string GetHookPipeName(AppSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return $"ghostwin-hook-{session.RunId}";
    }

    public AppSession Launch(AppSession session, TimeSpan mainWindowTimeout)
    {
        ArgumentNullException.ThrowIfNull(session);

        var startInfo = CreateStartInfo(session);
        var app = _launchApplication(startInfo);
        var mainWindowHandle = IntPtr.Zero;
        try
        {
            mainWindowHandle = app.GetMainWindowHandle(mainWindowTimeout);
            if (mainWindowHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"GhostWin main window was not found within {mainWindowTimeout}.");
            }
        }
        catch
        {
            app.Close();
            app.Kill();
            app.WaitForExit(TimeSpan.FromSeconds(10));
            throw;
        }

        return session with
        {
            Pid = app.ProcessId,
            MainWindowHandle = mainWindowHandle
        };
    }

    private IReadOnlyList<string> GetCandidatePaths()
    {
        return
        [
            Path.Combine(_repoRoot, @"src\GhostWin.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\GhostWin.App.exe"),
            Path.Combine(_repoRoot, @"src\GhostWin.App\bin\Debug\net10.0-windows10.0.22621.0\win-x64\GhostWin.App.exe"),
            Path.Combine(_repoRoot, @"src\GhostWin.App\bin\Debug\net10.0-windows\win-x64\GhostWin.App.exe"),
            Path.Combine(_repoRoot, @"src\GhostWin.App\bin\Debug\net10.0-windows\GhostWin.App.exe"),
            Path.Combine(_repoRoot, @"src\GhostWin.App\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\GhostWin.App.exe"),
            Path.Combine(_repoRoot, @"src\GhostWin.App\bin\x64\Release\net10.0-windows\GhostWin.App.exe"),
            Path.Combine(_repoRoot, @"src\GhostWin.App\bin\Release\net10.0-windows\GhostWin.App.exe"),
            Path.Combine(_repoRoot, @"src\GhostWin.App\bin\x64\Debug\net10.0-windows\GhostWin.App.exe")
        ];
    }

    private bool HasRequiredNativeDlls(string exePath)
    {
        var directory = Path.GetDirectoryName(exePath);
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        return RequiredNativeDlls.All(dll => _fileExists(Path.Combine(directory, dll)));
    }
}

public interface ILaunchedApplication
{
    int ProcessId { get; }

    IntPtr GetMainWindowHandle(TimeSpan timeout);

    void Close();

    void Kill();

    bool WaitForExit(TimeSpan timeout);
}

internal sealed class FlaUiLaunchedApplication(FlaUIApplication application) : ILaunchedApplication
{
    public int ProcessId => application.ProcessId;

    public IntPtr GetMainWindowHandle(TimeSpan timeout)
    {
        using var automation = new UIA3Automation();
        var mainWindow = application.GetMainWindow(automation, timeout);
        return mainWindow?.Properties.NativeWindowHandle.ValueOrDefault ?? IntPtr.Zero;
    }

    public void Close()
    {
        application.Close();
    }

    public void Kill()
    {
        try
        {
            Process.GetProcessById(application.ProcessId).Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort cleanup; the process may have already exited.
        }
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        try
        {
            return Process.GetProcessById(application.ProcessId).WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch
        {
            return true;
        }
    }
}
