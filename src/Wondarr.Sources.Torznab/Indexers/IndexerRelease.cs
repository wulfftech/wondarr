using Wondarr.Core.Sources;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// One file inside a release container: its position, its path and its size in bytes. A Torznab or
/// Newznab feed states only a file count, so <see cref="IndexerRelease.FileList"/> stays
/// <see langword="null"/> for those; Gazelle fills it in (P7-03b).
/// </summary>
/// <param name="Index">The file's position in the container, zero-based.</param>
/// <param name="Path">The file's path inside the container.</param>
/// <param name="Size">The file's size in bytes.</param>
public sealed record ReleaseFile(int Index, string Path, long Size);

/// <summary>
/// One release an indexer listed, in the one record shape every indexer dialect parses into
/// (ARCHITECTURE §5.7, MATCHING_ENGINE §6.4): what it is called, where it is downloaded from, and the
/// swarm or grab numbers the matching engine scores with.
/// </summary>
/// <param name="Title">The release name, for example <c>Daft Punk - Discovery [2001] [FLAC]</c>.</param>
/// <param name="ReleaseId">The item's <c>guid</c>, the indexer's own id for the release.</param>
/// <param name="DownloadUrl">The enclosure URL, or the item's <c>link</c> when there is no enclosure.</param>
/// <param name="MagnetUrl">The <c>torznab:attr</c> magnet URL, or the download URL when it is a <c>magnet:</c> URI.</param>
/// <param name="InfoHash">The torrent's info hash, lower-case.</param>
/// <param name="Size">The release's size in bytes.</param>
/// <param name="PublishDate">When the release appeared.</param>
/// <param name="Seeders">The swarm's seeders, for torrents.</param>
/// <param name="Peers">The swarm's peers, for torrents.</param>
/// <param name="Grabs">How often the release was grabbed.</param>
/// <param name="Categories">The newznab category ids the release carries.</param>
/// <param name="DownloadVolumeFactor">The download volume factor; 0 is freeleech.</param>
/// <param name="InfoUrl">The release's details page, the item's <c>comments</c> element.</param>
/// <param name="Protocol">How the release is downloaded.</param>
/// <param name="IndexerId">The id of the indexer row that listed the release.</param>
/// <param name="IndexerName">The name of the indexer row that listed the release.</param>
/// <param name="FileList">The files inside the container, or <see langword="null"/> when the indexer does not list them.</param>
public sealed record IndexerRelease(
    string Title,
    string ReleaseId,
    string? DownloadUrl,
    string? MagnetUrl,
    string? InfoHash,
    long? Size,
    DateTimeOffset? PublishDate,
    int? Seeders,
    int? Peers,
    int? Grabs,
    IReadOnlyList<int> Categories,
    double? DownloadVolumeFactor,
    string? InfoUrl,
    DownloadProtocol Protocol,
    long IndexerId,
    string IndexerName,
    IReadOnlyList<ReleaseFile>? FileList);
