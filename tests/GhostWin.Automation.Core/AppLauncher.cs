using System.Diagnostics;
using FlaUI.UIA3;
using FlaUIApplication = FlaUI.Core.Application;

namespace GhostWin.Automation.Core;

public sealed class AppLauncher
{
    public const string AppExeEnvironmentVariable = "GHOSTWIN_APP_EXE";

    private readonly string _repoRoot;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<ProcessStartInfo, ILaunchedApplication> _launchApplication;

    public AppLauncher(
        string repoRoot,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, bool>? fileExists = null,
        Func<ProcessStartInfo, ILaunchedApplication>? launchApplication = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        _repoRoot = repoRoot;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _fileExists = fileExists ?? File.Exists;
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
        foreach (var candidate in candidates)
        {
            if (_fileExists(candidate))
            {
                return candidate;
            }
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

        return startInfo;
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
}

public interface ILaunchedApplication
{
    int ProcessId { get; }

    IntPtr GetMainWindowHandle(TimeSpan timeout);

    void Close();

    void Kill();
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
            Process.GetProcessById(application.ProcessId).Kill();
        }
        catch
        {
            // Best effort cleanup; the process may have already exited.
        }
    }
}
