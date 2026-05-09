using FluentAssertions;
using GhostWin.Automation.Core;

namespace GhostWin.Automation.Core.Tests;

public sealed class AppSessionTests
{
    [Fact]
    public void Create_creates_isolated_profile_and_artifact_directories()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var session = AppSession.Create("abc123", root);

        session.RunId.Should().Be("abc123");
        session.ProfileDir.Should().Be(Path.Combine(root, "abc123", "profile"));
        session.ArtifactDir.Should().Be(Path.Combine(root, "abc123", "artifacts"));
        Directory.Exists(session.ProfileDir).Should().BeTrue();
        Directory.Exists(session.ArtifactDir).Should().BeTrue();
    }

    [Fact]
    public void FromLaunchedProcess_tracks_only_the_started_pid_and_main_window_handle()
    {
        var session = AppSession.Create(
            "abc123",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        var launched = session with
        {
            Pid = 1234,
            MainWindowHandle = new IntPtr(5678)
        };

        launched.Pid.Should().Be(1234);
        launched.MainWindowHandle.Should().Be(new IntPtr(5678));
    }

    [Fact]
    public void Terminate_uses_the_session_pid_only()
    {
        var session = AppSession.Create(
            "abc123",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))) with
        {
            Pid = 1234
        };
        var resolvedPids = new List<int>();
        var terminator = new AppProcessTerminator(pid =>
        {
            resolvedPids.Add(pid);
            return new FakeStartedProcess();
        });

        session.Terminate(terminator, TimeSpan.FromSeconds(1));

        resolvedPids.Should().Equal(1234);
    }

    [Theory]
    [InlineData(@"..\outside")]
    [InlineData(@"C:\outside")]
    public void Create_rejects_run_ids_that_escape_the_root_directory(string runId)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var act = () => AppSession.Create(runId, root);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("runId");
    }

    private sealed class FakeStartedProcess : IStartedProcess
    {
        public bool HasExited => true;

        public void CloseMainWindow()
        {
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            return true;
        }

        public void Kill()
        {
        }
    }
}
