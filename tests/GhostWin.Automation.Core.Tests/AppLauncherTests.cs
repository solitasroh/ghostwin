using FluentAssertions;
using GhostWin.Automation.Core;

namespace GhostWin.Automation.Core.Tests;

public sealed class AppLauncherTests
{
    [Fact]
    public void ResolveExecutable_prefers_existing_GHOSTWIN_APP_EXE()
    {
        var envPath = Path.Combine(Path.GetTempPath(), "GhostWin.App.exe");
        var launcher = new AppLauncher(
            repoRoot: @"C:\repo\ghostwin",
            getEnvironmentVariable: name => name == "GHOSTWIN_APP_EXE" ? envPath : null,
            fileExists: path => path == envPath);

        var resolved = launcher.ResolveExecutable();

        resolved.Should().Be(envPath);
    }

    [Fact]
    public void ResolveExecutable_uses_first_existing_candidate_when_env_is_missing()
    {
        var repoRoot = @"C:\repo\ghostwin";
        var expected = Path.Combine(
            repoRoot,
            @"src\GhostWin.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\GhostWin.App.exe");
        var expectedDir = Path.GetDirectoryName(expected)!;
        var launcher = new AppLauncher(
            repoRoot,
            getEnvironmentVariable: _ => null,
            fileExists: path =>
                path == expected ||
                path == Path.Combine(expectedDir, "ghostwin_engine.dll") ||
                path == Path.Combine(expectedDir, "ghostty-vt.dll"));

        var resolved = launcher.ResolveExecutable();

        resolved.Should().Be(expected);
    }

    [Fact]
    public void ResolveExecutable_prefers_x64_release_rid_candidate_over_legacy_release_candidate()
    {
        var repoRoot = @"C:\repo\ghostwin";
        var expected = Path.Combine(
            repoRoot,
            @"src\GhostWin.App\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\GhostWin.App.exe");
        var legacy = Path.Combine(
            repoRoot,
            @"src\GhostWin.App\bin\x64\Release\net10.0-windows\GhostWin.App.exe");
        var expectedDir = Path.GetDirectoryName(expected)!;
        var legacyDir = Path.GetDirectoryName(legacy)!;
        var launcher = new AppLauncher(
            repoRoot,
            getEnvironmentVariable: _ => null,
            fileExists: path =>
                path == expected ||
                path == legacy ||
                path == Path.Combine(expectedDir, "ghostwin_engine.dll") ||
                path == Path.Combine(expectedDir, "ghostty-vt.dll") ||
                path == Path.Combine(legacyDir, "ghostwin_engine.dll") ||
                path == Path.Combine(legacyDir, "ghostty-vt.dll"));

        var resolved = launcher.ResolveExecutable();

        resolved.Should().Be(expected);
    }

    [Fact]
    public void ResolveExecutable_uses_newest_existing_candidate()
    {
        var repoRoot = @"C:\repo\ghostwin";
        var stale = Path.Combine(
            repoRoot,
            @"src\GhostWin.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\GhostWin.App.exe");
        var fresh = Path.Combine(
            repoRoot,
            @"src\GhostWin.App\bin\Debug\net10.0-windows10.0.22621.0\win-x64\GhostWin.App.exe");
        var staleDir = Path.GetDirectoryName(stale)!;
        var freshDir = Path.GetDirectoryName(fresh)!;
        var launcher = new AppLauncher(
            repoRoot,
            getEnvironmentVariable: _ => null,
            fileExists: path =>
                path == stale ||
                path == fresh ||
                path == Path.Combine(staleDir, "ghostwin_engine.dll") ||
                path == Path.Combine(staleDir, "ghostty-vt.dll") ||
                path == Path.Combine(freshDir, "ghostwin_engine.dll") ||
                path == Path.Combine(freshDir, "ghostty-vt.dll"),
            getLastWriteTimeUtc: path => path == fresh
                ? DateTimeOffset.Parse("2026-05-09T00:00:00Z")
                : DateTimeOffset.Parse("2026-04-30T00:00:00Z"));

        var resolved = launcher.ResolveExecutable();

        resolved.Should().Be(fresh);
    }

    [Fact]
    public void ResolveExecutable_skips_candidate_without_native_dlls()
    {
        var repoRoot = @"C:\repo\ghostwin";
        var stale = Path.Combine(
            repoRoot,
            @"src\GhostWin.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\GhostWin.App.exe");
        var fresh = Path.Combine(
            repoRoot,
            @"src\GhostWin.App\bin\Debug\net10.0-windows10.0.22621.0\win-x64\GhostWin.App.exe");
        var freshDir = Path.GetDirectoryName(fresh)!;
        var launcher = new AppLauncher(
            repoRoot,
            getEnvironmentVariable: _ => null,
            fileExists: path =>
                path == stale ||
                path == fresh ||
                path == Path.Combine(freshDir, "ghostwin_engine.dll") ||
                path == Path.Combine(freshDir, "ghostty-vt.dll"),
            getLastWriteTimeUtc: path => path == stale
                ? DateTimeOffset.Parse("2026-05-09T00:00:00Z")
                : DateTimeOffset.Parse("2026-04-30T00:00:00Z"));

        var resolved = launcher.ResolveExecutable();

        resolved.Should().Be(fresh);
    }

