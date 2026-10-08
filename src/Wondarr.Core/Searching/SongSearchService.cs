using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Blocklisting;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.History;
using Wondarr.Core.Importing;
using Wondarr.Core.Messaging;
using Wondarr.Core.Metadata;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Searching;

/// <summary>How one <see cref="ISongSearchService.SearchAsync"/> call ended.</summary>
/// <param name="SearchRunId">The run that was recorded, or 0 when no run was opened (already downloading).</param>
/// <param name="Outcome">How the run ended.</param>
/// <param name="Decisions">Every candidate the run saw, in the engine's order (accepted first, best first).</param>
/// <param name="QueueItemId">The grab that was started, when one was.</param>
/// <param name="Message">A human-readable note: why nothing was accepted, or why no source answered.</param>
public sealed record SongSearchResult(
    long SearchRunId,
    SearchOutcome Outcome,
    IReadOnlyList<CandidateDecision> Decisions,
    long? QueueItemId,
    string? Message);

/// <summary>A candidate could not be handed to its download client.</summary>
public sealed class GrabFailedException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="GrabFailedException"/> class.</summary>
    public GrabFailedException()
        : this("The grab failed.")
    {
    }

    /// <summary>Initialises a new instance of the <see cref="GrabFailedException"/> class.</summary>
    /// <param name="message">What went wrong.</param>
    public GrabFailedException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="GrabFailedException"/> class.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The failure behind this one.</param>
    public GrabFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The song already has a download in flight, so this grab would be a second one for the same song.
/// The database enforces it (one active queue item per song); the pre-checks only report it early.
/// </summary>
public sealed class AlreadyDownloadingException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="AlreadyDownloadingException"/> class.</summary>
    public AlreadyDownloadingException()
        : this("The song already has a download in flight.")
    {
    }

    /// <summary>Initialises a new instance of the <see cref="AlreadyDownloadingException"/> class.</summary>
    /// <param name="message">What went wrong.</param>
    public AlreadyDownloadingException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="AlreadyDownloadingException"/> class.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The failure behind this one.</param>
    public AlreadyDownloadingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Runs one song's search-and-grab pass and records the evidence for it.</summary>
public interface ISongSearchService
{
    /// <summary>
    /// Searches every available source for a song, judges every candidate, records them, and — when
    /// asked — grabs the best accepted one.
    /// </summary>
    /// <param name="songId">The song to search for.</param>
    /// <param name="trigger">What started the search.</param>
    /// <param name="grab">Whether to hand the best accepted candidate to its download client.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>What the run saw and how it ended.</returns>
    /// <exception cref="SongNotFoundException">No song has that id.</exception>
    Task<SongSearchResult> SearchAsync(
        long songId,
        SearchTrigger trigger,
        bool grab,
        CancellationToken cancellationToken);

    /// <summary>
    /// Grabs the best candidate of a run that has not been grabbed yet: accepted, not blocklisted now,
    /// and from a provider that is not ignored. Tries the next candidate when a grab fails.
    /// </summary>
    /// <param name="searchRunId">The run whose candidates to try.</param>
    /// <param name="attempt">Which automatic attempt this is, 1-based; stored on the queue item.</param>
    /// <param name="cancellationToken">Cancels the grab.</param>
    /// <returns>The queue item id, or <see langword="null"/> when no candidate could be grabbed.</returns>
    Task<long?> GrabBestAsync(long searchRunId, int attempt, CancellationToken cancellationToken);

    /// <summary>
    /// Grabs one candidate by id, whether the decision engine accepted it or not — used by the manual
    /// grab, where the user decides, and by <see cref="GrabBestAsync"/>.
    /// </summary>
    /// <param name="candidateRecordId">The stored candidate to grab.</param>
    /// <param name="attempt">Which automatic attempt this is, 1-based; stored on the queue item.</param>
    /// <param name="cancellationToken">Cancels the grab.</param>
    /// <returns>The id of the queue item the grab created.</returns>
    /// <exception cref="GrabFailedException">The source refused or failed the grab.</exception>
    /// <exception cref="AlreadyDownloadingException">The song already has a download in flight.</exception>
    /// <exception cref="InvalidOperationException">The candidate does not exist.</exception>
    Task<long> GrabCandidateAsync(long candidateRecordId, int attempt, CancellationToken cancellationToken);
}

