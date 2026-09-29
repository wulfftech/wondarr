using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Searching;

/// <summary>
/// The scheduler's missing-song loop (ARCHITECTURE §5.2 step 4, MATCHING_ENGINE §6.6): it picks the
/// wanted songs that are due, waits for a download slot, and searches each one in a DI scope of its
/// own so a long batch never keeps one <c>DbContext</c> alive for an hour.
/// </summary>
public sealed partial class MissingSearchCommandHandler : ICommandHandler
{
    /// <summary>The name the <c>MissingSearch</c> scheduled task queues.</summary>
    public const string CommandName = "MissingSearch";

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<SearchOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MissingSearchCommandHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="MissingSearchCommandHandler"/> class.</summary>
    /// <param name="scopes">Builds the per-song scope every search runs in.</param>
    /// <param name="options">The batch size, the backoff and the download-slot limit.</param>
    /// <param name="timeProvider">The clock the slot wait is measured against.</param>
    /// <param name="logger">The logger.</param>
    public MissingSearchCommandHandler(
        IServiceScopeFactory scopes,
        IOptionsMonitor<SearchOptions> options,
        TimeProvider timeProvider,
        ILogger<MissingSearchCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = _options.CurrentValue;
        var songs = await SelectAsync(options, cancellationToken).ConfigureAwait(false);

        var grabbed = 0;
        var withoutAcceptable = 0;
        var skipped = 0;
        var searched = 0;

        foreach (var songId in songs)
        {
            await WaitForSlotAsync(options, cancellationToken).ConfigureAwait(false);

            using var scope = _scopes.CreateScope();

            try
            {
                var search = scope.ServiceProvider.GetRequiredService<ISongSearchService>();
                var result = await search
                    .SearchAsync(songId, SearchTrigger.Automatic, grab: true, cancellationToken)
                    .ConfigureAwait(false);

                switch (result.Outcome)
                {
                    case SearchOutcome.Grabbed:
                        grabbed++;
                        break;
                    case SearchOutcome.Cancelled:
                        // Already downloading: the run was never opened, so nothing was attempted.
                        skipped++;
                        break;
                    default:
                        withoutAcceptable++;
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One song's failure must not stop the batch.
                skipped++;
                LogSongFailed(_logger, songId, exception);
            }

            searched++;
            await context
                .ReportProgressAsync(string.Concat(
                    "Searched ",
                    searched.ToString(CultureInfo.InvariantCulture),
                    " of ",
                    songs.Count.ToString(CultureInfo.InvariantCulture),
                    ": ",
                    grabbed.ToString(CultureInfo.InvariantCulture),
                    " grabbed"))
                .ConfigureAwait(false);
        }

        return string.Concat(
            songs.Count.ToString(CultureInfo.InvariantCulture),
            " songs: ",
            grabbed.ToString(CultureInfo.InvariantCulture),
            " grabbed, ",
            withoutAcceptable.ToString(CultureInfo.InvariantCulture),
            " without an acceptable result, ",
            skipped.ToString(CultureInfo.InvariantCulture),
            " skipped");
    }

    /// <summary>
    /// Picks the batch: monitored songs with no file, no grab in flight and their backoff expired,
    /// the ones that have been waiting longest first (never searched songs first of all).
    /// </summary>
    private async Task<List<long>> SelectAsync(SearchOptions options, CancellationToken cancellationToken)
    {
        List<MissingSongRow> candidates;

        using (var scope = _scopes.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

            candidates = await database.Songs
                .AsNoTracking()
                .Where(song => song.Monitored && song.File == null)
                .Where(song => !database.QueueItems.Any(item =>
                    item.SongId == song.Id
                    && (item.State == QueueItemState.Queued
                        || item.State == QueueItemState.RemotelyQueued
                        || item.State == QueueItemState.Downloading)))
                .Select(song => new MissingSongRow(
                    song.Id,
                    song.CreatedAt,
                    database.SearchRuns
                        .Where(run => run.SongId == song.Id)
                        .OrderByDescending(run => run.StartedAt)
                        .ThenByDescending(run => run.Id)
                        .Select(run => (DateTime?)run.StartedAt)
                        .FirstOrDefault()))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        candidates.Sort(CompareMissingSongs);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var selected = new List<long>();

        foreach (var candidate in candidates)
        {
            if (selected.Count >= options.MissingBatchSize)
            {
                break;
            }

            using var scope = _scopes.CreateScope();
            var runs = scope.ServiceProvider.GetRequiredService<ISearchRunService>();

            var recent = await runs
                .GetRecentRunsAsync(candidate.SongId, Math.Max(1, options.BackoffHours.Count), cancellationToken)
                .ConfigureAwait(false);

            if (IsEligible(recent, options, now))
            {
                selected.Add(candidate.SongId);
            }
        }

        return selected;
    }

    /// <summary>Never-searched songs first, then by the oldest last run, then by the oldest row.</summary>
    private static int CompareMissingSongs(MissingSongRow left, MissingSongRow right)
    {
        var byLastRun = (left.LastRunAt ?? DateTime.MinValue).CompareTo(right.LastRunAt ?? DateTime.MinValue);

        if (byLastRun != 0)
        {
            return byLastRun;
        }

        var byCreated = left.CreatedAt.CompareTo(right.CreatedAt);

        return byCreated != 0 ? byCreated : left.SongId.CompareTo(right.SongId);
    }

    /// <summary>
    /// Whether the song's backoff has expired: the nth consecutive fruitless run waits
    /// <c>BackoffHours[min(n − 1, last)]</c>, and a song that was never searched is always eligible.
    /// </summary>
    private static bool IsEligible(IReadOnlyList<SearchRun> recent, SearchOptions options, DateTime now)
    {
        if (recent.Count == 0 || options.BackoffHours.Count == 0)
        {
            return true;
        }

        var failures = 0;

        foreach (var run in recent)
        {
            if (run.Outcome is SearchOutcome.NoResults or SearchOutcome.NoAcceptableCandidate)
            {
                failures++;
            }
            else
            {
                break;
            }
        }

        var index = Math.Clamp(failures - 1, 0, options.BackoffHours.Count - 1);

        return now - recent[0].StartedAt >= TimeSpan.FromHours(options.BackoffHours[index]);
    }

    /// <summary>Waits until fewer than <see cref="SearchOptions.MaxActiveDownloads"/> grabs are in flight.</summary>
    private async Task WaitForSlotAsync(SearchOptions options, CancellationToken cancellationToken)
    {
        while (true)
        {
            int active;

            using (var scope = _scopes.CreateScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

                active = await database.QueueItems
                    .AsNoTracking()
                    .CountAsync(
                        item => item.State == QueueItemState.Queued
                            || item.State == QueueItemState.RemotelyQueued
                            || item.State == QueueItemState.Downloading,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (active < options.MaxActiveDownloads)
            {
                return;
            }

            LogWaitingForSlot(_logger, active, options.MaxActiveDownloads);

            await Task.Delay(TimeSpan.FromSeconds(options.SlotWaitSeconds), _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>One candidate song: its id, when it was added, and when it was last searched.</summary>
    private sealed record MissingSongRow(long SongId, DateTime CreatedAt, DateTime? LastRunAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Searching song {SongId} failed; the batch continues")]
    private static partial void LogSongFailed(ILogger logger, long songId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{Active} downloads are in flight (limit {Limit}); waiting for a free slot")]
    private static partial void LogWaitingForSlot(ILogger logger, int active, int limit);
}
