namespace Wondarr.Core.Metadata.Http;

/// <summary>
/// Hands out evenly spaced start slots so that callers of one host never exceed
/// <c>1 / minInterval</c> requests per second, however many callers there are and whichever
/// <see cref="HttpClient"/> they came from.
/// </summary>
/// <remarks>
/// A slot is reserved the moment <see cref="WaitAsync"/> is called, not when the delay elapses, so
/// N concurrent callers start at t, t + minInterval, t + 2·minInterval … The wait itself goes
/// through <see cref="TimeProvider"/>, so tests drive it with <c>FakeTimeProvider</c>.
/// </remarks>
public sealed class RequestSpacingGate
{
    private readonly TimeSpan _minInterval;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();

    private DateTimeOffset _nextFreeSlot = DateTimeOffset.MinValue;

    /// <summary>Initialises a new instance of the <see cref="RequestSpacingGate"/> class.</summary>
    /// <param name="minInterval">Minimum distance between two request starts.</param>
    /// <param name="timeProvider">The clock the gate reads and delays with.</param>
    public RequestSpacingGate(TimeSpan minInterval, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minInterval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _minInterval = minInterval;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns when the caller's slot has arrived. The first caller at any moment returns
    /// immediately; later ones wait until their slot.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait; the slot stays reserved.</param>
    public ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now;
        TimeSpan delay;

        lock (_sync)
        {
            now = _timeProvider.GetUtcNow();

            var slot = now > _nextFreeSlot ? now : _nextFreeSlot;
            _nextFreeSlot = slot + _minInterval;
            delay = slot - now;
        }

        return delay > TimeSpan.Zero
            ? new ValueTask(WaitUntilAsync(now + delay, cancellationToken))
            : ValueTask.CompletedTask;
    }

    /// <summary>
    /// Waits until the clock reaches <paramref name="slot"/>. Timers run on the millisecond tick
    /// count, which on Windows advances in ~15.6 ms steps, so a delay can end up to one step early by
    /// the precise clock; check again and wait out the remainder rather than start early.
    /// </summary>
    private async Task WaitUntilAsync(DateTimeOffset slot, CancellationToken cancellationToken)
    {
        var remaining = slot - _timeProvider.GetUtcNow();
        while (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, _timeProvider, cancellationToken).ConfigureAwait(false);
            remaining = slot - _timeProvider.GetUtcNow();
        }
    }
}
