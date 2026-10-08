using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Paging;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Wondarr.Core.Wanted;
using Wondarr.Sources.Torznab.Clients;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Sources.Torznab.Parsing;

namespace Wondarr.Sources.Torznab.Searching;

/// <summary>
/// <c>release/push</c> for torrents and NZBs (DECISIONS build session 8 #11): the pushed release is
/// matched against the wanted songs by its parsed artist and album (or <c>Artist - Title</c> for a
/// single), its file list is read, and each matching song's candidate goes through the engine and is
/// grabbed when approved — the first grab bundles the others in the same container. Scoped: it runs in
/// the request's scope with the search service.
/// </summary>
public sealed partial class ReleasePushHandler : IReleasePushHandler
{
    private readonly IWantedService _wanted;
    private readonly ISongSearchService _search;
    private readonly IQueueService _queue;
    private readonly IDownloadClientService _clients;
    private readonly IIndexerClientFactory _indexers;
    private readonly TimeProvider _time;
    private readonly ILogger<ReleasePushHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="ReleasePushHandler"/> class.</summary>
    /// <param name="wanted">The wanted lists a push is matched against.</param>
    /// <param name="search">Judges and grabs each matching song's candidate.</param>
    /// <param name="queue">Tells which matching songs are downloading already (bundled by an earlier grab).</param>
    /// <param name="clients">Whether a download client of the protocol is enabled.</param>
    /// <param name="indexers">Reaches the pushed link through the push pseudo-indexer.</param>
    /// <param name="time">The clock a post's age is measured with.</param>
    /// <param name="logger">Logs the title and the outcome, never the links.</param>
    public ReleasePushHandler(
        IWantedService wanted,
        ISongSearchService search,
        IQueueService queue,
        IDownloadClientService clients,
        IIndexerClientFactory indexers,
        TimeProvider time,
        ILogger<ReleasePushHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(indexers);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _wanted = wanted;
        _search = search;
        _queue = queue;
        _clients = clients;
        _indexers = indexers;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ReleasePushOutcome> PushAsync(PushedRelease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);

        var protocol = release.Protocol.Trim().ToLowerInvariant() switch
        {
            "torrent" => DownloadProtocol.Torrent,
            "usenet" => DownloadProtocol.Usenet,
            _ => (DownloadProtocol?)null,
        };

        if (protocol is not { } known)
        {
            return Rejected(null, $"No download client for protocol '{release.Protocol}'");
        }

        var songs = await MatchAsync(release.Title, cancellationToken).ConfigureAwait(false);
        var first = songs.Count > 0 ? songs[0].Song : null;

        if (songs.Count == 0)
        {
            return Rejected(null, $"No wanted song matches '{release.Title}'");
        }

        var clients = await _clients.ListAsync(cancellationToken).ConfigureAwait(false);

        if (!clients.Any(client => client.Enabled && client.Protocol == known))
        {
            return Rejected(first, $"No download client for protocol '{release.Protocol}'");
        }

        ContainerListing listing;

        try
        {
            listing = await ReadAsync(release, known, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IndexerException or HttpRequestException or TorrentMetainfoException or XmlException or InvalidDataException)
        {
            return Rejected(first, string.Concat("The release could not be read: ", exception.Message));
        }

        var rejections = new List<string>();
        var grabbed = 0;
        var downloading = 0;

        foreach (var (song, trackNo) in songs)
        {
            // Bundled with an earlier song's grab of this release, or downloading already: asked of
            // the queue itself, not read from a message.
            if (await _queue.HasActiveForSongAsync(song.Id, cancellationToken).ConfigureAwait(false))
            {
                downloading++;
                continue;
            }

            var wanted = new ContainerMatchRequest(song.Title, VersionFlagNames.FromNames(song.VersionFlags), trackNo, song.DurationMs, 1);
            var candidate = ContainerCandidates.Build(known, listing, wanted, release.Title, _time.GetUtcNow());

            if (candidate is null)
            {
                rejections.Add($"'{song.Title}' is not in the release");
                continue;
            }

            var result = await _search.JudgeAsync(song.Id, [candidate], SearchTrigger.Push, grab: true, cancellationToken).ConfigureAwait(false);

            if (result.Outcome == SearchOutcome.Grabbed)
            {
                grabbed++;
            }
            else
            {
                var reasons = result.Decisions.SelectMany(decision => decision.Rejections).Select(rejection => rejection.Message).ToList();
                rejections.AddRange(reasons.Count > 0 ? reasons : [result.Message ?? "Not grabbed"]);
            }
        }

        LogPushed(_logger, release.Title, known, songs.Count, grabbed);

        if (grabbed > 0 && first is not null)
        {
            return new ReleasePushOutcome(true, [], first.Id, first.Title);
        }

        if (rejections.Count == 0 && downloading > 0)
        {
            rejections.Add("Already downloading");
        }

        return Rejected(first, [.. rejections.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// The wanted songs (missing, or below cutoff) the release is for: by its parsed artist and album
    /// (the song's album context, or the song itself for a single), else by <c>Artist - Title</c>.
    /// The song's track number is kept when the album is its album context.
    /// </summary>
    private async Task<List<(Song Song, int? TrackNo)>> MatchAsync(string title, CancellationToken cancellationToken)
    {
        var paging = new PagingSpec(1, PagingSpec.MaxPageSize, null, descending: false);
        var missing = await _wanted.GetMissingAsync(paging, cancellationToken).ConfigureAwait(false);
        var cutoff = await _wanted.GetCutoffUnmetAsync(paging, cancellationToken).ConfigureAwait(false);
        var wanted = missing.Records.Concat(cutoff.Records).DistinctBy(song => song.Id).ToList();

        var matches = new List<(Song, int?)>();

        if (ReleaseTitleParser.Parse(title) is { } parsed)
        {
            var artist = TextMatching.NormalizeArtist(parsed.Artist);
            var album = TextMatching.Normalize(parsed.Album);

            foreach (var song in wanted.Where(song => ArtistOf(song) == artist))
            {
                if (song.AlbumContext is { } context && TextMatching.Normalize(context.AlbumTitle) == album)
                {
                    matches.Add((song, context.TrackNo));
                }
                else if (TextMatching.Normalize(song.Title) == album)
                {
                    matches.Add((song, null));
                }
            }
        }

        if (matches.Count == 0 && SingleName(title) is { } single)
        {
            matches.AddRange(wanted
                .Where(song => ArtistOf(song) == single.Artist && TextMatching.Normalize(song.Title) == single.Title)
                .Select(song => (song, (int?)null)));
        }

        return matches;
    }

    /// <summary>The release's file list, read through the push pseudo-indexer (a magnet or an obfuscated post has none yet).</summary>
    private async Task<ContainerListing> ReadAsync(PushedRelease pushed, DownloadProtocol protocol, CancellationToken cancellationToken)
    {
        var magnetHash = pushed.MagnetUrl is { } magnet && MagnetLink.TryGetInfoHash(magnet, out var hex) ? hex : null;
        var release = new IndexerRelease(
            pushed.Title,
            ReleaseId(pushed),
            pushed.DownloadUrl,
            pushed.MagnetUrl,
            magnetHash,
            pushed.Size,
            pushed.PublishDate,
            null,
            null,
            null,
            [],
            null,
            null,
            protocol,
            PushedReleaseClient.IndexerId,
            PushedReleaseClient.Row(pushed.Indexer, protocol).Name,
            null);

        var indexer = PushedReleaseClient.Row(pushed.Indexer, protocol);
        var download = await _indexers.GetClient(indexer).DownloadAsync(indexer, release, cancellationToken).ConfigureAwait(false);

        if (download.Content is not { } content)
        {
            var hash = magnetHash ?? (download.MagnetUrl is { } link && MagnetLink.TryGetInfoHash(link, out var linkHash) ? linkHash : null);

            return new ContainerListing(release with { MagnetUrl = download.MagnetUrl ?? pushed.MagnetUrl, InfoHash = hash }, null, hash, null);
        }

        if (protocol == DownloadProtocol.Torrent)
        {
            var metainfo = TorrentMetainfo.Parse(content);

            return new ContainerListing(
                release with { InfoHash = metainfo.InfoHash },
                [.. metainfo.Files.Select(file => new ContainerFile(file.Index, file.Path, file.Size))],
                metainfo.InfoHash,
                metainfo.TotalSize);
        }

        using var stream = new MemoryStream(content, writable: false);
        var files = NzbFileList.Parse(stream);
        var size = files.Sum(file => file.Size);

        return NzbFileList.IsClean(files)
            ? new ContainerListing(
                release,
                [.. files.Where(file => file.IsAudio && file.FileName is not null).Select(file => new ContainerFile(file.Index, file.FileName!, file.Size))],
                null,
                size)
            : new ContainerListing(release, null, null, size);
    }

    /// <summary>
    /// A stable id for a pushed release without putting its link (which can carry a passkey) into the
    /// database: a hash of the link, or of the title when there is none.
    /// </summary>
    private static string ReleaseId(PushedRelease release)
    {
        var source = release.DownloadUrl ?? release.MagnetUrl ?? release.Title;

        return string.Concat("push-", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16]);
    }

    private static string ArtistOf(Song song) => TextMatching.NormalizeArtist(song.PrimaryArtist?.Name ?? song.ArtistCredit);

    /// <summary><c>Artist - Title [tags]</c>, normalised as the identity matcher compares them.</summary>
    private static (string Artist, string Title)? SingleName(string title)
    {
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);

        if (dash <= 0)
        {
            return null;
        }

        var artist = TextMatching.NormalizeArtist(title[..dash]);
        var name = TextMatching.Normalize(Bracketed().Replace(title[(dash + 3)..], " "));

        return artist.Length == 0 || name.Length == 0 ? null : (artist, name);
    }

    private static ReleasePushOutcome Rejected(Song? song, params string[] rejections) =>
        new(false, rejections, song?.Id, song?.Title);

    /// <summary>Release-name decorations: <c>[FLAC]</c>, <c>(320)</c>, <c>{WEB}</c>.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"[\[\(\{][^\]\)\}]*[\]\)\}]")]
    private static partial System.Text.RegularExpressions.Regex Bracketed();

    [LoggerMessage(Level = LogLevel.Information, Message = "Release push '{Title}' ({Protocol}): {Songs} wanted song(s) matched, {Grabbed} grabbed")]
    private static partial void LogPushed(ILogger logger, string title, DownloadProtocol protocol, int songs, int grabbed);
}
