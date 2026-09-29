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
    /// <exception cref="InvalidOperationException">The song already has a download in flight, or the candidate does not exist.</exception>
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
                .FinishAsync(run.Id, SearchOutcome.SourceUnavailable, [], [], unavailableMessage, cancellationToken)
                .ConfigureAwait(false);

            LogRun(_logger, songId, trigger, run.Id, 0, 0, 0, SearchOutcome.SourceUnavailable);

            return new SongSearchResult(run.Id, SearchOutcome.SourceUnavailable, [], null, unavailableMessage);
        }

        // Everything the engine needs that does not depend on what came back, loaded once so the
        // early-stop callback during the search can judge a pool without touching the database.
        var blockedKeys = await LoadBlockedKeysAsync(available, songId, cancellationToken).ConfigureAwait(false);
        var ignored = await _users.GetIgnoredAsync(cancellationToken).ConfigureAwait(false);
        var context = await BuildContextAsync(song, trigger, blockedKeys, ignored, cancellationToken).ConfigureAwait(false);

        var candidates = new List<Candidate>();
        var queries = new List<string>();
        var sources = new List<string>();
        string? sourceMessage = null;

        foreach (var provider in available)
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
                IsPoolGoodEnough = trigger == SearchTrigger.Manual
                    ? null
                    : pool => _engine.Evaluate(context, pool).Any(_engine.IsGoodEnough),
            };

            var result = await provider.SearchAsync(request, cancellationToken).ConfigureAwait(false);

            candidates.AddRange(result.Candidates);
            queries.AddRange(result.Queries);

            if (result.Candidates.Count == 0 && !string.IsNullOrWhiteSpace(result.Message))
            {
                sourceMessage = result.Message;
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

        await StoreCandidatesAsync(song, run.Id, decisions, cancellationToken).ConfigureAwait(false);

        var accepted = decisions.Count(decision => decision.Accepted);
        long? queueItemId = null;
        SearchOutcome outcome;
        string? message;

        if (grab && accepted > 0)
        {
            queueItemId = await GrabBestAsync(run.Id, 1, cancellationToken).ConfigureAwait(false);

            if (queueItemId is null)
            {
                outcome = SearchOutcome.NoAcceptableCandidate;
                message = string.Concat(
                    "Nothing could be grabbed: ",
                    Explain(decisions),
                    " (the accepted candidates are blocklisted or their users are ignored).");
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
            // An interactive search asks for the candidates only, so an accepted one is left alone.
            outcome = SearchOutcome.NoAcceptableCandidate;
            message = string.Concat(
                decisions.Count.ToString(CultureInfo.InvariantCulture),
                " candidates, ",
                accepted.ToString(CultureInfo.InvariantCulture),
                " acceptable (interactive search, nothing grabbed).");
        }

        await _runs.FinishAsync(run.Id, outcome, sources, queries, message, cancellationToken).ConfigureAwait(false);

        LogRun(_logger, songId, trigger, run.Id, queries.Count, candidates.Count, accepted, outcome);

        return new SongSearchResult(run.Id, outcome, decisions, queueItemId, message);
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
            catch (GrabFailedException exception)
            {
                // The failed candidate is marked grabbed, so the loop moves on to the next best one.
                LogGrabFailed(_logger, candidate.Id, exception.Message);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<long> GrabCandidateAsync(
        long candidateRecordId,
        int attempt,
        CancellationToken cancellationToken)
    {
        var record = await _database.Candidates
            .FirstOrDefaultAsync(candidate => candidate.Id == candidateRecordId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                string.Concat("No candidate has the id ", candidateRecordId.ToString(CultureInfo.InvariantCulture), "."));

        if (await _queue.HasActiveForSongAsync(record.SongId, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The song already has a download in flight.");
        }

        var candidate = JsonSerializer.Deserialize<Candidate>(record.Normalised, Json)
            ?? throw new InvalidOperationException("The stored candidate could not be read back.");

        var provider = _providers.FirstOrDefault(source => source.SourceType == record.SourceType)
            ?? throw new GrabFailedException(
                string.Concat("No source is registered for the type '", record.SourceType, "'."));

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // The per-grab folder is named after the queue item's id, which only exists once the row is
        // written; the placeholder is replaced in the same transaction-free pair of saves the queue
        // service already uses.
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
            Destination = string.Empty,
            NextCheckAt = now,
        };

        await _queue.AddAsync(item, cancellationToken).ConfigureAwait(false);

        item.Destination = string.Concat("wondarr/", item.Id.ToString(CultureInfo.InvariantCulture));
        await _queue.UpdateAsync(item, cancellationToken).ConfigureAwait(false);

        try
        {
            var handle = await provider.GrabAsync(candidate, item.Destination, cancellationToken).ConfigureAwait(false);

            item.Handle = handle.Value;
            await _queue.UpdateAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            item.State = QueueItemState.Failed;
            item.Message = exception.Message;
            item.FinishedAt = _timeProvider.GetUtcNow().UtcDateTime;
            await _queue.UpdateAsync(item, cancellationToken).ConfigureAwait(false);

            // The candidate counts as grabbed even though the grab failed: retrying the same file is
            // what the attempt loop is for, and the next attempt takes the next candidate.
            record.Grabbed = true;
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            throw new GrabFailedException(
                string.Concat("Grabbing '", record.DisplayName, "' from ", record.SourceType, " failed: ", exception.Message),
                exception);
        }

        record.Grabbed = true;
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _history
            .AddAsync(
                new HistoryItem
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
                            item.Destination),
                        Json),
                },
                cancellationToken)
            .ConfigureAwait(false);

        return item.Id;
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
            IsManualGrab = trigger == SearchTrigger.Manual,
            SourceTier = 1,
            IsBlocklisted = key => blockedKeys.Contains(key),
            IgnoredUsers = ignored,
        };
    }

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
        string Destination);
}
