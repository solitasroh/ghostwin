using FluentAssertions;
using GhostWin.Automation.Core;
using GhostWin.Core.Models;

namespace GhostWin.Automation.Core.Tests;

public sealed class RealAppSmokeTests
{
    [Fact]
    public void Launch_finds_main_window_and_terminates_started_pid_when_enabled()
    {
        if (Environment.GetEnvironmentVariable("GHOSTWIN_AUTOMATION_RUN_REAL_APP") != "1")
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "ghostwin-automation-smoke", Guid.NewGuid().ToString("N"));
        var session = AppSession.Create("real-app", root);
        var launcher = new AppLauncher(repoRoot);
        AppSession? launched = null;

        try
        {
            launched = launcher.Launch(session, TimeSpan.FromSeconds(10));

            launched.Pid.Should().NotBeNull();
            launched.MainWindowHandle.Should().NotBe(IntPtr.Zero);
        }
        finally
        {
            if (launched?.Pid is { } pid)
            {
                new AppProcessTerminator().TerminateByPid(pid, TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task Launch_enables_test_control_ipc_when_enabled()
    {
        if (Environment.GetEnvironmentVariable("GHOSTWIN_AUTOMATION_RUN_REAL_APP") != "1")
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "ghostwin-automation-smoke", Guid.NewGuid().ToString("N"));
        var session = AppSession.Create("real-app-ipc", root);
        var launcher = new AppLauncher(repoRoot);
        AppSession? launched = null;

        try
        {
            launched = launcher.Launch(session, TimeSpan.FromSeconds(10));

            var client = new TestControlClient(
                pipeName: AppLauncher.GetHookPipeName(session),
                connectTimeout: TimeSpan.FromSeconds(1));
            TestControlState? state = null;
            var ready = await new Waiter().WaitUntilAsync(
                "test-control ready",
                async () =>
                {
                    try
                    {
                        state = await client.GetStateAsync();
                        return state.WorkspaceCount > 0 &&
                               state.SessionCount > 0 &&
                               state.PaneCount > 0;
                    }
                    catch (IOException)
                    {
                        return false;
                    }
                    catch (TimeoutException)
                    {
                        return false;
                    }
                },
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(100),
                new ArtifactWriter(session.ArtifactDir));

            ready.Succeeded.Should().BeTrue();
            state.Should().NotBeNull();
            state!.ActiveWorkspaceId.Should().NotBeNull();
            state.ActiveSessionId.Should().NotBeNull();
        }
        finally
        {
            if (launched?.Pid is { } pid)
            {
                new AppProcessTerminator().TerminateByPid(pid, TimeSpan.FromSeconds(10));
            }
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GhostWin.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("GhostWin.sln was not found above the test output directory.");
    }
}
