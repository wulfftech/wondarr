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

    /// <summary>How long the batch waits between two warnings that every download slot is taken.</summary>
    private static readonly TimeSpan SlotWarningInterval = TimeSpan.FromMinutes(10);

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
    /// Picks the batch: monitored songs with no file and no identified reference file, no grab in flight and their backoff expired,
    /// the ones that have been waiting longest first (never searched songs first of all).
    /// </summary>
    private async Task<List<long>> SelectAsync(SearchOptions options, CancellationToken cancellationToken)
    {
        List<MissingSongRow> candidates;
        List<SearchRun> runs;

        using (var scope = _scopes.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

            var wanted = database.Songs
                .AsNoTracking()
                .Where(song => song.Monitored && song.File == null)

                // A reference file identifies this song: the user owns it there, so it is not missing.
                .Where(song => !database.ReferenceFiles.Any(file =>
                    file.SongId == song.Id && file.State == ReferenceFileState.Identified))
                .Where(song => !database.QueueItems.Any(item =>
                    item.SongId == song.Id
                    && (item.State == QueueItemState.Queued
                        || item.State == QueueItemState.RemotelyQueued
                        || item.State == QueueItemState.Downloading)));

            candidates = await wanted
                .Select(song => new MissingSongRow(song.Id, song.CreatedAt, null))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // One query for the candidate songs and one for their recent runs: asking per song would
            // be a query per wanted song on every scheduled run. Only the runs the longest backoff can
            // reach are needed — anything older leaves the song eligible either way.
            var horizon = _timeProvider.GetUtcNow().UtcDateTime.AddHours(-options.BackoffHours.DefaultIfEmpty(0).Max());

            runs = await database.SearchRuns
                .AsNoTracking()
                .Where(run => run.StartedAt >= horizon && wanted.Any(song => song.Id == run.SongId))
                .OrderByDescending(run => run.StartedAt)
                .ThenByDescending(run => run.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var bySong = runs.ToLookup(run => run.SongId);

        candidates = [.. candidates.Select(candidate => candidate with
        {
            // The lookup hands the runs back newest first, as the query ordered them.
            LastRunAt = bySong[candidate.SongId].Select(run => (DateTime?)run.StartedAt).FirstOrDefault(),
        })];

        candidates.Sort(CompareMissingSongs);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var selected = new List<long>();

        foreach (var candidate in candidates)
        {
            if (selected.Count >= options.MissingBatchSize)
            {
                break;
            }

            if (IsEligible([.. bySong[candidate.SongId]], options, now))
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
    /// The arithmetic lives in <see cref="SearchBackoff"/>, which the upgrade loop shares.
    /// </summary>
    /// <remarks>
    /// Only the runs the automatic loop itself finished speak for the backoff. A run the user asked
    /// for (<see cref="SearchTrigger.Manual"/>) says nothing about when this loop should try again, and
    /// a run that neither found a candidate nor reached a verdict — failed, cancelled, or with no
    /// source to ask — counts as neither a fruitless attempt (which would lengthen the wait) nor a
    /// success (which would reset it).
    /// </remarks>
    private static bool IsEligible(IReadOnlyList<SearchRun> runs, SearchOptions options, DateTime now) =>
        SearchBackoff.IsEligible(runs, options, now, run => run.Trigger != SearchTrigger.Manual);

    /// <summary>Waits until fewer than <see cref="SearchOptions.MaxActiveDownloads"/> grabs are in flight.</summary>
    private async Task WaitForSlotAsync(SearchOptions options, CancellationToken cancellationToken)
    {
        var lastWarning = _timeProvider.GetUtcNow();

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

            // A stall here is not visible anywhere else — the batch simply stops — so the wait says so
            // out loud, but only once every ten minutes rather than on every poll.
            var now = _timeProvider.GetUtcNow();

            if (now - lastWarning >= SlotWarningInterval)
            {
                LogWaitingForSlot(_logger, active);
                lastWarning = now;
            }

            await Task.Delay(TimeSpan.FromSeconds(options.SlotWaitSeconds), _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>One candidate song: its id, when it was added, and when it was last searched.</summary>
    private sealed record MissingSongRow(long SongId, DateTime CreatedAt, DateTime? LastRunAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Searching song {SongId} failed; the batch continues")]
    private static partial void LogSongFailed(ILogger logger, long songId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Waiting for a download slot: {Active} active")]
    private static partial void LogWaitingForSlot(ILogger logger, int active);
}
