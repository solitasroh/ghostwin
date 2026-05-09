namespace GhostWin.Automation.Core;

public sealed class Waiter
{
    private readonly IWaitClock _clock;

    public Waiter(IWaitClock? clock = null)
    {
        _clock = clock ?? new SystemWaitClock();
    }

    public async Task<WaitResult> WaitUntilAsync(
        string reason,
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan pollInterval,
        ArtifactWriter? artifactWriter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(condition);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be zero or positive.");
        }

        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "Poll interval must be positive.");
        }

        var startedAt = _clock.UtcNow;
        var attempts = 0;

        while (_clock.UtcNow - startedAt <= timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;

            if (await condition().ConfigureAwait(false))
            {
                return new WaitResult(true, reason, attempts, _clock.UtcNow - startedAt);
            }

            await _clock.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }

        var result = new WaitResult(false, reason, attempts, _clock.UtcNow - startedAt);
        artifactWriter?.WriteJson($"wait-timeout-{ToArtifactToken(reason)}.json", result);
        return result;
    }

    private static string ToArtifactToken(string reason)
    {
        return string.Join(
            "-",
            reason.Split(Path.GetInvalidFileNameChars().Concat([' ']).ToArray(), StringSplitOptions.RemoveEmptyEntries));
    }
}

public sealed record WaitResult(bool Succeeded, string Reason, int Attempts, TimeSpan Elapsed);

public interface IWaitClock
{
    DateTimeOffset UtcNow { get; }

    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemWaitClock : IWaitClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.Delay(delay, cancellationToken);
    }
}

public sealed class ManualWaitClock : IWaitClock
{
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

    public DateTimeOffset UtcNow => _utcNow;

    public int DelayCalls { get; private set; }

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DelayCalls++;
        _utcNow += delay;
        return Task.CompletedTask;
    }
}