    [Fact]
    public void CreateStartInfo_sets_working_directory_and_isolated_environment()
    {
        var exePath = Path.Combine(Path.GetTempPath(), "GhostWin.App.exe");
        var session = AppSession.Create(
            runId: "run-001",
            rootDirectory: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var launcher = new AppLauncher(
            repoRoot: @"C:\repo\ghostwin",
            getEnvironmentVariable: name => name == "GHOSTWIN_APP_EXE" ? exePath : null,
            fileExists: path => path == exePath);

        var startInfo = launcher.CreateStartInfo(session);

        startInfo.FileName.Should().Be(exePath);
        startInfo.WorkingDirectory.Should().Be(Path.GetDirectoryName(exePath));
        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.Environment["GHOSTWIN_AUTOMATION"].Should().Be("1");
        startInfo.Environment["GHOSTWIN_AUTOMATION_RUN_ID"].Should().Be("run-001");
        startInfo.Environment["GHOSTWIN_PROFILE_DIR"].Should().Be(session.ProfileDir);
        startInfo.Environment["GHOSTWIN_ARTIFACT_DIR"].Should().Be(session.ArtifactDir);
        startInfo.Environment["GHOSTWIN_HOOK_PIPE_NAME"].Should().Be("ghostwin-hook-run-001");
    }

    [Fact]
    public void CreateStartInfo_removes_inherited_diagnostic_file_sinks()
    {
        var previousKeyDiag = Environment.GetEnvironmentVariable("GHOSTWIN_KEYDIAG");
        var previousImeDiag = Environment.GetEnvironmentVariable("GHOSTWIN_IMEDIAG");
        var previousRenderDiag = Environment.GetEnvironmentVariable("GHOSTWIN_RENDERDIAG");
        try
        {
            Environment.SetEnvironmentVariable("GHOSTWIN_KEYDIAG", "3");
            Environment.SetEnvironmentVariable("GHOSTWIN_IMEDIAG", "1");
            Environment.SetEnvironmentVariable("GHOSTWIN_RENDERDIAG", "2");

            var exePath = Path.Combine(Path.GetTempPath(), "GhostWin.App.exe");
            var session = AppSession.Create(
                runId: "run-001",
                rootDirectory: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
            var launcher = new AppLauncher(
                repoRoot: @"C:\repo\ghostwin",
                getEnvironmentVariable: name => name == "GHOSTWIN_APP_EXE" ? exePath : null,
                fileExists: path => path == exePath);

            var startInfo = launcher.CreateStartInfo(session);

            startInfo.Environment.ContainsKey("GHOSTWIN_KEYDIAG").Should().BeFalse();
            startInfo.Environment.ContainsKey("GHOSTWIN_IMEDIAG").Should().BeFalse();
            startInfo.Environment.ContainsKey("GHOSTWIN_RENDERDIAG").Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("GHOSTWIN_KEYDIAG", previousKeyDiag);
            Environment.SetEnvironmentVariable("GHOSTWIN_IMEDIAG", previousImeDiag);
            Environment.SetEnvironmentVariable("GHOSTWIN_RENDERDIAG", previousRenderDiag);
        }
    }

    [Fact]
    public void Launch_kills_started_process_when_main_window_is_missing()
    {
        var session = AppSession.Create(
            "run-001",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var fakeApp = new FakeLaunchedApp(processId: 42, mainWindowHandle: IntPtr.Zero);
        var launcher = new AppLauncher(
            repoRoot: @"C:\repo\ghostwin",
            getEnvironmentVariable: name => name == "GHOSTWIN_APP_EXE" ? @"C:\repo\GhostWin.App.exe" : null,
            fileExists: _ => true,
            launchApplication: _ => fakeApp);

        var act = () => launcher.Launch(session, TimeSpan.FromSeconds(1));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*main window*");
        fakeApp.CloseCalls.Should().Be(1);
        fakeApp.KillCalls.Should().Be(1);
        fakeApp.WaitForExitCalls.Should().Be(1);
    }

    private sealed class FakeLaunchedApp(int processId, IntPtr mainWindowHandle) : ILaunchedApplication
    {
        public int ProcessId { get; } = processId;

        public int CloseCalls { get; private set; }

        public int KillCalls { get; private set; }

        public int WaitForExitCalls { get; private set; }

        public IntPtr GetMainWindowHandle(TimeSpan timeout)
        {
            return mainWindowHandle;
        }

        public void Close()
        {
            CloseCalls++;
        }

        public void Kill()
        {
            KillCalls++;
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            WaitForExitCalls++;
            return true;
        }
    }
}
