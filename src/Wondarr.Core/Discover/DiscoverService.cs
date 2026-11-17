using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata.Http;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.Discover;

/// <summary>A library song a result was seeded from.</summary>
/// <param name="SongId">The Wondarr song id.</param>
/// <param name="Title">The song's title.</param>
/// <param name="Artist">The main artist's name.</param>
public sealed record DiscoverSeedInfo(long SongId, string Title, string Artist);

/// <summary>Whether one source answered for a result.</summary>
/// <param name="Name">The source: <c>listenbrainz</c>, <c>lastfm</c> or <c>deezer</c>.</param>
/// <param name="Status"><c>answered</c>, <c>skipped</c> (nothing to ask) or <c>failed</c>.</param>
public sealed record DiscoverSourceInfo(string Name, string Status);

/// <summary>One suggested song.</summary>
/// <param name="Title">The title.</param>
/// <param name="Artist">The artist credit.</param>
/// <param name="Album">The album, when a source named one.</param>
/// <param name="CoverUrl">A cover image: the Cover Art Archive's when ListenBrainz gave a release, else Deezer's.</param>
/// <param name="MbRecordingId">The recording MBID, when known.</param>
/// <param name="Isrc">The ISRC, when known.</param>
/// <param name="DeezerId">The Deezer track id, when known.</param>
/// <param name="DurationMs">The duration in milliseconds, when known.</param>
/// <param name="Sources">The sources that suggested it.</param>
/// <param name="Score">The summed, weighted rank score; higher is better.</param>
public sealed record DiscoverSuggestion(
    string Title,
    string Artist,
    string? Album,
    string? CoverUrl,
    string? MbRecordingId,
    string? Isrc,
    long? DeezerId,
    int? DurationMs,
    IReadOnlyList<string> Sources,
    double Score);

/// <summary>What Discover answers.</summary>
/// <param name="Mode"><c>radio</c> or <c>mix</c>.</param>
/// <param name="MixSeed">The sample seed of a mix, to send back for the same sample; <see langword="null"/> for a radio.</param>
/// <param name="Seeds">The library songs the result was seeded from.</param>
/// <param name="Sources">Each source and whether it answered.</param>
/// <param name="Suggestions">The ranked suggestions, best first, at most <paramref name="Limit"/>.</param>
/// <param name="Limit">The most suggestions this answer carries.</param>
/// <param name="TotalBeforeLimit">How many suggestions there were after the filters and the artist cap, before the limit.</param>
public sealed record DiscoverResult(
    string Mode,
    int? MixSeed,
    int Limit,
    int TotalBeforeLimit,
    IReadOnlyList<DiscoverSeedInfo> Seeds,
    IReadOnlyList<DiscoverSourceInfo> Sources,
    IReadOnlyList<DiscoverSuggestion> Suggestions);

/// <summary>What a dismissal is made from.</summary>
/// <param name="MbRecordingId">The suggestion's recording MBID, or <see langword="null"/>.</param>
/// <param name="Artist">The suggestion's artist.</param>
/// <param name="Title">The suggestion's title.</param>
public sealed record DiscoverDismissalInput(string? MbRecordingId, string Artist, string Title);

/// <summary>The request cannot be acted on as written.</summary>
public sealed class DiscoverValidationException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="DiscoverValidationException"/> class.</summary>
    /// <param name="message">What is wrong, for the person who made the request.</param>
    public DiscoverValidationException(string message)
        : base(message)
    {
    }
}

/// <summary>Suggests songs from the library's neighbours, and keeps what the user turned down.</summary>
public interface IDiscoverService
{
    /// <summary>A radio seeded by one library song.</summary>
    /// <param name="songId">The seed song.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result, or <see langword="null"/> when the song does not exist.</returns>
    Task<DiscoverResult?> GetRadioAsync(long songId, int limit, CancellationToken cancellationToken);

    /// <summary>A mix seeded by a random sample of the library.</summary>
    /// <param name="sampleSeed">The sample seed to repeat, or <see langword="null"/> to draw a new sample.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<DiscoverResult> GetMixAsync(int? sampleSeed, int limit, CancellationToken cancellationToken);

    /// <summary>Lists the dismissals, newest first.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<DiscoverDismissal>> ListDismissalsAsync(CancellationToken cancellationToken);

    /// <summary>Records a dismissal; one that already exists is returned as it is.</summary>
    /// <param name="input">What was turned down.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="DiscoverValidationException">The artist or title is empty, or the MBID is not one.</exception>
    Task<DiscoverDismissal> DismissAsync(DiscoverDismissalInput input, CancellationToken cancellationToken);

    /// <summary>Removes a dismissal, which brings the suggestion back.</summary>
    /// <param name="id">The dismissal id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="false"/> when there is no such dismissal.</returns>
    Task<bool> UndoDismissalAsync(long id, CancellationToken cancellationToken);
}

