using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Domain;
using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;

// The queue poll is driven one cycle at a time, and its startup recovery on its own, by
// Wondarr.Core.Tests; nothing else may reach past its public surface.
[assembly: InternalsVisibleTo("Wondarr.Core.Tests")]

namespace Wondarr.Core.Importing;

/// <summary>Something the queue poll can be told about, from outside the poll loop.</summary>
public interface IQueueTracker
{
    /// <summary>
    /// Asks for a cycle now instead of at the end of the current wait. The hint is only a hint: the
    /// source's own status is the truth, so a wake costs latency and never correctness.
    /// </summary>
    void Wake();
}

/// <summary>
/// Follows every grab to its end (ARCHITECTURE §5.2 step 7, §5.5): it polls the active queue items
/// through the source that owns them, moves them through <c>Queued → RemotelyQueued → Downloading →
/// Completed → Importing → Imported</c>, gives up on peers that never answer, queue us forever or
/// stall, grabs the next candidate after a failure, hands finished files to the import service, and
/// publishes every change so the UI can follow along without polling.
/// </summary>
/// <remarks>
/// Polling is the truth; the slskd webhook only calls <see cref="Wake"/> to cut the wait short. One
/// cycle uses one DI scope and saves the entities it loaded through that scope, so the timestamps the
/// timeouts run on are the ones written by <see cref="IQueueService"/>. The import of a finished file
/// runs in a scope of its own, one file at a time.
/// </remarks>
public sealed partial class QueueTracker : BackgroundService, IQueueTracker
{
    /// <summary>How much the progress has to move before the UI is told about it.</summary>
    private const double ProgressEpsilon = 0.01;

    /// <summary>Appended to a failure when the search run has no candidate left.</summary>
    internal const string NoMoreCandidates = "no more candidates";

    /// <summary>What a grab is failed with when the source gave no reason of its own.</summary>
    internal const string DownloadFailed = "The download failed";

    /// <summary>What an item left mid-import by a previous run is marked with.</summary>
    internal const string RecoveredAfterRestart = "recovered after restart";

    /// <summary>The JSON shape of every payload this class writes: camelCase, nulls left out.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyList<ISourceProvider> _sources;
    private readonly IOptionsMonitor<QueueOptions> _options;
    private readonly IOptionsMonitor<SearchOptions> _searchOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QueueTracker> _logger;

