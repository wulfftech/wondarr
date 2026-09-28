using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>A point-in-time view of the search budget, for the status API.</summary>
/// <param name="SubmittedInWindow">How many searches were submitted inside the current window.</param>
/// <param name="Outstanding">How many leases are currently held.</param>
/// <param name="NextAllowedAt">When the next search may be submitted, or <c>null</c> if it may go now.</param>
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
public sealed partial class SoulseekSearchBudget : ISoulseekSearchBudget, IDisposable
{
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly SemaphoreSlim _slotReleased = new(0, int.MaxValue);
    private readonly TimeProvider _timeProvider;
    private readonly IOptionsMonitor<SoulseekOptions> _options;
    private readonly ILogger<SoulseekSearchBudget> _logger;

    /// <summary>Submission times still inside the window, oldest first.</summary>
    private readonly LinkedList<DateTimeOffset> _submissions = new();

    private DateTimeOffset? _lastSubmission;
    private int _outstanding;

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
        while (true)
        {
            TimeSpan? wait = null;
            var waitForSlot = false;
            var reason = string.Empty;

            // One waiter at a time checks and records. SemaphoreSlim releases its waiters in arrival
            // order, so the queue stays FIFO; everything before it is only reading.
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var search = _options.CurrentValue.Search;
                var window = TimeSpan.FromSeconds(search.WindowSeconds);
                var now = _timeProvider.GetUtcNow();

                while (_submissions.First is { } oldest && oldest.Value <= now - window)
                {
                    _submissions.RemoveFirst();
                }

                if (_outstanding >= search.MaxOutstanding)
                {
                    waitForSlot = true;
                    reason = $"{_outstanding} outstanding";
                }
                else if (_submissions.Count >= search.MaxSearches)
                {
                    // The oldest submission has to leave the window before this one can go.
                    wait = _submissions.First!.Value + window - now;
                    reason = "window full";
                }
                else
                {
                    var earliest = _lastSubmission + TimeSpan.FromSeconds(search.MinSpacingSeconds);

                    if (earliest > now)
                    {
                        wait = earliest - now;
                        reason = "spacing";
                    }
                    else
                    {
                        _submissions.AddLast(now);
                        _lastSubmission = now;
                        _outstanding++;

                        return new Lease(this);
                    }
                }
            }
            finally
            {
                _sync.Release();
            }

            if (waitForSlot)
            {
                LogWaitingForSlot(_logger, reason);

                // Released by whichever lease is disposed next; the wake re-checks every condition.
                await _slotReleased.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            LogWaiting(_logger, wait ?? TimeSpan.Zero, reason);

            // Timers can fire early, so every wake re-checks all three conditions rather than
            // assuming this one delay was enough.
            await Task.Delay(wait ?? TimeSpan.Zero, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public SoulseekSearchBudgetSnapshot Snapshot()
    {
        // The critical section never awaits, so this only ever blocks for a few instructions.
        _sync.Wait();
        try
        {
            var search = _options.CurrentValue.Search;
            var window = TimeSpan.FromSeconds(search.WindowSeconds);
            var now = _timeProvider.GetUtcNow();

            while (_submissions.First is { } oldest && oldest.Value <= now - window)
            {
                _submissions.RemoveFirst();
            }

            var next = _outstanding >= search.MaxOutstanding
                ? (DateTimeOffset?)null
                : EarliestSubmission(search, window);

            return new SoulseekSearchBudgetSnapshot(
                _submissions.Count,
                _outstanding,
                next > now ? next : null);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>When the next submission may go, ignoring the outstanding count.</summary>
    private DateTimeOffset? EarliestSubmission(SoulseekSearchOptions search, TimeSpan window)
    {
        var earliest = _lastSubmission + TimeSpan.FromSeconds(search.MinSpacingSeconds);

        if (_submissions.Count >= search.MaxSearches)
        {
            var windowOpens = _submissions.First!.Value + window;
            if (earliest is null || windowOpens > earliest)
            {
                earliest = windowOpens;
            }
        }

        return earliest;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sync.Dispose();
        _slotReleased.Dispose();
    }

    private async Task ReleaseAsync()
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            _outstanding--;
        }
        finally
        {
            _sync.Release();
        }

        if (_slotReleased.CurrentCount < int.MaxValue)
        {
            _slotReleased.Release();
        }
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

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _budget.ReleaseAsync().ConfigureAwait(false);
        }
    }
}