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
/// The scheduler's upgrade loop (ARCHITECTURE §5.2 step 4, MATCHING_ENGINE §6.6): it picks the wanted
/// songs whose held file is below their profile's cutoff — the Cutoff Unmet list — waits for a
/// download slot, and searches each one in a DI scope of its own so a long batch never keeps one
/// <c>DbContext</c> alive for an hour. A strictly better file replaces the held one through the
/// existing search → grab → import path; nothing here imports anything itself.
/// </summary>
public sealed partial class UpgradeSearchCommandHandler : ICommandHandler
{
    /// <summary>The name the <c>UpgradeSearch</c> scheduled task queues.</summary>
    public const string CommandName = "UpgradeSearch";

    /// <summary>How long the batch waits between two warnings that every download slot is taken.</summary>
    private static readonly TimeSpan SlotWarningInterval = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<SearchOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UpgradeSearchCommandHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="UpgradeSearchCommandHandler"/> class.</summary>
    /// <param name="scopes">Builds the per-song scope every search runs in.</param>
    /// <param name="options">The batch size, the backoff and the download-slot limit.</param>
    /// <param name="timeProvider">The clock the slot wait is measured against.</param>
    /// <param name="logger">The logger.</param>
    public UpgradeSearchCommandHandler(
        IServiceScopeFactory scopes,
        IOptionsMonitor<SearchOptions> options,
        TimeProvider timeProvider,
        ILogger<UpgradeSearchCommandHandler> logger)
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
                    .SearchAsync(songId, SearchTrigger.Upgrade, grab: true, cancellationToken)
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
    /// Picks the batch: monitored songs that hold a file which is not a reference file, whose profile
    /// allows upgrades and whose cutoff the file does not meet, with no grab in flight and their
    /// upgrade backoff expired — the ones that have been waiting longest first, and never-searched
    /// songs first of all.
    /// </summary>
    private async Task<List<long>> SelectAsync(SearchOptions options, CancellationToken cancellationToken)
    {
        List<UpgradeSongRow> candidates;
        List<SearchRun> runs;
        Dictionary<long, QualityProfile> profiles;

        using (var scope = _scopes.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

            var wanted = database.Songs
                .AsNoTracking()
                .Where(song => song.Monitored && song.File != null && song.File.SourceType != SourceTypes.Reference)
                .Where(song => !database.QueueItems.Any(item =>
                    item.SongId == song.Id
                    && (item.State == QueueItemState.Queued
                        || item.State == QueueItemState.RemotelyQueued
                        || item.State == QueueItemState.Downloading
                        || item.State == QueueItemState.Completed
                        || item.State == QueueItemState.Importing)));

            candidates = await wanted
                .Select(song => new UpgradeSongRow(
                    song.Id,
                    song.QualityProfileId,
                    song.File!.QualityId,
                    song.File.ImportedAt,
                    null))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // One query for the candidate songs and one for their runs: asking per song would be a
            // query per wanted song on every scheduled run. Only this loop's own runs speak for its
            // backoff, and there are few of them, so no horizon is needed — the oldest one is the
            // ordering, not just the eligibility.
            runs = await database.SearchRuns
                .AsNoTracking()
                .Where(run => run.Trigger == SearchTrigger.Upgrade && wanted.Any(song => song.Id == run.SongId))
                .OrderByDescending(run => run.StartedAt)
                .ThenByDescending(run => run.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            profiles = await database.QualityProfiles
                .AsNoTracking()
                .ToDictionaryAsync(profile => profile.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        var bySong = runs.ToLookup(run => run.SongId);

        candidates = [.. candidates.Select(candidate => candidate with
        {
            // The lookup hands the runs back newest first, as the query ordered them.
            LastRunAt = bySong[candidate.SongId].Select(run => (DateTime?)run.StartedAt).FirstOrDefault(),
        })];

        // The cutoff check needs the profile's items, which the database cannot judge in SQL.
        candidates.RemoveAll(candidate => !profiles.TryGetValue(candidate.QualityProfileId, out var profile)
            || !profile.UpgradeAllowed
            || profile.MeetsCutoff(candidate.FileQualityId));

        candidates.Sort(CompareUpgradeSongs);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var selected = new List<long>();

        foreach (var candidate in candidates)
        {
            if (selected.Count >= options.UpgradeBatchSize)
            {
                break;
            }

            if (SearchBackoff.IsEligible(
                    [.. bySong[candidate.SongId]],
                    options,
                    now,
                    run => run.Trigger == SearchTrigger.Upgrade))
            {
                selected.Add(candidate.SongId);
            }
        }

        return selected;
    }

    /// <summary>Never-upgraded songs first, then by the oldest last upgrade run, then by the oldest file, then by the oldest row.</summary>
    private static int CompareUpgradeSongs(UpgradeSongRow left, UpgradeSongRow right)
    {
        var byLastRun = (left.LastRunAt ?? DateTime.MinValue).CompareTo(right.LastRunAt ?? DateTime.MinValue);

        if (byLastRun != 0)
        {
            return byLastRun;
        }

        var byImported = left.ImportedAt.CompareTo(right.ImportedAt);

        if (byImported != 0)
        {
            return byImported;
        }

        return left.SongId.CompareTo(right.SongId);
    }

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

    /// <summary>One candidate song: its id, its profile, its held file's quality and import time, and when it was last upgrade-searched.</summary>
    private sealed record UpgradeSongRow(
        long SongId,
        long QualityProfileId,
        long FileQualityId,
        DateTime ImportedAt,
        DateTime? LastRunAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Searching song {SongId} failed; the batch continues")]
    private static partial void LogSongFailed(ILogger logger, long songId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Waiting for a download slot: {Active} active")]
    private static partial void LogWaitingForSlot(ILogger logger, int active);
}