/// <summary>
/// The search step of the pipeline (ARCHITECTURE §5.2 steps 4–6): it asks every available source,
/// lets <see cref="DecisionEngine"/> judge every candidate, stores the run and its candidates, and
/// grabs the best accepted one into a per-grab folder. The same method answers the interactive
/// search (<c>grab: false</c>) and the manual grab.
/// </summary>
public sealed partial class SongSearchService : ISongSearchService
{
    /// <summary>The JSON shape of every body and column this class writes: camelCase, enums as strings.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly WondarrDbContext _database;
    private readonly IEnumerable<ISourceProvider> _providers;
    private readonly DecisionEngine _engine;
    private readonly ISearchRunService _runs;
    private readonly IQueueService _queue;
    private readonly IBlocklistService _blocklist;
    private readonly ISoulseekUserService _users;
    private readonly IHistoryService _history;
    private readonly IEventAggregator _events;
    private readonly IOptionsMonitor<SearchOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SongSearchService> _logger;

    /// <summary>Initialises a new instance of the <see cref="SongSearchService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="providers">Every configured source, in registration order.</param>
    /// <param name="engine">The candidate judge.</param>
    /// <param name="runs">The search-run and candidate store.</param>
    /// <param name="queue">The download queue.</param>
    /// <param name="blocklist">The blocklist, checked before every grab.</param>
    /// <param name="users">The Soulseek peer reputation and ignore list.</param>
    /// <param name="history">The song lifecycle log.</param>
    /// <param name="events">The aggregator a successful grab is announced on.</param>
    /// <param name="options">The search limits.</param>
    /// <param name="timeProvider">The clock used to stamp the run.</param>
    /// <param name="logger">The logger.</param>
    public SongSearchService(
        WondarrDbContext database,
        IEnumerable<ISourceProvider> providers,
        DecisionEngine engine,
        ISearchRunService runs,
        IQueueService queue,
        IBlocklistService blocklist,
        ISoulseekUserService users,
        IHistoryService history,
        IEventAggregator events,
        IOptionsMonitor<SearchOptions> options,
        TimeProvider timeProvider,
        ILogger<SongSearchService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(blocklist);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _providers = providers;
        _engine = engine;
        _runs = runs;
        _queue = queue;
        _blocklist = blocklist;
        _users = users;
        _history = history;
        _events = events;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SongSearchResult> SearchAsync(
        long songId,
        SearchTrigger trigger,
        bool grab,
        CancellationToken cancellationToken)
    {
        var song = await LoadSongAsync(songId, cancellationToken).ConfigureAwait(false)
            ?? throw new SongNotFoundException(string.Concat("No song has the id ", songId.ToString(CultureInfo.InvariantCulture), "."));

        if (grab && await _queue.HasActiveForSongAsync(songId, cancellationToken).ConfigureAwait(false))
        {
            return new SongSearchResult(0, SearchOutcome.Cancelled, [], null, "Already downloading");
        }

        var run = await _runs.StartAsync(songId, trigger, cancellationToken).ConfigureAwait(false);

        // Everything that was asked and everything that came back, so a run that ends in an exception
        // still says what it had got through. Declared out here because the catch below closes it.
        var sources = new List<string>();
        var queries = new List<string>();

        try
        {
            return await RunAsync(song, trigger, grab, run.Id, sources, queries, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Whatever took the search away, the run must not be left open: unfinished runs are what
            // the backoff and the history read. The finish itself ignores the cancelled token.
            await FinishQuietlyAsync(
                    run.Id,
                    SearchOutcome.Cancelled,
                    sources,
                    queries,
                    "The search was cancelled.")
                .ConfigureAwait(false);

            throw;
        }
        catch (Exception exception)
        {
            await FinishQuietlyAsync(run.Id, SearchOutcome.Failed, sources, queries, exception.Message)
                .ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// The body of a search, from the run being open to it being closed. Kept apart from
    /// <see cref="SearchAsync"/> so every way out of it — including the exceptions — is finished there.
    /// </summary>
    private async Task<SongSearchResult> RunAsync(
        Song song,
        SearchTrigger trigger,
        bool grab,
        long searchRunId,
        List<string> sources,
        List<string> queries,
        CancellationToken cancellationToken)
    {
        var songId = song.Id;
        var available = new List<ISourceProvider>();
        var reasons = new List<string>();

        foreach (var provider in _providers)
        {
            var (isAvailable, reason) = await provider.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);

            if (isAvailable)
            {
                available.Add(provider);
            }
            else if (!string.IsNullOrWhiteSpace(reason))
            {
                reasons.Add(reason);
            }
        }

        if (available.Count == 0)
        {
            var unavailableMessage = reasons.Count > 0
                ? string.Join("; ", reasons)
                : "No source is available.";

            await _runs
                .FinishAsync(searchRunId, SearchOutcome.SourceUnavailable, [], [], unavailableMessage, cancellationToken)
                .ConfigureAwait(false);

            LogRun(_logger, songId, trigger, searchRunId, 0, 0, 0, SearchOutcome.SourceUnavailable);

            return new SongSearchResult(searchRunId, SearchOutcome.SourceUnavailable, [], null, unavailableMessage);
        }

        // Everything the engine needs that does not depend on what came back, loaded once so the
        // early-stop callback during the search can judge a pool without touching the database.
        var blockedKeys = await LoadBlockedKeysAsync(available, songId, cancellationToken).ConfigureAwait(false);
        var ignored = await _users.GetIgnoredAsync(cancellationToken).ConfigureAwait(false);
        var context = await BuildContextAsync(song, trigger, blockedKeys, ignored, cancellationToken).ConfigureAwait(false);

        var candidates = new List<Candidate>();
        string? sourceMessage = null;

        // The sources run in tier order (MATCHING_ENGINE §6.4): Soulseek first, YouTube only when it
        // found nothing the engine accepts, torrents last. A manual (interactive) search fans out over
        // every tier, because the user asked for the whole pool.
        foreach (var tier in available.GroupBy(SourceTier).OrderBy(group => group.Key))
        {
            foreach (var provider in tier)
            {
                sources.Add(provider.SourceType);

                var request = new SongSearchRequest(
                    song.Id,
                    song.Title,
                    song.ArtistCredit,
                    context.MainArtists,
                    context.AlbumTitle,
                    song.DurationMs,
                    context.SongFlags)
                {
                    // The interactive search runs every query so the user sees the whole pool; an
                    // automatic run stops early once a candidate is good enough (MATCHING_ENGINE §6.4).
                    IsPoolGoodEnough = trigger == SearchTrigger.Manual ? null : PoolIsGoodEnough(context),
                    MbRecordingId = song.MbRecordingId,
                    AlbumMbReleaseId = song.AlbumContext?.MbReleaseId,
                    AlbumTrackNo = song.AlbumContext?.TrackNo,
                };

                var result = await provider.SearchAsync(request, cancellationToken).ConfigureAwait(false);

                candidates.AddRange(result.Candidates);
                queries.AddRange(result.Queries);

                if (result.Candidates.Count == 0 && !string.IsNullOrWhiteSpace(result.Message))
                {
                    sourceMessage = result.Message;
                }
            }

            // The same caveat as the pool callback: the verdict is taken with the reputation known
            // before the search, so a candidate this check accepts can still be rejected by the final
            // evaluation. Stopping here only means the later tiers are not asked.
            if (trigger != SearchTrigger.Manual
                && _engine.Evaluate(context, candidates).Any(decision => decision.Accepted))
            {
                break;
            }
        }

        var reputation = await _users
            .GetReputationAsync(
                candidates
                    .Select(candidate => candidate.Provider)
                    .Where(provider => !string.IsNullOrEmpty(provider))
                    .Select(provider => provider!),
                cancellationToken)
            .ConfigureAwait(false);

        context = context with { Reputation = reputation };

        var decisions = _engine.Evaluate(context, candidates);

        await StoreCandidatesAsync(song, searchRunId, decisions, cancellationToken).ConfigureAwait(false);

        var accepted = decisions.Count(decision => decision.Accepted);
        long? queueItemId = null;
        SearchOutcome outcome;
        string? message;

        if (grab && accepted > 0 && trigger != SearchTrigger.Manual && !await HasFreeSlotAsync(cancellationToken).ConfigureAwait(false))
        {
            // The batch loop waited for a slot before it got here; the downloads it counted may since
            // have been joined by others, so an automatic grab is only made when one is still free.
            outcome = SearchOutcome.Cancelled;
            message = "No free download slot";
        }
        else if (grab && accepted > 0)
        {
            queueItemId = await GrabBestAsync(searchRunId, 1, cancellationToken).ConfigureAwait(false);

            if (queueItemId is null)
            {
                if (await _queue.HasActiveForSongAsync(songId, cancellationToken).ConfigureAwait(false))
                {
                    // A grab that beat this run to the song: nothing is wrong with the candidates.
                    outcome = SearchOutcome.Cancelled;
                    message = "Already downloading";
                }
                else
                {
                    outcome = SearchOutcome.NoAcceptableCandidate;
                    message = string.Concat(
                        "Nothing could be grabbed: ",
                        Explain(decisions),
                        " (the accepted candidates are blocklisted or their users are ignored).");
                }
            }
            else
            {
                outcome = SearchOutcome.Grabbed;
                message = string.Concat("Grabbed candidate ", queueItemId.Value.ToString(CultureInfo.InvariantCulture), ".");
            }
        }
        else if (candidates.Count == 0)
        {
            outcome = SearchOutcome.NoResults;
            message = sourceMessage ?? "No candidate was found.";
        }
        else if (accepted == 0)
        {
            outcome = SearchOutcome.NoAcceptableCandidate;
            message = string.Concat(decisions.Count.ToString(CultureInfo.InvariantCulture), " candidates, none acceptable: ", Explain(decisions));
        }
        else
        {
            // An interactive search asks for the candidates only: an accepted one is left alone, but
            // the run did its job and must not read as a failed search.
            outcome = SearchOutcome.Cancelled;
            message = string.Concat(
                "Interactive search: ",
                accepted.ToString(CultureInfo.InvariantCulture),
                " acceptable");
        }

        await _runs.FinishAsync(searchRunId, outcome, sources, queries, message, cancellationToken).ConfigureAwait(false);

        LogRun(_logger, songId, trigger, searchRunId, queries.Count, candidates.Count, accepted, outcome);

        return new SongSearchResult(searchRunId, outcome, decisions, queueItemId, message);
    }

    /// <summary>
    /// Closes a run that ended in an exception. It never throws: the failure that is being reported
    /// must reach the caller unchanged.
    /// </summary>
    private async Task FinishQuietlyAsync(
        long searchRunId,
        SearchOutcome outcome,
        IReadOnlyList<string> sources,
        IReadOnlyList<string> queries,
        string message)
    {
        try
        {
            await _runs
                .FinishAsync(searchRunId, outcome, sources, queries, message, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogFinishFailed(_logger, searchRunId, exception);
        }
    }

    /// <summary>
    /// The callback a source calls after each query, so an automatic search stops as soon as the pool
    /// already holds a candidate the engine accepts (MATCHING_ENGINE §6.4).
    /// </summary>
    /// <remarks>
    /// The verdict is taken with the reputation known <em>before</em> the search: the peer statistics
    /// are read once, after every source has answered. A candidate this callback accepts can therefore
    /// still be rejected by the final evaluation, and that is not a failure — the run simply keeps the
    /// candidates it has, judges them all against the final context, and grabs the best one that
    /// survives. Stopping early only means the sources are not asked for more.
    /// </remarks>
    private Func<IReadOnlyList<Candidate>, bool> PoolIsGoodEnough(DecisionContext context) =>
        pool => _engine.Evaluate(context, pool).Any(_engine.IsGoodEnough);

    /// <summary>The tier a source runs in (MATCHING_ENGINE §6.4): Soulseek, then YouTube, then torrents.</summary>
    private static int SourceTier(ISourceProvider provider) => provider.SourceType switch
    {
        SourceTypes.Soulseek => 1,
        SourceTypes.YouTube => 2,
        _ => 3,
    };

    /// <summary>Whether another grab may start: the downloads in flight are below the configured limit.</summary>
    private async Task<bool> HasFreeSlotAsync(CancellationToken cancellationToken)
    {
        var active = await _database.QueueItems
            .AsNoTracking()
            .CountAsync(
                item => item.State == QueueItemState.Queued
                    || item.State == QueueItemState.RemotelyQueued
                    || item.State == QueueItemState.Downloading,
                cancellationToken)
            .ConfigureAwait(false);

        return active < _options.CurrentValue.MaxActiveDownloads;
    }

    /// <inheritdoc />
    public async Task<long?> GrabBestAsync(long searchRunId, int attempt, CancellationToken cancellationToken)
    {
        var candidates = await _database.Candidates
            .Where(candidate => candidate.SearchRunId == searchRunId && candidate.Accepted && !candidate.Grabbed)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return null;
        }

        var ignored = await _users.GetIgnoredAsync(cancellationToken).ConfigureAwait(false);

        // The same song-aware key set the engine judged the candidates against: an entry recorded for
        // another song must not block this one here any more than it did there.
        var blocked = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sourceType in candidates.Select(candidate => candidate.SourceType).Distinct(StringComparer.Ordinal))
        {
            blocked.UnionWith(
                await _blocklist
                    .GetActiveKeysAsync(sourceType, candidates[0].SongId, cancellationToken)
                    .ConfigureAwait(false));
        }

        // One search run may only try so many candidates: the attempt number the caller passes in
        // comes from the queue tracker (P2-13), and the budget keeps a run from working through a
        // whole page of dead peers in one go.
        var budget = Math.Max(1, _options.CurrentValue.MaxAutoAttemptsPerSearch - attempt + 1);
        var failures = 0;

        foreach (var candidate in candidates)
        {
            if (candidate.Provider is { } provider && ignored.Contains(provider))
            {
                continue;
            }

            if (blocked.Contains(candidate.BlocklistKey))
            {
                continue;
            }

            try
            {
                return await GrabCandidateAsync(candidate.Id, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (AlreadyDownloadingException)
            {
                // Another grab for this song won the race. Trying the next candidate would fail the
                // same way, so the run reports it and stops.
                LogAlreadyDownloading(_logger, candidate.SongId);
                return null;
            }
            catch (GrabFailedException exception)
            {
                // The failed candidate is marked grabbed, so the loop moves on to the next best one.
                LogGrabFailed(_logger, candidate.Id, exception.Message);

                if (exception.InnerException is ISourceGrabFailure { BlocklistCandidate: false })
                {
                    // The source's own bad moment — a bot check, a rate limit, a missing tool: the
                    // candidate is not dead, so it is not blocklisted, and the next one would fail the
                    // same way. The item is already failed with the classified message; the song's own
                    // backoff owns the retry.
                    return null;
                }

                if (exception.InnerException is ISourceGrabFailure { BlocklistCandidate: true } failure)
                {
                    // The candidate is dead — geo-restricted, age-gated, gone: blocklist it for this
                    // song so no later run tries it again, then move on to the next best candidate.
                    await _blocklist
                        .AddAsync(
                            new BlocklistItem
                            {
                                SongId = candidate.SongId,
                                SourceType = candidate.SourceType,
                                BlocklistKey = candidate.BlocklistKey,
                                Reason = failure.Message,
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    blocked.Add(candidate.BlocklistKey);
                }
                failures++;

                if (failures >= budget)
                {
                    LogBudgetSpent(_logger, searchRunId, attempt, failures);
                    break;
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public Task<long> GrabCandidateAsync(
        long candidateRecordId,
        int attempt,
        CancellationToken cancellationToken) =>
        GrabCandidateAsync(candidateRecordId, attempt, bundle: true, cancellationToken);

    /// <summary>
    /// Grabs one candidate; a container grab with <paramref name="bundle"/> set also takes every other
    /// wanted song the container holds (DECISIONS build session 8 #6).
    /// </summary>
    private async Task<long> GrabCandidateAsync(
        long candidateRecordId,
        int attempt,
        bool bundle,
        CancellationToken cancellationToken)
    {
        var record = await _database.Candidates
            .FirstOrDefaultAsync(candidate => candidate.Id == candidateRecordId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                string.Concat("No candidate has the id ", candidateRecordId.ToString(CultureInfo.InvariantCulture), "."));

        if (await _queue.HasActiveForSongAsync(record.SongId, cancellationToken).ConfigureAwait(false))
        {
            // A fast path only: the unique index on the queue item is what actually enforces this.
            throw new AlreadyDownloadingException(
                string.Concat(
                    "Song ",
                    record.SongId.ToString(CultureInfo.InvariantCulture),
                    " already has a download in flight."));
        }

        var candidate = JsonSerializer.Deserialize<Candidate>(record.Normalised, Json)
            ?? throw new InvalidOperationException("The stored candidate could not be read back.");

        var provider = _providers.FirstOrDefault(source => source.SourceType == record.SourceType)
            ?? throw new GrabFailedException(
                string.Concat("No source is registered for the type '", record.SourceType, "'."));

        // The other wanted songs in the same container are found before the grab, so the client
        // selects their files (and SABnzbd keeps them) from the start.
        var bundled = bundle
            ? await ContainerBundler.FindAsync(_database, record.SongId, candidate, cancellationToken).ConfigureAwait(false)
            : [];

        if (bundled.Count > 0)
        {
            candidate = candidate with
            {
                Release = candidate.Release! with { AlsoWanted = [.. bundled.Select(song => song.Match.File.Path)] },
            };
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // The per-grab folder must be known before the row is written, so it is named after a fresh
        // guid rather than after the queue item's id — that way the item and the folder it downloads
        // into are written in one save, and a failure can never leave a queue item with no folder.
        var item = new QueueItem
        {
            SongId = record.SongId,
            CandidateId = record.Id,
            SearchRunId = record.SearchRunId,
            SourceType = record.SourceType,
            SourceInstanceId = record.SourceInstanceId,
            State = QueueItemState.Queued,
            Attempt = attempt,
            SizeBytes = candidate.SizeBytes,
            Destination = string.Concat("wondarr/", Guid.NewGuid().ToString("N")),
            NextCheckAt = now,
        };

        try
        {
            await _queue.AddAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsActiveDownloadConflict(exception))
        {
            // Another grab for this song got its row in first. The failed insert is detached so it
            // cannot be retried by a later save on this context: nothing is left behind.
            _database.Entry(item).State = EntityState.Detached;

            throw new AlreadyDownloadingException(
                string.Concat(
                    "Song ",
                    record.SongId.ToString(CultureInfo.InvariantCulture),
                    " already has a download in flight."),
                exception);
        }

        GrabHandle handle;

        try
        {
            handle = await provider.GrabAsync(candidate, item.Destination, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The item is already in the queue: leaving it Queued would make it look like a live
            // download, so it is failed with the cancelled token on purpose.
            await FailItemAsync(item, record, "Cancelled before the peer answered").ConfigureAwait(false);

            throw;
        }
        catch (Exception exception)
        {
            await FailItemAsync(item, record, exception.Message).ConfigureAwait(false);

            throw new GrabFailedException(
                string.Concat("Grabbing '", record.DisplayName, "' from ", record.SourceType, " failed: ", exception.Message),
                exception);
        }

        item.Handle = handle.Value;
        record.Grabbed = true;

        // The item, the candidate's flag and the history row are one unit: the queue item must not
        // claim a handle the history does not know about, or the other way round.
        _database.History.Add(new HistoryItem
        {
            SongId = record.SongId,
            EventType = HistoryEventType.Grabbed,
            SourceInstanceId = record.SourceInstanceId,
            QualityId = candidate.QualityId,
            Data = JsonSerializer.Serialize(
                new GrabHistoryData(
                    record.Id,
                    record.SearchRunId,
                    record.SourceType,
                    record.Provider,
                    record.DisplayName,
                    record.Score,
                    candidate.Query,
                    item.Destination,
                    candidate.Release?.Title,
                    bundled.Count),
                Json),
        });

        try
        {
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The transfer is real even though it could not be written down: the item must not be
            // marked failed for a bookkeeping error, and the queue tracker will find the transfer.
            LogPersistFailed(_logger, record.Id, exception);

            throw;
        }

        // Announce the grab only once it is on disk and the handle is recorded. Handlers are isolated
        // by the aggregator, so this cannot fail the grab, and it is deliberately the last thing here.
        await _events
            .PublishAsync(new SongGrabbedEvent(item.Id, record.SongId), cancellationToken)
            .ConfigureAwait(false);

        foreach (var song in bundled)
        {
            await GrabBundledAsync(record, candidate, song, attempt, cancellationToken).ConfigureAwait(false);
        }

        return item.Id;
    }

    /// <summary>
    /// One bundled song's own candidate record, queue item and grab on the container already grabbed.
    /// A song that started downloading meanwhile, or whose grab fails, is skipped: the first song's
    /// grab stands either way.
    /// </summary>
    private async Task GrabBundledAsync(
        CandidateRecord grabbed,
        Candidate container,
        BundledSong song,
        int attempt,
        CancellationToken cancellationToken)
    {
        var candidate = ContainerBundler.CandidateFor(container, song);
        var record = new CandidateRecord
        {
            SearchRunId = grabbed.SearchRunId,
            SongId = song.Song.Id,
            SourceType = candidate.SourceType,
            SourceInstanceId = candidate.SourceInstanceId,
            BlocklistKey = candidate.BlocklistKey,
            DisplayName = candidate.DisplayName,
            RemotePath = candidate.RemotePath,
            Provider = candidate.Provider,
            QualityId = candidate.QualityId,
            SizeBytes = candidate.SizeBytes,
            DurationMs = candidate.DurationMs,
            Normalised = JsonSerializer.Serialize(candidate, Json),
            Score = grabbed.Score,
            ScoreBreakdown = grabbed.ScoreBreakdown,
            Accepted = true,
        };

        try
        {
            _database.Candidates.Add(record);
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await GrabCandidateAsync(record.Id, attempt, bundle: false, cancellationToken).ConfigureAwait(false);
            LogBundled(_logger, song.Song.Id, grabbed.SongId, record.Id);
        }
        catch (Exception exception) when (exception is AlreadyDownloadingException or GrabFailedException or DbUpdateException)
        {
            // A record that never reached the database must not ride along on a later save.
            if (_database.Entry(record).State == EntityState.Added)
            {
                _database.Entry(record).State = EntityState.Detached;
            }

            LogBundleFailed(_logger, song.Song.Id, grabbed.SongId, exception.Message);
        }
    }

    /// <summary>Marks a queue item failed without a usable cancellation token, and never throws.</summary>
    private async Task FailItemAsync(QueueItem item, CandidateRecord record, string message)
    {
        item.State = QueueItemState.Failed;
        item.Message = message;
        item.FinishedAt = _timeProvider.GetUtcNow().UtcDateTime;

        // The candidate counts as grabbed even though the grab failed: retrying the same file is what
        // the attempt loop is for, and the next attempt takes the next candidate.
        record.Grabbed = true;

        try
        {
            await _queue.UpdateAsync(item, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogFailWriteFailed(_logger, item.Id, exception);
        }
    }

    /// <summary>
    /// Whether a failed insert is the "one active download per song" index rather than anything else.
    /// SQLite names the index's columns in the message, so the check is on that name.
    /// </summary>
    private static bool IsActiveDownloadConflict(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("queue_item.song_id", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Loads the song with everything the decision context needs.</summary>
    private async Task<Song?> LoadSongAsync(long songId, CancellationToken cancellationToken) =>
        await _database.Songs
            .AsNoTracking()
            .Include(song => song.AlbumContext)
            .Include(song => song.File)
            .Include(song => song.Artists)
                .ThenInclude(credit => credit.Artist)
            .FirstOrDefaultAsync(song => song.Id == songId, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The keys the song is currently blocked on, for every source that is about to be asked.</summary>
    private async Task<IReadOnlySet<string>> LoadBlockedKeysAsync(
        IReadOnlyList<ISourceProvider> providers,
        long songId,
        CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var provider in providers)
        {
            var providerKeys = await _blocklist
                .GetActiveKeysAsync(provider.SourceType, songId, cancellationToken)
                .ConfigureAwait(false);

            keys.UnionWith(providerKeys);
        }

        return keys;
    }

    /// <summary>Builds the context the engine judges every candidate against.</summary>
    private async Task<DecisionContext> BuildContextAsync(
        Song song,
        SearchTrigger trigger,
        IReadOnlySet<string> blockedKeys,
        IReadOnlySet<string> ignored,
        CancellationToken cancellationToken)
    {
        var profile = await _database.QualityProfiles
            .AsNoTracking()
            .Include(candidate => candidate.CutoffQuality)
            .FirstOrDefaultAsync(candidate => candidate.Id == song.QualityProfileId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                string.Concat(
                    "The song's quality profile ",
                    song.QualityProfileId.ToString(CultureInfo.InvariantCulture),
                    " does not exist."));

        var qualities = await _database.Qualities
            .AsNoTracking()
            .ToDictionaryAsync(quality => quality.Id, cancellationToken)
            .ConfigureAwait(false);

        return new DecisionContext
        {
            SongTitle = song.Title,
            MainArtists =
            [
                .. song.Artists
                    .Where(credit => credit.Role == ArtistRole.Main)
                    .OrderBy(credit => credit.Position)
                    .Select(credit => credit.Artist.Name),
            ],
            SongDurationMs = song.DurationMs,
            SongFlags = ParseFlags(song.VersionFlags),
            AlbumTitle = song.AlbumContext?.AlbumTitle,
            TrackNo = song.AlbumContext?.TrackNo,
            Profile = profile,
            Qualities = qualities,
            CurrentFileQualityId = song.File?.QualityId,
            CurrentFileIdentityScore = await ReadHeldIdentityAsync(song.File, cancellationToken).ConfigureAwait(false),
            IsManualGrab = trigger == SearchTrigger.Manual,
            SourceTier = 1,
            IsBlocklisted = key => blockedKeys.Contains(key),
            IgnoredUsers = ignored,
            MaxContainerSizeBytes = _options.CurrentValue.MaxContainerSizeMb * 1024L * 1024L,
        };
    }

    /// <summary>
    /// The identity sub-score of the candidate that produced the file the song already holds, or
    /// <see langword="null"/> when nothing can say: no file, no source ref, no candidate id in it, no
    /// stored candidate row, or a breakdown without the value. Malformed JSON is
    /// <see langword="null"/> too, never an error — the search must not fail over a column it can
    /// re-derive nothing from (MATCHING_ENGINE §6.6).
    /// </summary>
    /// <param name="file">The file the song holds, when it holds one.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The held candidate's identity sub-score, or <see langword="null"/>.</returns>
    private async Task<int?> ReadHeldIdentityAsync(SongFile? file, CancellationToken cancellationToken)
    {
        if (file?.SourceRef is not { Length: > 0 } sourceRef)
        {
            return null;
        }

        HeldSourceReference? reference;

        try
        {
            reference = JsonSerializer.Deserialize<HeldSourceReference>(sourceRef, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (reference?.CandidateId is not { } candidateId)
        {
            return null;
        }

        var record = await _database.Candidates
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == candidateId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return null;
        }

        try
        {
            using var breakdown = JsonDocument.Parse(record.ScoreBreakdown);

            return breakdown.RootElement.ValueKind == JsonValueKind.Object
                && breakdown.RootElement.TryGetProperty("identity", out var identity)
                && identity.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The part of <c>song_file.source_ref</c> the identity rule needs.</summary>
    /// <param name="CandidateId">The stored candidate that produced the file, when one did.</param>
    private sealed record HeldSourceReference(long? CandidateId);

    /// <summary>Stores the best of what the run saw, with the score and every rejection.</summary>
    private async Task StoreCandidatesAsync(
        Song song,
        long searchRunId,
        IReadOnlyList<CandidateDecision> decisions,
        CancellationToken cancellationToken)
    {
        var stored = decisions
            .Take(_options.CurrentValue.MaxStoredCandidates)
            .Select(decision => new CandidateRecord
            {
                SongId = song.Id,
                SourceType = decision.Candidate.SourceType,
                SourceInstanceId = decision.Candidate.SourceInstanceId,
                BlocklistKey = decision.Candidate.BlocklistKey,
                DisplayName = decision.Candidate.DisplayName,
                RemotePath = decision.Candidate.RemotePath,
                Provider = decision.Candidate.Provider,
                QualityId = decision.Candidate.QualityId,
                SizeBytes = decision.Candidate.SizeBytes,
                DurationMs = decision.Candidate.DurationMs,
                Normalised = JsonSerializer.Serialize(decision.Candidate, Json),
                Score = decision.Score.Total,
                ScoreBreakdown = JsonSerializer.Serialize(decision.Score, Json),
                Rejections = JsonSerializer.Serialize(
                    decision.Rejections.Select(rejection => new RejectionData(rejection.Reason.ToWireName(), rejection.Message)),
                    Json),
                Accepted = decision.Accepted,
            })
            .ToList();

        await _runs.AddCandidatesAsync(searchRunId, stored, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>"12 candidates, none acceptable: 7 × versionMismatch, 5 × durationOutOfTolerance".</summary>
    private static string Explain(IReadOnlyList<CandidateDecision> decisions)
    {
        var counts = decisions
            .SelectMany(decision => decision.Rejections)
            .GroupBy(rejection => rejection.Reason.ToWireName(), StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => string.Concat(
                group.Count().ToString(CultureInfo.InvariantCulture),
                " × ",
                group.Key));

        var explained = string.Join(", ", counts);

        return explained.Length == 0 ? "every candidate failed a rule" : explained;
    }

    /// <summary>The song's stored version flags as a mask; unknown names are ignored.</summary>
    private static VersionFlags ParseFlags(IEnumerable<string> names)
    {
        var flags = VersionFlags.None;

        foreach (var name in names)
        {
            if (VersionFlagNames.TryParse(name, out var single))
            {
                flags |= single;
            }
        }

        return flags;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Song {SongId} bundled with song {GrabbedSongId}'s grab (candidate {CandidateId})")]
    private static partial void LogBundled(ILogger logger, long songId, long grabbedSongId, long candidateId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Song {SongId} could not be bundled with song {GrabbedSongId}'s grab: {Reason}")]
    private static partial void LogBundleFailed(ILogger logger, long songId, long grabbedSongId, string reason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Search {SearchRunId} for song {SongId} ({Trigger}): {Queries} queries, {Candidates} candidates, {Accepted} accepted → {Outcome}")]
    private static partial void LogRun(
        ILogger logger,
        long songId,
        SearchTrigger trigger,
        long searchRunId,
        int queries,
        int candidates,
        int accepted,
        SearchOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Candidate {CandidateId} could not be grabbed: {Reason}")]
    private static partial void LogGrabFailed(ILogger logger, long candidateId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Song {SongId} is already downloading; the grab was left to the download in flight")]
    private static partial void LogAlreadyDownloading(ILogger logger, long songId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Search {SearchRunId} spent its attempt budget ({Failures} failed grabs) at attempt {Attempt}; no further candidate will be tried")]
    private static partial void LogBudgetSpent(ILogger logger, long searchRunId, int attempt, int failures);

    [LoggerMessage(Level = LogLevel.Error, Message = "Search run {SearchRunId} could not be closed")]
    private static partial void LogFinishFailed(ILogger logger, long searchRunId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Queue item {QueueItemId} could not be marked failed; the poll will correct it")]
    private static partial void LogFailWriteFailed(ILogger logger, long queueItemId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The grab of candidate {CandidateId} succeeded but could not be written down; the queue tracker will find the transfer")]
    private static partial void LogPersistFailed(ILogger logger, long candidateId, Exception exception);

    /// <summary>One rejection as it is stored: the camel-case wire name and the message.</summary>
    private sealed record RejectionData(string Reason, string Message);

    /// <summary>The history payload of a grab.</summary>
    private sealed record GrabHistoryData(
        long CandidateId,
        long SearchRunId,
        string SourceType,
        string? Provider,
        string DisplayName,
        int Score,
        string? Query,
        string Destination,
        string? Release = null,
        int BundledWith = 0);
}
