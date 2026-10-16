using System.Globalization;
using System.Xml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Domain;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.Indexers;
using Wondarr.Core.Logging;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Organizer;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Wondarr.Sources.Torznab.Clients;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Sources.Torznab.Parsing;

namespace Wondarr.Sources.Torznab.Searching;

/// <summary>
/// The torrent or the usenet source behind <see cref="ISourceProvider"/> (one registration per
/// protocol, tier 3): a song is searched for as the releases it appears on, every enabled indexer of
/// the protocol is asked for each, the containers' file lists are read, and the wanted song's file in
/// each becomes one candidate (DECISIONS build session 8 #2, #3, #7; MATCHING_ENGINE §6.4). The grab
/// half lives in <c>IndexerSourceProvider.Grab.cs</c>.
/// </summary>
public sealed partial class IndexerSourceProvider : ISourceProvider
{
    /// <summary>How long one search may spend on the indexers and the file lists.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many releases of one torrent query have their file lists read, and how many NZBs one usenet
    /// indexer (each upstream indexer behind a Prowlarr row) is asked for in one whole search: an NZB
    /// download counts against the indexer's daily quota (free plans allow 5 to 20), a <c>.torrent</c>
    /// read does not.
    /// </summary>
    public const int ContainersPerQuery = 8;

    /// <summary>The seeded Unknown quality: what <see cref="ReleaseQualityParser"/> answers for a name that claims none.</summary>
    private const long UnknownQuality = 1;

    /// <summary>
    /// How long the file lists of what the indexers did answer may still be read once the budget is
    /// spent, so one stuck indexer does not throw away what the others found.
    /// </summary>
    private static readonly TimeSpan ContainerGrace = TimeSpan.FromSeconds(10);

    /// <summary>How many <c>.torrent</c> or NZB downloads run at once.</summary>
    private const int DownloadParallelism = 4;

    private readonly DownloadProtocol _protocol;
    private readonly IServiceScopeFactory _scopes;
    private readonly IIndexerClientFactory _clients;
    private readonly ITorrentClient _torrents;
    private readonly IUsenetClient _usenet;
    private readonly IDiskOperations _disk;
    private readonly IOptionsMonitor<ImportOptions> _import;
    private readonly TimeProvider _time;
    private readonly ILogger<IndexerSourceProvider> _logger;
    private readonly TimeSpan _budget;
    private readonly NzbCache _nzbCache;
    private readonly NzbCooldowns _nzbCooldowns;
    private readonly ISecretRegistry? _secrets;
    private readonly IOptionsMonitor<SearchOptions>? _search;

    /// <summary>Initialises a new instance of the <see cref="IndexerSourceProvider"/> class.</summary>
    /// <param name="protocol">Torrents (Torznab) or usenet (Newznab).</param>
    /// <param name="scopes">
    /// Opens a scope per call for the indexer and client rows and the MusicBrainz client (a typed
    /// HTTP client, which a singleton must not hold on to).
    /// </param>
    /// <param name="clients">Chooses the client for an indexer row.</param>
    /// <param name="torrents">The torrent download client (qBittorrent).</param>
    /// <param name="usenet">The usenet download client (SABnzbd).</param>
    /// <param name="disk">Stages finished files: hard links, copies, moves.</param>
    /// <param name="import">Where finished files are staged (<c>import.container_staging_path</c>).</param>
    /// <param name="time">The clock a post's age is measured with.</param>
    /// <param name="logger">One Warning per indexer or download that failed.</param>
    /// <param name="budget">How long a search may take; <see cref="DefaultBudget"/> when null.</param>
    /// <param name="nzbCache">
    /// Holds the NZBs the search downloads so a grab can reuse them; the host shares one across the
    /// sources. A private one when null.
    /// </param>
    /// <param name="secrets">
    /// Removes the API keys it knows from what an indexer says about its limit; only the pattern
    /// based cleansing when null.
    /// </param>
    /// <param name="search">
    /// The search settings; <c>search.max_container_size_mb</c> is the largest post a grab downloads whole
    /// when it could not be trimmed. The default limit when null.
    /// </param>
    /// <param name="nzbCooldowns">
    /// Remembers which indexers refused NZB downloads and until when, across searches; the host shares one
    /// across the sources. A private one when null.
    /// </param>
    public IndexerSourceProvider(
        DownloadProtocol protocol,
        IServiceScopeFactory scopes,
        IIndexerClientFactory clients,
        ITorrentClient torrents,
        IUsenetClient usenet,
        IDiskOperations disk,
        IOptionsMonitor<ImportOptions> import,
        TimeProvider time,
        ILogger<IndexerSourceProvider> logger,
        TimeSpan? budget = null,
        NzbCache? nzbCache = null,
        ISecretRegistry? secrets = null,
        IOptionsMonitor<SearchOptions>? search = null,
        NzbCooldowns? nzbCooldowns = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(torrents);
        ArgumentNullException.ThrowIfNull(usenet);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _protocol = protocol;
        _scopes = scopes;
        _clients = clients;
        _torrents = torrents;
        _usenet = usenet;
        _disk = disk;
        _import = import;
        _time = time;
        _logger = logger;
        _budget = budget ?? DefaultBudget;
        _nzbCache = nzbCache ?? new NzbCache(time);
        _nzbCooldowns = nzbCooldowns ?? new NzbCooldowns(time);
        _secrets = secrets;
        _search = search;
    }

