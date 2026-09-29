using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>A point-in-time view of the search budget, for the status API.</summary>
/// <param name="SubmittedInWindow">How many searches were submitted inside the current window.</param>
/// <param name="Outstanding">How many leases are currently held.</param>
/// <param name="NextAllowedAt">When the next search may be submitted, or <c>null</c> if it may go now (or waits for a lease).</param>
public sealed record SoulseekSearchBudgetSnapshot(int SubmittedInWindow, int Outstanding, DateTimeOffset? NextAllowedAt);

/// <summary>
/// Hands out permission to submit one Soulseek search. Every search, whoever asks for it, passes
/// through here: the network's own guidance is at most 30 searches per 4 minutes, at most 2 in
/// flight and at least 5 seconds between submissions, and this is the only place that counts.
/// </summary>
public interface ISoulseekSearchBudget
{
    /// <summary>
    /// Waits until a search may be submitted, records the submission and returns the lease that
    /// holds its "outstanding" slot. Disposing the lease releases the slot; the submission stays in
    /// the sliding window either way.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait. Nothing is recorded for a cancelled call.</param>
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);

    /// <summary>Reads the budget's current state without waiting for anything.</summary>
    SoulseekSearchBudgetSnapshot Snapshot();
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Waiters queue on a turnstile (<see cref="SemaphoreSlim"/> hands its async waiters out in arrival
/// order) and only the waiter at the head evaluates the limits — and it keeps the turnstile for the
/// whole of its wait, so nobody can overtake it and fairness is strictly first come, first served.
/// </para>
/// <para>
/// A freed slot is announced by completing the current "generation" task and replacing it, which
/// wakes whoever is waiting at that moment; the head captures the generation under the state lock
/// before it checks, so a release between the check and the wait cannot be lost.
/// </para>
/// <para>
/// Times are <see cref="TimeProvider.GetTimestamp"/> values (monotonic): a wall-clock step (NTP,
/// DST) can neither empty the window early nor stall it.
/// </para>
/// </remarks>
public sealed partial class SoulseekSearchBudget : ISoulseekSearchBudget, IDisposable
{
    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private readonly Lock _state = new();
    private readonly TimeProvider _timeProvider;
    private readonly IOptionsMonitor<SoulseekOptions> _options;
    private readonly ILogger<SoulseekSearchBudget> _logger;

    /// <summary>Submission timestamps still inside the window, oldest first.</summary>
    private readonly Queue<long> _submissions = new();

    private long? _lastSubmission;
    private int _outstanding;
    private TaskCompletionSource _slotReleased = NewGeneration();

    /// <summary>Initialises a new instance of the <see cref="SoulseekSearchBudget"/> class.</summary>
    /// <param name="options">Soulseek settings, whose <c>search</c> section holds the limits.</param>
    /// <param name="timeProvider">The clock every wait goes through.</param>
    /// <param name="logger">Receives a Debug line whenever a submission had to wait.</param>
    public SoulseekSearchBudget(
        IOptionsMonitor<SoulseekOptions> options,
        TimeProvider timeProvider,
        ILogger<SoulseekSearchBudget> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Task? slotReleased = null;
                var wait = TimeSpan.Zero;
                var reason = string.Empty;

                lock (_state)
                {
                    var search = _options.CurrentValue.Search;
                    var now = _timeProvider.GetTimestamp();
                    Prune(now, search);

                    if (_outstanding >= search.MaxOutstanding)
                    {
                        // Captured under the lock: a lease released after this point completes it.
                        slotReleased = _slotReleased.Task;
                        reason = $"{_outstanding} outstanding";
                    }
                    else if (WaitFor(now, search) is var remaining && remaining > TimeSpan.Zero)
                    {
                        wait = remaining;
                        reason = _submissions.Count >= search.MaxSearches ? "window full" : "spacing";
                    }
                    else
                    {
                        _submissions.Enqueue(now);
                        _lastSubmission = now;
                        _outstanding++;

                        return new Lease(this);
                    }
                }

                if (slotReleased is not null)
                {
                    LogWaitingForSlot(_logger, reason);
                    await slotReleased.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Task.Delay truncates to whole milliseconds: round up, or a sub-millisecond wait
                    // becomes Delay(0) and the loop spins until the clock moves.
                    wait = TimeSpan.FromMilliseconds(Math.Ceiling(wait.TotalMilliseconds));
                    LogWaiting(_logger, wait, reason);

                    // Timers can fire early; the loop re-checks every condition after each wake.
                    await Task.Delay(wait, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _turnstile.Release();
        }
    }

    /// <inheritdoc />
    public SoulseekSearchBudgetSnapshot Snapshot()
    {
        lock (_state)
        {
            var search = _options.CurrentValue.Search;
            var now = _timeProvider.GetTimestamp();
            Prune(now, search);

            DateTimeOffset? next = null;
            if (_outstanding < search.MaxOutstanding && WaitFor(now, search) is var remaining && remaining > TimeSpan.Zero)
            {
                next = _timeProvider.GetUtcNow() + remaining;
            }

            return new SoulseekSearchBudgetSnapshot(_submissions.Count, _outstanding, next);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _turnstile.Dispose();

    private static TaskCompletionSource NewGeneration() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Drops submissions that are more than a whole window old. A submission exactly one window old
    /// still counts, so any closed interval of the window's length holds at most the maximum.
    /// </summary>
    private void Prune(long now, SoulseekSearchOptions search)
    {
        var window = TimeSpan.FromSeconds(search.WindowSeconds);

        while (_submissions.Count > 0 && _timeProvider.GetElapsedTime(_submissions.Peek(), now) > window)
        {
            _submissions.Dequeue();
        }
    }

    /// <summary>
    /// How long the next submission must still wait for the window and the spacing (not the
    /// outstanding count); zero when it may go now.
    /// </summary>
    private TimeSpan WaitFor(long now, SoulseekSearchOptions search)
    {
        var wait = TimeSpan.Zero;

        if (_lastSubmission is { } last)
        {
            var spacing = TimeSpan.FromSeconds(search.MinSpacingSeconds) - _timeProvider.GetElapsedTime(last, now);
            if (spacing > wait)
            {
                wait = spacing;
            }
        }

        if (_submissions.Count >= search.MaxSearches)
        {
            // The oldest of the last MaxSearches submissions must be strictly more than a window old.
            var oldest = _submissions.ElementAt(_submissions.Count - search.MaxSearches);
            var window = TimeSpan.FromSeconds(search.WindowSeconds) - _timeProvider.GetElapsedTime(oldest, now);
            if (window >= TimeSpan.Zero && window + TimeSpan.FromTicks(1) > wait)
            {
                wait = window + TimeSpan.FromTicks(1);
            }
        }

        return wait;
    }

    private void Release()
    {
        TaskCompletionSource released;

        lock (_state)
        {
            _outstanding--;
            released = _slotReleased;
            _slotReleased = NewGeneration();
        }

        released.TrySetResult();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Soulseek search budget: waiting {Wait} before submitting ({Reason})")]
    private static partial void LogWaiting(ILogger logger, TimeSpan wait, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Soulseek search budget: waiting for an in-flight search to finish ({Reason})")]
    private static partial void LogWaitingForSlot(ILogger logger, string reason);

    /// <summary>Holds one "outstanding" slot until it is disposed. Disposing twice releases once.</summary>
    private sealed class Lease : IAsyncDisposable
    {
        private readonly SoulseekSearchBudget _budget;

        private int _disposed;

        public Lease(SoulseekSearchBudget budget) => _budget = budget;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _budget.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