    // Capacity one: a wake while a wake is already pending is the same request twice.
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Initialises a new instance of the <see cref="QueueTracker"/> class.</summary>
    /// <param name="scopeFactory">Creates the scope one cycle runs in.</param>
    /// <param name="sources">The source providers, one per source type.</param>
    /// <param name="options">The poll intervals and the timeouts.</param>
    /// <param name="searchOptions">The attempt budget a failed grab works within.</param>
    /// <param name="timeProvider">The clock every wait and every timeout is measured against.</param>
    /// <param name="logger">The logger.</param>
    public QueueTracker(
        IServiceScopeFactory scopeFactory,
        IEnumerable<ISourceProvider> sources,
        IOptionsMonitor<QueueOptions> options,
        IOptionsMonitor<SearchOptions> searchOptions,
        TimeProvider timeProvider,
        ILogger<QueueTracker> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(searchOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _sources = [.. sources];
        _options = options;
        _searchOptions = searchOptions;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A cycle is already owed one; asking for another would only run the same poll twice.
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Recovers what a previous process left behind, then polls until the host stops. Nothing thrown
    /// by a cycle leaves this method: <c>BackgroundServiceExceptionBehavior.StopHost</c> would stop the
    /// whole application, and a queue that cannot be polled is not a reason to.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            // The poll still has work to do; whatever was left in Importing is picked up by the next
            // cycle as a Completed item anyway.
            LogRecoveryFailed(_logger, exception);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;

            try
            {
                wait = await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogCycleFailed(_logger, exception);

                wait = TimeSpan.FromSeconds(_options.CurrentValue.IdlePollSeconds);
            }

            try
            {
                await WaitForWakeAsync(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Waits for <see cref="Wake"/> or for the wait to run out, whichever comes first. The timeout is
    /// a <see cref="TimeProvider"/> timer, so a test can move the clock instead of waiting.
    /// </summary>
    /// <param name="wait">How long to wait.</param>
    /// <param name="stoppingToken">Cancels the wait (the host is stopping).</param>
    /// <returns><see langword="true"/> when woken, <see langword="false"/> when the wait ran out.</returns>
    private async Task<bool> WaitForWakeAsync(TimeSpan wait, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var timer = _timeProvider.CreateTimer(_ => CancelWait(timeout), null, wait, Timeout.InfiniteTimeSpan);

        try
        {
            await _wake.WaitAsync(timeout.Token).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Cancels the wait from the timer callback; a source that is already gone is not an error.</summary>
    /// <param name="timeout">The wait to cancel.</param>
    private static void CancelWait(CancellationTokenSource timeout)
    {
        try
        {
            timeout.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The cycle finished between the timer firing and the callback running.
        }
    }

    /// <summary>
    /// Runs one poll cycle and says how long the caller should wait before the next one.
    /// </summary>
    /// <param name="cancellationToken">Cancels the cycle (the host is stopping).</param>
    /// <returns>
    /// <see cref="QueueOptions.ActivePollSeconds"/> while a download is being followed, the time until
    /// a deferred retry is due (capped at <see cref="QueueOptions.DeferredRetryMinutes"/>), or
    /// <see cref="QueueOptions.IdlePollSeconds"/> when the queue is empty.
    /// </returns>
    /// <remarks>Internal so a test can drive one cycle without the timed loop; the loop calls it too.</remarks>
    internal async Task<TimeSpan> RunCycleAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var queue = services.GetRequiredService<IQueueService>();
        var database = services.GetRequiredService<WondarrDbContext>();

        var active = await queue.GetActiveAsync(cancellationToken).ConfigureAwait(false);

        var following = false;
        DateTime? deferred = null;

        foreach (var snapshot in active)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // GetActiveAsync is a no-tracking snapshot used for the ordering and the filter; the item
            // itself is loaded tracked here, because the timeouts run on the timestamps this scope
            // writes back, and IQueueService.UpdateAsync stamps a detached item with "now".
            var item = await database.QueueItems
                .Include(entry => entry.Candidate)
                .FirstOrDefaultAsync(entry => entry.Id == snapshot.Id, cancellationToken)
                .ConfigureAwait(false);

            if (item is null)
            {
                // Removed while the cycle was looking at the queue.
                continue;
            }

            if (item.State == QueueItemState.Importing)
            {
                // Only a process that died mid-import leaves this behind, and startup put it back.
                continue;
            }

            if (item.State == QueueItemState.Completed)
            {
                if (item.NextCheckAt is { } due && due > now)
                {
                    deferred = deferred is null || due < deferred ? due : deferred;

                    continue;
                }

                // Its own scope: the import writes the item, and this scope must not hold that row.
                await ImportAsync(item.Id, cancellationToken).ConfigureAwait(false);

                continue;
            }

            following = true;

            var provider = FindProvider(item.SourceType);

            if (provider is null)
            {
                await FailAsync(database, item, $"Source {item.SourceType} is not available", cancellationToken)
                    .ConfigureAwait(false);

                continue;
            }

            var handle = new GrabHandle(item.SourceType, item.Handle ?? string.Empty);
            DownloadStatus? status = null;

            if (!string.IsNullOrEmpty(item.Handle))
            {
                try
                {
                    status = await provider.GetStatusAsync(handle, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // The source could not answer. The item keeps the timestamps it has, so the wait
                    // still counts towards the timeouts and the next cycle asks again.
                    LogStatusFailed(_logger, item.Id, item.SourceType, exception);
                }
            }

            var previousState = item.State;
            var previousProgress = item.Progress;
            var previousMessage = item.Message;

            if (status is not null)
            {
                if (status.State == DownloadState.Completed)
                {
                    await CompleteAsync(database, item, status, cancellationToken).ConfigureAwait(false);
                    await ImportAsync(item.Id, cancellationToken).ConfigureAwait(false);

                    continue;
                }

                if (status.State == DownloadState.Failed)
                {
                    await FailAsync(database, item, status.Message ?? DownloadFailed, cancellationToken)
                        .ConfigureAwait(false);

                    continue;
                }

                if (status.State == DownloadState.Cancelled)
                {
                    // Someone cancelled it at the source; there is nothing here to cancel and no
                    // candidate to move on to — the user has decided.
                    await CancelAsync(database, item, status.Message, cancellationToken).ConfigureAwait(false);

                    continue;
                }

                Apply(status, item, _timeProvider.GetUtcNow().UtcDateTime);
            }

            var reason = TimeoutReason(item, options, _timeProvider.GetUtcNow().UtcDateTime);

            if (reason is not null)
            {
                try
                {
                    await provider.CancelAsync(handle, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // The grab is written down as failed either way; a source that will not cancel
                    // is a problem in the log, not a reason to keep the item in the queue.
                    LogCancelFailed(_logger, item.Id, item.SourceType, exception);
                }

                await FailAsync(database, item, reason, cancellationToken).ConfigureAwait(false);

                continue;
            }

            // Only a change the UI can see is written down and broadcast: a poll that learned the same
            // bytes from the same peer is not an update.
            if (item.State != previousState
                || Math.Abs(item.Progress - previousProgress) >= ProgressEpsilon
                || !string.Equals(item.Message, previousMessage, StringComparison.Ordinal))
            {
                await SaveAsync(database, item, cancellationToken).ConfigureAwait(false);
            }
        }

        return Wait(options, following, deferred, _timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Puts items a previous process left mid-import back to <c>Completed</c>, so the next cycle hands
    /// them to the import again. Placing a file is idempotent up to the move, so this is safe.
    /// </summary>
    /// <param name="cancellationToken">Cancels the recovery.</param>
    internal async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var stuck = await database.QueueItems
            .Where(entry => entry.State == QueueItemState.Importing)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (stuck.Count == 0)
        {
            return;
        }

        foreach (var item in stuck)
        {
            item.State = QueueItemState.Completed;
            item.StateChangedAt = now;
            item.NextCheckAt = null;
            item.Message = RecoveredAfterRestart;

            database.History.Add(new HistoryItem
            {
                SongId = item.SongId,
                EventType = HistoryEventType.Failed,
                SourceInstanceId = item.SourceInstanceId,
                Data = JsonSerializer.Serialize(new RecoveryHistoryData(RecoveredAfterRestart, item.Attempt), Json),
            });
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogRecovered(_logger, stuck.Count);
    }

    /// <summary>Hands one finished download to the import service, in a scope of its own.</summary>
    /// <param name="queueItemId">The item to import.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    private async Task ImportAsync(long queueItemId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var import = scope.ServiceProvider.GetRequiredService<IImportService>();

            await import.ImportAsync(queueItemId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The import records its own failures; anything that reaches here is a bug, and a bug in
            // one item must not stop the poll.
            LogImportFailed(_logger, queueItemId, exception);
        }
    }

    /// <summary>Moves an item to <c>Completed</c>, pointing at the file the source wrote.</summary>
    /// <param name="database">The cycle's database context.</param>
    /// <param name="item">The item the source reports finished.</param>
    /// <param name="status">What the source said.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task CompleteAsync(
        WondarrDbContext database,
        QueueItem item,
        DownloadStatus status,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        item.State = QueueItemState.Completed;
        item.StateChangedAt = now;
        item.Progress = 1;
        item.DownloadPath = status.CompletedPath;
        item.SizeBytes = status.SizeBytes ?? item.SizeBytes;
        item.Message = status.Message;

        await SaveAsync(database, item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves an item to <c>Cancelled</c>: it was stopped at the source, not by us.</summary>
    /// <param name="database">The cycle's database context.</param>
    /// <param name="item">The item the source reports cancelled.</param>
    /// <param name="message">What the source said, when it said anything.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task CancelAsync(
        WondarrDbContext database,
        QueueItem item,
        string? message,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        item.State = QueueItemState.Cancelled;
        item.StateChangedAt = now;
        item.FinishedAt = now;
        item.Message = message ?? "Cancelled at the source";

        await SaveAsync(database, item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fails a grab, records it, drops the peer's reputation and — while the run has attempts left —
    /// grabs its next candidate. The peer is at fault, not the file, so nothing is blocklisted.
    /// </summary>
    /// <param name="database">The cycle's database context.</param>
    /// <param name="item">The item to fail.</param>
    /// <param name="message">Why it failed, as the user will read it.</param>
    /// <param name="cancellationToken">Cancels the writes and the next grab.</param>
    private async Task FailAsync(
        WondarrDbContext database,
        QueueItem item,
        string message,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var provider = item.Candidate?.Provider;

        await RecordPeerFailureAsync(item, provider, cancellationToken).ConfigureAwait(false);

        item.State = QueueItemState.Failed;
        item.StateChangedAt = now;
        item.FinishedAt = now;
        item.Message = message;

        // Saved before the next grab: the search refuses to grab for a song that still has a grab in
        // flight, and this item is that grab until the row says otherwise.
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var next = await TryNextAsync(item, cancellationToken).ConfigureAwait(false);

        if (next is null)
        {
            item.Message = string.Concat(message, " (", NoMoreCandidates, ")");
        }

        database.History.Add(new HistoryItem
        {
            SongId = item.SongId,
            EventType = HistoryEventType.Failed,
            SourceInstanceId = item.SourceInstanceId,
            Data = JsonSerializer.Serialize(
                new FailureHistoryData(item.Message!, provider, item.Candidate?.RemotePath, item.Attempt, next),
                Json),
        });

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await PublishAsync(
                new QueueItemChangedEvent(item.Id, item.SongId, item.State)
                {
                    Progress = item.Progress,
                    Message = item.Message,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Records the peer's failure for Soulseek; other sources have no reputation yet.</summary>
    /// <param name="item">The item whose peer failed.</param>
    /// <param name="provider">The peer's name, when the candidate carries one.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task RecordPeerFailureAsync(QueueItem item, string? provider, CancellationToken cancellationToken)
    {
        if (item.SourceType != SourceTypes.Soulseek || provider is not { Length: > 0 })
        {
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<ISoulseekUserService>();

            await users.RecordFailureAsync(provider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A reputation that could not be written is not a reason to leave the item in the queue.
            LogPeerFailureNotRecorded(_logger, item.Id, provider, exception);
        }
    }

    /// <summary>
    /// Asks the search for the next accepted candidate of the same run, within the attempt budget.
    /// </summary>
    /// <param name="item">The item that just failed.</param>
    /// <param name="cancellationToken">Cancels the grab.</param>
    /// <returns>The new item's id, or <see langword="null"/> when there is nothing left to try.</returns>
    private async Task<string?> TryNextAsync(QueueItem item, CancellationToken cancellationToken)
    {
        if (item.Attempt >= _searchOptions.CurrentValue.MaxAutoAttemptsPerSearch)
        {
            return null;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var search = scope.ServiceProvider.GetRequiredService<ISongSearchService>();

            var next = await search
                .GrabBestAsync(item.SearchRunId, item.Attempt + 1, cancellationToken)
                .ConfigureAwait(false);

            return next?.ToString(CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogNextAttemptFailed(_logger, item.Id, exception);

            return null;
        }
    }

    /// <summary>Saves the item and tells the UI it changed.</summary>
    /// <param name="database">The cycle's database context.</param>
    /// <param name="item">The item the cycle updated.</param>
    /// <param name="cancellationToken">Cancels the write and the publish.</param>
    private async Task SaveAsync(WondarrDbContext database, QueueItem item, CancellationToken cancellationToken)
    {
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await PublishAsync(
                new QueueItemChangedEvent(item.Id, item.SongId, item.State)
                {
                    Progress = item.Progress,
                    Message = item.Message,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Publishes one queue item change through the aggregator.</summary>
    /// <param name="message">The event to publish.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    private async Task PublishAsync(QueueItemChangedEvent message, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventAggregator>();

        await events.PublishAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The source provider for a source type, or <see langword="null"/> when there is none.</summary>
    /// <param name="sourceType">One of <see cref="SourceTypes"/>.</param>
    private ISourceProvider? FindProvider(string sourceType)
    {
        foreach (var provider in _sources)
        {
            if (string.Equals(provider.SourceType, sourceType, StringComparison.OrdinalIgnoreCase))
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the three states a source can report into the item, stamping the timestamps the timeouts
    /// are measured from. Returns whether anything the UI cares about moved.
    /// </summary>
    /// <param name="status">What the source reported.</param>
    /// <param name="item">The item to update.</param>
    /// <param name="now">The instant the update happens at.</param>
    private static void Apply(DownloadStatus status, QueueItem item, DateTime now)
    {
        var previousBytes = item.BytesTransferred;
        var state = status.State switch
        {
            DownloadState.RemotelyQueued => QueueItemState.RemotelyQueued,
            DownloadState.Downloading => QueueItemState.Downloading,
            _ => QueueItemState.Queued,
        };

        if (item.State != state)
        {
            item.StateChangedAt = now;
        }

        item.State = state;
        item.Progress = Math.Clamp(status.Progress, 0, 1);
        item.BytesTransferred = status.BytesTransferred;
        item.SizeBytes = status.SizeBytes ?? item.SizeBytes;
        item.PlaceInQueue = status.PlaceInQueue;
        item.Message = status.Message;

        if (item.BytesTransferred > previousBytes)
        {
            item.LastProgressAt = now;
        }
    }

    /// <summary>
    /// Why a grab has waited too long, by the state it is in and the timestamp that state is measured
    /// from; <see langword="null"/> while it is still making progress.
    /// </summary>
    /// <param name="item">The item the poll just updated.</param>
    /// <param name="options">The configured timeouts.</param>
    /// <param name="now">The instant to measure against.</param>
    private static string? TimeoutReason(QueueItem item, QueueOptions options, DateTime now) => item.State switch
    {
        QueueItemState.Queued when Waited(item.StateChangedAt, options.StartTimeoutMinutes, now)
            => "Peer did not respond",
        QueueItemState.RemotelyQueued when Waited(item.StateChangedAt, options.RemoteQueueTimeoutMinutes, now)
            => string.Create(
                CultureInfo.InvariantCulture,
                $"Waited {options.RemoteQueueTimeoutMinutes} min in the peer's queue (place {Place(item)})"),
        QueueItemState.Downloading when Waited(item.LastProgressAt, options.StallTimeoutMinutes, now)
            => string.Create(CultureInfo.InvariantCulture, $"Stalled at {Math.Round(item.Progress * 100)} %"),
        _ => null,
    };

    /// <summary>Where in the peer's queue the item is, as the message reads it.</summary>
    /// <param name="item">The item in the peer's queue.</param>
    private static string Place(QueueItem item) =>
        item.PlaceInQueue?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    /// <summary>Whether <paramref name="since"/> is at least <paramref name="minutes"/> old.</summary>
    /// <param name="since">The instant the state (or the last progress) started.</param>
    /// <param name="minutes">How long it may last.</param>
    /// <param name="now">The instant to measure against.</param>
    private static bool Waited(DateTime since, int minutes, DateTime now) =>
        now - since >= TimeSpan.FromMinutes(minutes);

    /// <summary>How long to wait before the next cycle.</summary>
    /// <param name="options">The configured intervals.</param>
    /// <param name="following">Whether a download is being followed.</param>
    /// <param name="deferred">The earliest deferred retry, when there is one.</param>
    /// <param name="now">The instant the cycle ended at.</param>
    private static TimeSpan Wait(QueueOptions options, bool following, DateTime? deferred, DateTime now)
    {
        if (following)
        {
            return TimeSpan.FromSeconds(options.ActivePollSeconds);
        }

        if (deferred is not { } due)
        {
            return TimeSpan.FromSeconds(options.IdlePollSeconds);
        }

        // Nothing is moving, but a deferred item has a time to be looked at again: sleep until then,
        // and never longer than the configured cap, so a far-away retry still gets periodic attention.
        var until = due - now;
        var cap = TimeSpan.FromMinutes(options.DeferredRetryMinutes);

        return until < TimeSpan.Zero ? TimeSpan.Zero : until < cap ? until : cap;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Put {Count} item(s) left mid-import by a previous run back to Completed")]
    private static partial void LogRecovered(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The startup recovery of interrupted imports failed")]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A queue poll cycle failed")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The status of queue item {QueueItemId} could not be read from {SourceType}")]
    private static partial void LogStatusFailed(ILogger logger, long queueItemId, string sourceType, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The grab of queue item {QueueItemId} could not be cancelled at {SourceType}")]
    private static partial void LogCancelFailed(ILogger logger, long queueItemId, string sourceType, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The failure of peer {Provider} for queue item {QueueItemId} could not be recorded")]
    private static partial void LogPeerFailureNotRecorded(ILogger logger, long queueItemId, string provider, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The next candidate for queue item {QueueItemId} could not be grabbed")]
    private static partial void LogNextAttemptFailed(ILogger logger, long queueItemId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The import of queue item {QueueItemId} failed")]
    private static partial void LogImportFailed(ILogger logger, long queueItemId, Exception exception);

    /// <summary>The history payload of a recovered item.</summary>
    /// <param name="Message">What happened.</param>
    /// <param name="Attempt">Which attempt the interrupted item was.</param>
    private sealed record RecoveryHistoryData(string Message, int Attempt);

    /// <summary>The history payload of a failed grab.</summary>
    /// <param name="Message">Why it failed, as the user will read it.</param>
    /// <param name="Provider">The peer that failed us, when it is known.</param>
    /// <param name="RemotePath">The file it never delivered, when it is known.</param>
    /// <param name="Attempt">Which automatic attempt failed.</param>
    /// <param name="NextQueueItemId">The grab the failure led to, or <see langword="null"/>.</param>
    private sealed record FailureHistoryData(
        string Message,
        string? Provider,
        string? RemotePath,
        int Attempt,
        string? NextQueueItemId);
}