    /// <summary>The largest usenet post (bytes) worth downloading whole; <c>search.max_container_size_mb</c>.</summary>
    private long MaxContainerBytes => (_search?.CurrentValue.MaxContainerSizeMb ?? new SearchOptions().MaxContainerSizeMb) * 1024L * 1024L;

    /// <inheritdoc />
    public string SourceType => _protocol == DownloadProtocol.Torrent ? SourceTypes.Torznab : SourceTypes.Newznab;

    private string Label => _protocol == DownloadProtocol.Torrent ? "Torrents" : "Usenet";

    /// <inheritdoc />
    public async Task<(bool Available, string? Reason)> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        var scope = _scopes.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var (indexers, reason) = await LoadIndexersAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);

            return indexers.Count > 0 ? (true, null) : (false, reason);
        }
    }

    /// <inheritdoc />
    public async Task<SourceSearchResult> SearchAsync(SongSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scope = _scopes.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            return await SearchAsync(scope.ServiceProvider, request, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SourceSearchResult> SearchAsync(
        IServiceProvider services,
        SongSearchRequest request,
        CancellationToken cancellationToken)
    {
        var (indexers, reason) = await LoadIndexersAsync(services, cancellationToken).ConfigureAwait(false);

        if (indexers.Count == 0)
        {
            return new SourceSearchResult([], [], reason);
        }

        var targets = await ReleaseTargets.FindAsync(
            services.GetRequiredService<IMusicBrainzClient>(),
            request,
            exception => LogMusicBrainzFailed(_logger, exception),
            cancellationToken).ConfigureAwait(false);

        if (targets.Count == 0)
        {
            return new SourceSearchResult([], [], $"{Label}: the song has no MusicBrainz release to search for");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_budget);

        var state = new SearchState(Redact);
        var queries = new List<string>();
        var candidates = new List<Candidate>();

        foreach (var target in targets)
        {
            if (budget.IsCancellationRequested)
            {
                break;
            }

            var query = new ReleaseQuery(target.Artist, target.Album, target.Year);
            var text = string.Concat(target.Artist, " ", target.Album);
            queries.Add(text);

            var releases = await SearchIndexersAsync(indexers, query, state, cancellationToken, budget.Token)
                .ConfigureAwait(false);

            var unseen = releases.Where(release => state.Releases.Add(ReleaseKey(release)));

            // Torrents: the best-seeded of each query. Usenet: every NZB read costs a download from the
            // indexer's daily quota, so the cap is per upstream indexer for the whole search (not per
            // query), and the posts that name a quality are read first, the likeliest to hold the song in
            // a form that can be grabbed. An NZB already in the cache costs no download and no slot.
            var fresh = _protocol == DownloadProtocol.Torrent
                ? unseen.OrderByDescending(release => release.Seeders ?? 0).Take(ContainersPerQuery).ToList()
                : state.PlanNzbReads(
                    unseen
                        .OrderByDescending(release => ReleaseQualityParser.Parse(release.Title) != UnknownQuality)
                        .ThenByDescending(release => release.Grabs ?? 0),
                    release => _nzbCache.TryGet(NzbKey(release), out _));

            using var grace = budget.IsCancellationRequested
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            grace?.CancelAfter(ContainerGrace);

            var containers = await ReadContainersAsync(indexers, fresh, state, cancellationToken, grace?.Token ?? budget.Token)
                .ConfigureAwait(false);

            foreach (var container in containers)
            {
                var candidate = ContainerCandidates.Build(
                    _protocol,
                    container,
                    new ContainerMatchRequest(request.Title, request.VersionFlags, target.TrackNo, request.DurationMs, 1, SongArtist: request.ArtistCredit, SongAlbum: ContextAlbum(request), TargetAlbum: target.Album),
                    text,
                    _time.GetUtcNow());

                if (candidate is null)
                {
                    state.NotFound++;
                }
                else if (state.Keys.Add(candidate.BlocklistKey))
                {
                    candidates.Add(candidate);
                }
            }

            if (request.IsPoolGoodEnough?.Invoke(candidates) == true)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        LogCoolings(state);

        return new SourceSearchResult(candidates, queries, Message(state, budget.IsCancellationRequested));
    }

    /// <summary>
    /// The album context's title, unless the context is a pseudo-album. The request does not carry the
    /// context's kind, but a pseudo-album ("Singles") is not a MusicBrainz release and has no release id,
    /// while a real album has one; the release the search targets stands for the album otherwise.
    /// </summary>
    private static string? ContextAlbum(SongSearchRequest request) =>
        string.IsNullOrWhiteSpace(request.AlbumMbReleaseId) || string.IsNullOrWhiteSpace(request.AlbumTitle) ? null : request.AlbumTitle;

    /// <summary>
    /// The enabled indexers of this protocol that have a client, in priority order — or none, with why,
    /// when no indexer or no download client of the protocol is enabled.
    /// </summary>
    private async Task<(IReadOnlyList<Indexer> Indexers, string? Reason)> LoadIndexersAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var indexerRows = await services.GetRequiredService<IIndexerService>()
            .ListAsync(cancellationToken).ConfigureAwait(false);
        var clientRows = await services.GetRequiredService<IDownloadClientService>()
            .ListAsync(cancellationToken).ConfigureAwait(false);

        var indexers = indexerRows
            .Where(indexer => indexer.Enabled && indexer.Protocol == _protocol && HasClient(indexer))
            .OrderBy(indexer => indexer.Priority)
            .ThenBy(indexer => indexer.Id)
            .ToList();

        var client = _protocol == DownloadProtocol.Torrent ? "torrent" : "usenet";

        if (indexers.Count == 0)
        {
            return ([], $"{Label}: no enabled {client} indexer");
        }

        if (!clientRows.Any(row => row.Enabled && row.Protocol == _protocol))
        {
            return ([], $"{Label}: no enabled {client} download client");
        }

        return (indexers, null);
    }

    /// <summary>Whether this build has a client for the indexer's type (Prowlarr and Gazelle arrive with P7-03b).</summary>
    private bool HasClient(Indexer indexer)
    {
        try
        {
            _clients.GetClient(indexer);
            return true;
        }
        catch (IndexerException)
        {
            return false;
        }
    }

    /// <summary>Asks every indexer at once; the results come back in the indexers' priority order.</summary>
    private async Task<IReadOnlyList<IndexerRelease>> SearchIndexersAsync(
        IReadOnlyList<Indexer> indexers,
        ReleaseQuery query,
        SearchState state,
        CancellationToken cancellationToken,
        CancellationToken budget)
    {
        var searches = indexers.Select(async indexer =>
        {
            try
            {
                return await _clients.GetClient(indexer).SearchAsync(indexer, query, budget).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                && budget.IsCancellationRequested
                && (exception is OperationCanceledException || exception.InnerException is OperationCanceledException))
            {
                // Whichever way the client reported it (the cancellation itself, or wrapped in an
                // IndexerException by a conversion that ran as the budget fired).
                // The search's time budget ran out before this indexer answered: say so, an empty
                // answer with no trace would look like "nothing found".
                LogIndexerBudgetSpent(_logger, indexer.Name);
                return [];
            }
            catch (Exception exception) when (exception is IndexerException or HttpRequestException or XmlException
                || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                LogIndexerFailed(_logger, indexer.Name, exception);
                state.AddError(indexer.Name);
                return (IReadOnlyList<IndexerRelease>)[];
            }
        });

        var results = await Task.WhenAll(searches).ConfigureAwait(false);

        // A release with neither link could never be grabbed.
        return results
            .SelectMany(releases => releases)
            .Where(release => release.Protocol == _protocol
                && (!string.IsNullOrWhiteSpace(release.DownloadUrl) || !string.IsNullOrWhiteSpace(release.MagnetUrl)))
            .ToList();
    }

    /// <summary>Reads the file list of each release; a release whose container cannot be read is dropped.</summary>
    private async Task<IReadOnlyList<ContainerListing>> ReadContainersAsync(
        IReadOnlyList<Indexer> indexers,
        List<IndexerRelease> releases,
        SearchState state,
        CancellationToken cancellationToken,
        CancellationToken budget)
    {
        var listings = new ContainerListing?[releases.Count];

        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, releases.Count),
                new ParallelOptions { MaxDegreeOfParallelism = DownloadParallelism, CancellationToken = budget },
                async (index, token) =>
                {
                    var release = releases[index];
                    var indexer = indexers.FirstOrDefault(row => row.Id == release.IndexerId);

                    if (indexer is null)
                    {
                        return;
                    }

                    try
                    {
                        listings[index] = await ReadContainerAsync(indexer, release, state, token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IndexerException or HttpRequestException
                        or TorrentMetainfoException or XmlException or InvalidDataException
                        || (exception is OperationCanceledException && !token.IsCancellationRequested))
                    {
                        LogContainerFailed(_logger, release.IndexerName, exception);
                        state.AddUnreadable();
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The budget ran out: the containers read so far still count.
        }

        return listings.OfType<ContainerListing>().ToList();
    }

    /// <summary>
    /// The release's files: a Gazelle file list as given, a <c>.torrent</c>'s metainfo, an NZB's audio
    /// files when every name in it is plain (from the cache, or from a download another search has under
    /// way, when there is one); a magnet or an obfuscated NZB has none yet. Null when an indexer that
    /// already refused an NZB in this search, or is cooling down from an earlier one, is not asked again,
    /// or refuses this one, or has had its 8 reads.
    /// </summary>
    private async Task<ContainerListing?> ReadContainerAsync(Indexer indexer, IndexerRelease release, SearchState state, CancellationToken token)
    {
        if (release.FileList is { Count: > 0 } given)
        {
            return new ContainerListing(
                release,
                [.. given.Select(file => new ContainerFile(file.Index, file.Path, file.Size))],
                release.InfoHash,
                given.Sum(file => file.Size));
        }

        var hash = release.InfoHash ?? InfoHashOf(release.MagnetUrl);

        if (_protocol == DownloadProtocol.Torrent
            && (release.DownloadUrl is null || release.DownloadUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)))
        {
            return new ContainerListing(release, null, hash, null);
        }

        if (_protocol == DownloadProtocol.Usenet)
        {
            return await ReadNzbAsync(indexer, release, hash, state, token).ConfigureAwait(false);
        }

        var download = await _clients.GetClient(indexer).DownloadAsync(indexer, release, token).ConfigureAwait(false);

        if (download.Content is not { } content)
        {
            var magnet = release with { MagnetUrl = download.MagnetUrl ?? release.MagnetUrl };
            return new ContainerListing(magnet, null, hash ?? InfoHashOf(download.MagnetUrl), null);
        }

        var metainfo = TorrentMetainfo.Parse(content);

        return new ContainerListing(
            release,
            [.. metainfo.Files.Select(file => new ContainerFile(file.Index, file.Path, file.Size))],
            metainfo.InfoHash,
            metainfo.TotalSize);
    }

    /// <summary>The audio files of an NZB's post when every name in it is plain; none yet otherwise.</summary>
    private static ContainerListing ListNzb(IndexerRelease release, byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        var files = NzbFileList.Parse(stream);

        // The whole post downloads, archives and Par2 volumes included: that is the size the
        // container-size limit is about, not the audio files' alone.
        var postSize = files.Sum(file => file.Size);

        if (!NzbFileList.IsClean(files))
        {
            return new ContainerListing(release, null, null, postSize);
        }

        return new ContainerListing(
            release,
            [.. files.Where(file => file.IsAudio && file.FileName is not null)
                .Select(file => new ContainerFile(file.Index, file.FileName!, file.Size))],
            null,
            postSize);
    }

    /// <summary>Where a release's NZB is kept: the search puts it there, the next search and the grab look there.</summary>
    private static string NzbKey(IndexerRelease release) =>
        NzbCache.KeyOf(release.IndexerId, release.DownloadUrl, release.ReleaseId);

    /// <summary>Removes the secrets Wondarr knows from text an indexer sent.</summary>
    private string Redact(string text) => _secrets is null ? CleanseLogMessage.Cleanse(text) : _secrets.Redact(text);

    /// <summary>One release seen on two indexers (or for two queries) is read once.</summary>
    private static string ReleaseKey(IndexerRelease release) =>
        !string.IsNullOrEmpty(release.InfoHash)
            ? string.Concat("h:", release.InfoHash.ToLowerInvariant())
            : string.Concat("g:", release.IndexerId.ToString(CultureInfo.InvariantCulture), ":", release.ReleaseId);

    private string? Message(SearchState state, bool budgetSpent)
    {
        var parts = new List<string>();

        if (state.Errors.Count > 0)
        {
            parts.Add(string.Concat("failed: ", string.Join(", ", state.Errors)));
        }

        parts.AddRange(state.Refusals);

        foreach (var (source, until) in state.Coolings)
        {
            parts.Add(string.Concat(source.Name, " is cooling down until ", LocalTime(until), " after refusing NZB downloads"));
        }

        if (state.NotFound > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{state.NotFound} release(s) did not contain the song"));
        }

        if (state.Unreadable > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{state.Unreadable} release(s) could not be read"));
        }

        if (budgetSpent)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"stopped after {_budget.TotalSeconds:0} s"));
        }

        return parts.Count == 0 ? null : string.Concat(Label, ": ", string.Join("; ", parts));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Indexer {Indexer} failed a search")]
    private static partial void LogIndexerFailed(ILogger logger, string indexer, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Indexer} did not answer before the search's time budget ran out")]
    private static partial void LogIndexerBudgetSpent(ILogger logger, string indexer);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A release from {Indexer} could not be read")]
    private static partial void LogContainerFailed(ILogger logger, string indexer, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MusicBrainz could not list the song's releases")]
    private static partial void LogMusicBrainzFailed(ILogger logger, Exception exception);

    /// <summary>The magnet's info-hash, or null when it has none Wondarr can use.</summary>
    private static string? InfoHashOf(string? magnet) =>
        magnet is not null && MagnetLink.TryGetInfoHash(magnet, out var hex) ? hex : null;

    /// <summary>What one search collected besides candidates. Shared by the parallel indexer calls.</summary>
    private sealed class SearchState(Func<string, string> redact)
    {
        private readonly Lock _gate = new();

        // NZB downloads started so far, per source: the cap is for the whole search.
        private readonly Dictionary<NzbSource, int> _nzbReads = [];

        // The sources that refused an NZB, and what each said ("name: text"), in the order they did.
        private readonly HashSet<NzbSource> _refused = [];
        private readonly List<string> _refusals = [];

        // The sources found cooling down from an earlier search whose posts were therefore dropped, and
        // until when, in the order they were found.
        private readonly Dictionary<NzbSource, DateTimeOffset> _cooling = [];

        public HashSet<string> Releases { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

        public List<string> Errors { get; } = [];

        public int NotFound { get; set; }

        private int _unreadable;

        public int Unreadable => Volatile.Read(ref _unreadable);

        public void AddUnreadable() => Interlocked.Increment(ref _unreadable);

        public IReadOnlyList<string> Refusals
        {
            get
            {
                lock (_gate)
                {
                    return [.. _refusals];
                }
            }
        }

        public void AddError(string indexer)
        {
            lock (_gate)
            {
                if (!Errors.Contains(indexer))
                {
                    Errors.Add(indexer);
                }
            }
        }

        /// <summary>Removes the secrets Wondarr knows from text an indexer sent.</summary>
        public string Redact(string text) => redact(text);

        /// <summary>The cooling sources that did not also refuse in this search, whose refusal already says it.</summary>
        public IReadOnlyList<KeyValuePair<NzbSource, DateTimeOffset>> Coolings
        {
            get
            {
                lock (_gate)
                {
                    return [.. _cooling.Where(entry => !_refused.Contains(entry.Key))];
                }
            }
        }

        /// <summary>Records that a source will hand out no more NZBs; the first answer is the one kept.</summary>
        /// <returns><see langword="true"/> for the first refusal of this source.</returns>
        public bool Refuse(NzbSource source, string text)
        {
            lock (_gate)
            {
                if (!_refused.Add(source))
                {
                    return false;
                }

                _refusals.Add(string.Concat(source.Name, ": ", text));

                return true;
            }
        }

        /// <summary>
        /// The releases of the order that are worth a turn at the reads: each source's first
        /// <see cref="ContainersPerQuery"/> downloads of the whole search (the ones already made count,
        /// the ones planned here are counted as they are listed), none from one that has refused. A release
        /// whose NZB is cached costs no download, so it takes no slot and is kept even for a source that
        /// has refused. Decided up front and in order, so which releases are read never depends on how the
        /// parallel reads happen to interleave; <see cref="BeginNzbRead"/> then settles each one again at
        /// the moment its read begins.
        /// </summary>
        public List<IndexerRelease> PlanNzbReads(IEnumerable<IndexerRelease> ordered, Func<IndexerRelease, bool> isCached)
        {
            var inOrder = ordered.Select(release => (Release: release, Cached: isCached(release))).ToList();
            var taken = new List<IndexerRelease>();
            var planned = new Dictionary<NzbSource, int>();

            lock (_gate)
            {
                foreach (var (release, cached) in inOrder)
                {
                    if (cached)
                    {
                        taken.Add(release);
                        continue;
                    }

                    var source = NzbSource.Of(release);

                    _nzbReads.TryGetValue(source, out var reads);
                    planned.TryGetValue(source, out var also);

                    if (reads + also >= ContainersPerQuery || _refused.Contains(source))
                    {
                        continue;
                    }

                    planned[source] = also + 1;
                    taken.Add(release);
                }
            }

            return taken;
        }

        /// <summary>
        /// What a read of one release does, decided and recorded in one step: the cache is looked at again
        /// now (an NZB the plan found there may have been evicted since, one it did not may have arrived), and
        /// a read slot is taken only when a download is really about to be made. The caller holds the lock of
        /// the downloads under way, so the answer to <paramref name="inFlight"/> cannot change before it acts.
        /// </summary>
        /// <param name="source">Where the release's NZB comes from.</param>
        /// <param name="key">The release's cache key.</param>
        /// <param name="inFlight">Whether another read is downloading this very NZB right now.</param>
        /// <param name="cache">The NZBs already read.</param>
        /// <param name="cooldowns">The sources that refused downloads in an earlier search.</param>
        /// <param name="kept">The NZB, when it is in the cache.</param>
        public NzbTurn BeginNzbRead(NzbSource source, string key, bool inFlight, NzbCache cache, NzbCooldowns cooldowns, out byte[]? kept)
        {
            lock (_gate)
            {
                if (cache.TryGet(key, out kept))
                {
                    return NzbTurn.Kept;
                }

                if (_refused.Contains(source))
                {
                    // Its quota is spent: another request would only be answered with the same sentence.
                    return NzbTurn.Skipped;
                }

                if (cooldowns.TryGetUntil(source, out var until))
                {
                    _cooling[source] = until.Value;

                    return NzbTurn.Cooling;
                }

                if (inFlight)
                {
                    return NzbTurn.Shared;
                }

                _nzbReads.TryGetValue(source, out var reads);

                if (reads >= ContainersPerQuery)
                {
                    return NzbTurn.Skipped;
                }

                _nzbReads[source] = reads + 1;

                return NzbTurn.Download;
            }
        }
    }

    /// <summary>What a read of one usenet release does (<see cref="SearchState.BeginNzbRead"/>).</summary>
    private enum NzbTurn
    {
        /// <summary>The NZB is in the cache: no download and no slot.</summary>
        Kept,

        /// <summary>Nothing is read: the source has refused in this search, or its 8 downloads are made.</summary>
        Skipped,

        /// <summary>The source refused in an earlier search and is cooling down: nothing is read, and the search says so.</summary>
        Cooling,

        /// <summary>Another read is downloading this NZB: wait for its answer; no slot.</summary>
        Shared,

        /// <summary>This read downloads the NZB, and has taken the slot for it.</summary>
        Download,
    }
}
