using System.Globalization;
using System.Xml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Domain;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.Indexers;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Organizer;
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

    /// <summary>How many releases of one query have their file lists read.</summary>
    public const int ContainersPerQuery = 8;

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
        TimeSpan? budget = null)
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
    }

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

        var state = new SearchState();
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

            var fresh = releases
                .Where(release => state.Releases.Add(ReleaseKey(release)))
                .OrderByDescending(release => _protocol == DownloadProtocol.Torrent ? release.Seeders ?? 0 : release.Grabs ?? 0)
                .Take(ContainersPerQuery)
                .ToList();

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
                    new ContainerMatchRequest(request.Title, request.VersionFlags, target.TrackNo, request.DurationMs, 1),
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

        return new SourceSearchResult(candidates, queries, Message(state, budget.IsCancellationRequested));
    }

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
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return [];
            }
            catch (Exception exception) when (exception is IndexerException or HttpRequestException or XmlException)
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
                        listings[index] = await ReadContainerAsync(indexer, release, token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IndexerException or HttpRequestException
                        or TorrentMetainfoException or XmlException or InvalidDataException)
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
    /// files when every name in it is plain; a magnet or an obfuscated NZB has none yet (null).
    /// </summary>
    private async Task<ContainerListing> ReadContainerAsync(Indexer indexer, IndexerRelease release, CancellationToken token)
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

        var download = await _clients.GetClient(indexer).DownloadAsync(indexer, release, token).ConfigureAwait(false);

        if (download.Content is not { } content)
        {
            var magnet = release with { MagnetUrl = download.MagnetUrl ?? release.MagnetUrl };
            return new ContainerListing(magnet, null, hash ?? InfoHashOf(download.MagnetUrl), null);
        }

        if (_protocol == DownloadProtocol.Torrent)
        {
            var metainfo = TorrentMetainfo.Parse(content);
            return new ContainerListing(
                release,
                [.. metainfo.Files.Select(file => new ContainerFile(file.Index, file.Path, file.Size))],
                metainfo.InfoHash,
                metainfo.TotalSize);
        }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "A release from {Indexer} could not be read")]
    private static partial void LogContainerFailed(ILogger logger, string indexer, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MusicBrainz could not list the song's releases")]
    private static partial void LogMusicBrainzFailed(ILogger logger, Exception exception);

    /// <summary>The magnet's info-hash, or null when it has none Wondarr can use.</summary>
    private static string? InfoHashOf(string? magnet) =>
        magnet is not null && MagnetLink.TryGetInfoHash(magnet, out var hex) ? hex : null;

    /// <summary>What one search collected besides candidates. Shared by the parallel indexer calls.</summary>
    private sealed class SearchState
    {
        private readonly Lock _gate = new();

        public HashSet<string> Releases { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

        public List<string> Errors { get; } = [];

        public int NotFound { get; set; }

        private int _unreadable;

        public int Unreadable => Volatile.Read(ref _unreadable);

        public void AddUnreadable() => Interlocked.Increment(ref _unreadable);

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
    }
}