/// <summary>
/// The Discover service. Per seed it asks every source, merges the answers on recording MBID, then ISRC,
/// then normalised artist and title, scores each suggestion by the weighted ranks its sources gave it,
/// drops what the library holds, the seeds and the dismissals, and keeps at most
/// <see cref="DiscoverOptions.MaxPerArtist"/> per artist. The merged candidates are cached per seed; the
/// filters run on every request, so a dismissal or an add shows at once.
/// </summary>
public sealed partial class DiscoverService : IDiscoverService
{
    /// <summary>The most suggestions one answer may carry.</summary>
    public const int MaxLimit = 200;

    /// <summary>How many suggestions an answer carries when the caller does not say.</summary>
    public const int DefaultLimit = 50;

    private readonly WondarrDbContext _database;
    private readonly IReadOnlyList<IRecommendationSource> _sources;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<DiscoverOptions> _options;
    private readonly ILogger<DiscoverService> _logger;

    /// <summary>Initialises a new instance of the <see cref="DiscoverService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="sources">The recommendation sources.</param>
    /// <param name="cache">Holds the merged candidates per seed.</param>
    /// <param name="options">The Discover settings.</param>
    /// <param name="logger">The logger.</param>
    public DiscoverService(
        WondarrDbContext database,
        IEnumerable<IRecommendationSource> sources,
        IMemoryCache cache,
        IOptionsMonitor<DiscoverOptions> options,
        ILogger<DiscoverService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _sources = [.. sources];
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DiscoverResult?> GetRadioAsync(long songId, int limit, CancellationToken cancellationToken)
    {
        var seeds = await LoadSeedsAsync([songId], cancellationToken).ConfigureAwait(false);
        if (seeds.Count == 0)
        {
            return null;
        }

        var run = await RunAsync($"discover:radio:{songId.ToString(CultureInfo.InvariantCulture)}", seeds, cancellationToken)
            .ConfigureAwait(false);

        return await FinishAsync("radio", null, run, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DiscoverResult> GetMixAsync(int? sampleSeed, int limit, CancellationToken cancellationToken)
    {
        var seed = sampleSeed ?? Random.Shared.Next(1, int.MaxValue);
        var count = _options.CurrentValue.MixSeedCount;

        var ids = await _database.Songs
            .AsNoTracking()
            .OrderBy(song => song.Id)
            .Select(song => song.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var sample = Sample(ids, count, seed);
        var seeds = await LoadSeedsAsync(sample, cancellationToken).ConfigureAwait(false);

        var key = string.Create(CultureInfo.InvariantCulture, $"discover:mix:{seed}:{count}:{ids.Count}");
        var run = await RunAsync(key, seeds, cancellationToken).ConfigureAwait(false);

        return await FinishAsync("mix", seed, run, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoverDismissal>> ListDismissalsAsync(CancellationToken cancellationToken) =>
        await _database.DiscoverDismissals
            .AsNoTracking()
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<DiscoverDismissal> DismissAsync(DiscoverDismissalInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Artist) || string.IsNullOrWhiteSpace(input.Title))
        {
            throw new DiscoverValidationException("A dismissal needs an artist and a title.");
        }

        string? mbid = null;
        if (!string.IsNullOrWhiteSpace(input.MbRecordingId))
        {
            if (!Guid.TryParse(input.MbRecordingId.Trim(), out var parsed))
            {
                throw new DiscoverValidationException("mbRecordingId is not a MusicBrainz id.");
            }

            mbid = parsed.ToString("D", CultureInfo.InvariantCulture);
        }

        var artistKey = TextMatching.NormalizeArtist(input.Artist);
        var titleKey = TextMatching.Normalize(input.Title);

        if (artistKey.Length == 0 || titleKey.Length == 0)
        {
            throw new DiscoverValidationException("A dismissal needs an artist and a title with letters or digits in them.");
        }

        var existing = await _database.DiscoverDismissals
            .FirstOrDefaultAsync(
                row => (mbid != null && row.MbRecordingId == mbid)
                    || (row.ArtistKey == artistKey && row.TitleKey == titleKey),
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        var row = new DiscoverDismissal
        {
            MbRecordingId = mbid,
            Artist = Truncate(input.Artist.Trim()),
            Title = Truncate(input.Title.Trim()),
            ArtistKey = artistKey,
            TitleKey = titleKey,
        };
        _database.DiscoverDismissals.Add(row);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return row;
    }

    /// <inheritdoc />
    public async Task<bool> UndoDismissalAsync(long id, CancellationToken cancellationToken)
    {
        var row = await _database.DiscoverDismissals
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return false;
        }

        _database.DiscoverDismissals.Remove(row);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>Picks <paramref name="count"/> ids with a partial Fisher-Yates shuffle that depends only on the seed.</summary>
    internal static List<long> Sample(IReadOnlyList<long> ids, int count, int seed)
    {
        var pool = ids.ToArray();
        var take = Math.Min(Math.Max(count, 0), pool.Length);
        var random = new Random(seed);

        for (var i = 0; i < take; i++)
        {
            var j = random.Next(i, pool.Length);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        return [.. pool.Take(take)];
    }

    private static string Truncate(string value) => value.Length <= 512 ? value : value[..512];

    private async Task<List<RecommendationSeed>> LoadSeedsAsync(
        List<long> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await _database.Songs
            .AsNoTracking()
            .Where(song => ids.Contains(song.Id))
            .Select(song => new SeedRow(
                song.Id,
                song.Title,
                song.PrimaryArtist.Name,
                song.ArtistCredit,
                song.MbRecordingId,
                song.DeezerId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byId = rows.ToDictionary(row => row.Id);

        // Keep the order the ids came in: for a mix it is the order the sample drew them.
        return
        [
            .. ids
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .Select(row => new RecommendationSeed(
                    row.Id,
                    row.Title,
                    string.IsNullOrWhiteSpace(row.ArtistName) ? row.ArtistCredit : row.ArtistName,
                    string.IsNullOrWhiteSpace(row.MbRecordingId) ? null : row.MbRecordingId.ToLowerInvariant(),
                    row.DeezerId)),
        ];
    }

    /// <summary>Serves the merged candidates of a seed from the cache, or asks every source and merges.</summary>
    private async Task<CachedRun> RunAsync(
        string cacheKey,
        IReadOnlyList<RecommendationSeed> seeds,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(cacheKey, out CachedRun? cached) && cached is not null)
        {
            return cached;
        }

        // A person is waiting on the page: a source behind a long Retry-After fails at once.
        using var interactive = InteractiveRequests.Begin();

        var runs = await Task.WhenAll(_sources.Select(source => AskAsync(source, seeds, cancellationToken)))
            .ConfigureAwait(false);

        var run = new CachedRun(
            seeds,
            [.. runs.Select(item => new DiscoverSourceInfo(item.Name, StatusName(item.Status)))],
            SuggestionMerger.Merge(runs));

        // A request in which no source answered says nothing about the song; try again next time.
        var hours = _options.CurrentValue.CacheHours;
        if (hours > 0 && runs.Any(item => item.Status == RecommendationSourceStatus.Answered))
        {
            _cache.Set(cacheKey, run, TimeSpan.FromHours(hours));
        }

        return run;
    }

    /// <summary>Asks one source about every seed, in turn. A failing seed does not stop the next one.</summary>
    private async Task<SourceRun> AskAsync(
        IRecommendationSource source,
        IReadOnlyList<RecommendationSeed> seeds,
        CancellationToken cancellationToken)
    {
        var lists = new List<IReadOnlyList<RecommendationCandidate>>();
        var answered = false;
        var failed = false;

        foreach (var seed in seeds)
        {
            RecommendationSourceResult result;

            try
            {
                result = await source.GetAsync(seed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogSourceFailed(_logger, source.Name, exception);
                result = RecommendationSourceResult.Failed;
            }

            switch (result.Status)
            {
                case RecommendationSourceStatus.Answered:
                    answered = true;
                    lists.Add(result.Candidates);
                    break;
                case RecommendationSourceStatus.Failed:
                    failed = true;
                    break;
                default:
                    break;
            }
        }

        var status = answered
            ? RecommendationSourceStatus.Answered
            : failed ? RecommendationSourceStatus.Failed : RecommendationSourceStatus.Skipped;

        return new SourceRun(source.Name, source.Weight, status, lists);
    }

    /// <summary>Applies the filters that must not be cached: the library, the seeds and the dismissals.</summary>
    private async Task<DiscoverResult> FinishAsync(
        string mode,
        int? mixSeed,
        CachedRun run,
        int limit,
        CancellationToken cancellationToken)
    {
        var library = await _database.Songs
            .AsNoTracking()
            .Select(song => new LibraryRow(
                song.MbRecordingId,
                song.DeezerId,
                song.Isrcs,
                song.Title,
                song.ArtistCredit,
                song.PrimaryArtist.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var dismissals = await _database.DiscoverDismissals
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var suggestions = SuggestionMerger.Filter(
            run.Candidates,
            library,
            run.Seeds,
            dismissals,
            _options.CurrentValue.MaxPerArtist);

        var shown = Math.Clamp(limit, 1, MaxLimit);

        return new DiscoverResult(
            mode,
            mixSeed,
            shown,
            suggestions.Count,
            [.. run.Seeds.Select(seed => new DiscoverSeedInfo(seed.SongId, seed.Title, seed.Artist))],
            run.Sources,
            [.. suggestions.Take(shown)]);
    }

    private static string StatusName(RecommendationSourceStatus status) => status switch
    {
        RecommendationSourceStatus.Answered => "answered",
        RecommendationSourceStatus.Failed => "failed",
        _ => "skipped",
    };

    [LoggerMessage(Level = LogLevel.Debug, Message = "Discover source {Source} failed.")]
    private static partial void LogSourceFailed(ILogger logger, string source, Exception exception);

    private sealed record SeedRow(
        long Id,
        string Title,
        string ArtistName,
        string ArtistCredit,
        string? MbRecordingId,
        long? DeezerId);

    private sealed record CachedRun(
        IReadOnlyList<RecommendationSeed> Seeds,
        IReadOnlyList<DiscoverSourceInfo> Sources,
        IReadOnlyList<DiscoverSuggestion> Candidates);
}
