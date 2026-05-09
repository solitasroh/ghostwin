using FluentAssertions;
using GhostWin.Automation.Core;

namespace GhostWin.Automation.Core.Tests;

public sealed class WaiterTests
{
    [Fact]
    public async Task WaitUntilAsync_returns_when_condition_becomes_true()
    {
        var clock = new ManualWaitClock();
        var waiter = new Waiter(clock);
        var attempts = 0;

        var result = await waiter.WaitUntilAsync(
            "ready",
            () => Task.FromResult(++attempts == 3),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10));

        result.Succeeded.Should().BeTrue();
        result.Reason.Should().Be("ready");
        attempts.Should().Be(3);
        clock.DelayCalls.Should().Be(2);
    }

    [Fact]
    public async Task WaitUntilAsync_writes_timeout_diagnostic_when_condition_never_matches()
    {
        var artifactDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "artifacts");
        var writer = new ArtifactWriter(artifactDir);
        var clock = new ManualWaitClock();
        var waiter = new Waiter(clock);

        var result = await waiter.WaitUntilAsync(
            "main window",
            () => Task.FromResult(false),
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(10),
            writer);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be("main window");
        File.Exists(Path.Combine(artifactDir, "wait-timeout-main-window.json")).Should().BeTrue();
    }

    [Fact]
    public async Task WaitUntilAsync_rejects_negative_timeout()
    {
        var waiter = new Waiter(new ManualWaitClock());

        var act = () => waiter.WaitUntilAsync(
            "ready",
            () => Task.FromResult(true),
            TimeSpan.FromMilliseconds(-1),
            TimeSpan.FromMilliseconds(10));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("timeout");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task WaitUntilAsync_rejects_non_positive_poll_interval(int milliseconds)
    {
        var waiter = new Waiter(new ManualWaitClock());

        var act = () => waiter.WaitUntilAsync(
            "ready",
            () => Task.FromResult(true),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(milliseconds));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("pollInterval");
    }
}
