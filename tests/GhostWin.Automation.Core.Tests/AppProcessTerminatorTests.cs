using FluentAssertions;
using GhostWin.Automation.Core;

namespace GhostWin.Automation.Core.Tests;

public sealed class AppProcessTerminatorTests
{
    [Fact]
    public void Terminate_requests_graceful_close_for_started_process()
    {
        var process = new FakeStartedProcess(waitForExitResult: true);
        var terminator = new AppProcessTerminator();

        terminator.Terminate(process, TimeSpan.FromSeconds(1));

        process.CloseMainWindowCalls.Should().Be(1);
        process.WaitForExitCalls.Should().Be(1);
        process.KillCalls.Should().Be(0);
    }

    [Fact]
    public void Terminate_kills_started_process_when_graceful_close_times_out()
    {
        var process = new FakeStartedProcess(waitForExitResult: false);
        var terminator = new AppProcessTerminator();

        terminator.Terminate(process, TimeSpan.FromSeconds(1));

        process.CloseMainWindowCalls.Should().Be(1);
        process.KillCalls.Should().Be(1);
        process.WaitForExitCalls.Should().Be(2);
    }

    [Fact]
    public void Terminate_does_not_touch_process_that_already_exited()
    {
        var process = new FakeStartedProcess(waitForExitResult: true)
        {
            HasExited = true
        };
        var terminator = new AppProcessTerminator();

        terminator.Terminate(process, TimeSpan.FromSeconds(1));

        process.CloseMainWindowCalls.Should().Be(0);
        process.WaitForExitCalls.Should().Be(0);
        process.KillCalls.Should().Be(0);
    }

    [Fact]
    public void TerminateByPid_resolves_only_the_requested_pid()
    {
        var process = new FakeStartedProcess(waitForExitResult: true);
        var resolvedPids = new List<int>();
        var terminator = new AppProcessTerminator(pid =>
        {
            resolvedPids.Add(pid);
            return process;
        });

        terminator.TerminateByPid(1234, TimeSpan.FromSeconds(1));

        resolvedPids.Should().Equal(1234);
        process.CloseMainWindowCalls.Should().Be(1);
    }

    [Fact]
    public void TerminateByPid_ignores_missing_pid()
    {
        var terminator = new AppProcessTerminator(_ => throw new ArgumentException("missing"));

        var act = () => terminator.TerminateByPid(1234, TimeSpan.FromSeconds(1));

        act.Should().NotThrow();
    }

    private sealed class FakeStartedProcess(bool waitForExitResult) : IStartedProcess
    {
        public bool HasExited { get; init; }

        public int CloseMainWindowCalls { get; private set; }

        public int WaitForExitCalls { get; private set; }

        public int KillCalls { get; private set; }

        public void CloseMainWindow()
        {
            CloseMainWindowCalls++;
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            WaitForExitCalls++;
            return waitForExitResult;
        }

        public void Kill()
        {
            KillCalls++;
        }
    }
}
