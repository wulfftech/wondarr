using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Domain;
using Wondarr.Core.ImportLists;
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

    /// <summary>The command bodies are camelCase, like every other JSON this app exchanges.</summary>
    private static readonly JsonSerializerOptions BodyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

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
        var songs = await SelectAsync(options, ReadSongIds(context.Body), cancellationToken).ConfigureAwait(false);

        var grabbed = 0;
        var withoutAcceptable = 0;
        var skipped = 0;
        var searched = 0;

        foreach (var songId in songs)
        {
            await WaitForSlotAsync(context, options, cancellationToken).ConfigureAwait(false);

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
                    case SearchOutcome.NoFreeSlot:
                        // Already downloading, or every slot was taken: nothing was attempted for the song.
                        skipped++;
                        break;
                    default:
                        withoutAcceptable++;
                        break;
                }
            }
            catch (Exception exception) when (CancellationRules.IsCallerCancellation(exception, cancellationToken))
            {
                throw;
            }
            catch (Exception exception)
            {
                // One song's failure must not stop the batch; a provider's own timeout is one too.
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
    /// the ones that have been waiting longest first (never searched songs first of all). While a
    /// reference library still has files it has not identified, a song a synced import list added in the
    /// last 24 hours is left out: it waits for the scan (owner question 15), and the import-list sync
    /// searches it when the scan is done or the wait is over.
    /// </summary>
    private async Task<List<long>> SelectAsync(
        SearchOptions options,
        IReadOnlyList<long>? onlySongs,
        CancellationToken cancellationToken)
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

            // A run asked for particular songs (search on add) looks at those and no others.
            if (onlySongs is not null)
            {
                wanted = wanted.Where(song => onlySongs.Contains(song.Id));
            }

            if ((await ReferenceScanWait.PendingLibrariesAsync(database, cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                var waitCutoff = _timeProvider.GetUtcNow().UtcDateTime - ReferenceScanWait.MaxWait;

                wanted = wanted.Where(song => song.CreatedAt < waitCutoff
                    || !database.ImportLists.Any(list =>
                        list.Type != ImportList.PasteType && song.AddedBy == "list:" + list.Id));
            }

            candidates = await wanted
                .Select(song => new MissingSongRow(song.Id, song.CreatedAt, null))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // One query for the candidate songs and one for their runs: asking per song would be a
            // query per wanted song on every scheduled run. The backoff step is the number of fruitless
            // runs in a row, so the runs are loaded back to the newest one the loop counts as a success
            // (and not for a fixed number of hours: the failures that fall outside a window would lower
            // the step, and a hopeless song would never reach the weekly one).
            runs = await database.SearchRuns
                .AsNoTracking()
                .Where(run => wanted.Any(song => song.Id == run.SongId)
                    && !database.SearchRuns.Any(grab => grab.SongId == run.SongId
                        && grab.Outcome == SearchOutcome.Grabbed
                        && grab.Trigger != SearchTrigger.Manual
                        && grab.Trigger != SearchTrigger.Push
                        && (grab.StartedAt > run.StartedAt || (grab.StartedAt == run.StartedAt && grab.Id > run.Id))))
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

    /// <summary>Waits until a download slot is free, without holding the command's executor place.</summary>
    private Task WaitForSlotAsync(CommandContext context, SearchOptions options, CancellationToken cancellationToken) =>
        SlotWaiting.WaitForFreeSlotAsync(
            _scopes,
            _timeProvider,
            context,
            options,
            active => LogWaitingForSlot(_logger, active),
            cancellationToken);

    /// <summary>
    /// The songs a body names (<c>{"name":"MissingSearch","songIds":[1,2]}</c>, what search on add queues
    /// for a batch), or <see langword="null"/> for the scheduled run, which looks at every wanted song.
    /// </summary>
    private static List<long>? ReadSongIds(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MissingSearchBody>(body, BodyJson)?.SongIds is { } ids ? [.. ids] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The body of a missing search that was asked to look at particular songs.</summary>
    /// <param name="Name">The command name, echoed by the API for readability.</param>
    /// <param name="SongIds">The songs to look at.</param>
    private sealed record MissingSearchBody(string? Name, List<long>? SongIds);

    /// <summary>One candidate song: its id, when it was added, and when it was last searched.</summary>
    private sealed record MissingSongRow(long SongId, DateTime CreatedAt, DateTime? LastRunAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Searching song {SongId} failed; the batch continues")]
    private static partial void LogSongFailed(ILogger logger, long songId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Waiting for a download slot: {Active} active")]
    private static partial void LogWaitingForSlot(ILogger logger, int active);
}
