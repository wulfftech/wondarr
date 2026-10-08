using Wondarr.Core.Domain;

namespace Wondarr.Sources.Torznab.Clients;

/// <summary>
/// Drives a torrent download client: add a torrent so that nothing downloads until Wondarr has
/// chosen the files, list them, give only the wanted ones priority, start, follow and remove it.
/// P7-07 builds the grab/track/import flow on this (ADR-0009).
/// </summary>
public interface ITorrentClient
{
    /// <summary>Adds a torrent to the client. A <c>.torrent</c> is added stopped; a magnet is added running until its metadata arrives.</summary>
    /// <param name="client">The configured client row.</param>
    /// <param name="request">What to add; exactly one of the file and the magnet.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task AddAsync(DownloadClient client, TorrentAddRequest request, CancellationToken cancellationToken);

    /// <summary>Reads one torrent, or <see langword="null"/> when the client does not have it.</summary>
    /// <param name="client">The configured client row.</param>
    /// <param name="infoHash">The torrent's infohash.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<TorrentInfo?> GetAsync(DownloadClient client, string infoHash, CancellationToken cancellationToken);

    /// <summary>Lists a torrent's files; empty while the client is still fetching a magnet's metadata.</summary>
    /// <param name="client">The configured client row.</param>
    /// <param name="infoHash">The torrent's infohash.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<TorrentFileInfo>> GetFilesAsync(DownloadClient client, string infoHash, CancellationToken cancellationToken);

    /// <summary>Gives the named files a priority: <c>1</c> downloads them, <c>0</c> skips them.</summary>
    /// <param name="client">The configured client row.</param>
    /// <param name="infoHash">The torrent's infohash.</param>
    /// <param name="fileIndexes">The indexes of the files, as <see cref="TorrentFileInfo.Index"/> reported them.</param>
    /// <param name="priority">The priority to give them, <c>0</c> or <c>1</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task SetFilePriorityAsync(DownloadClient client, string infoHash, IReadOnlyCollection<int> fileIndexes, int priority, CancellationToken cancellationToken);

    /// <summary>Starts a stopped torrent.</summary>
    /// <param name="client">The configured client row.</param>
    /// <param name="infoHash">The torrent's infohash.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task StartAsync(DownloadClient client, string infoHash, CancellationToken cancellationToken);

    /// <summary>Stops a torrent without removing it.</summary>
    /// <param name="client">The configured client row.</param>
    /// <param name="infoHash">The torrent's infohash.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task StopAsync(DownloadClient client, string infoHash, CancellationToken cancellationToken);

    /// <summary>Removes a torrent from the client. Nothing but this call ever passes <c>true</c> for <paramref name="deleteFiles"/>.</summary>
    /// <param name="client">The configured client row.</param>
    /// <param name="infoHash">The torrent's infohash.</param>
    /// <param name="deleteFiles">Whether the client deletes the downloaded data with the torrent.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task RemoveAsync(DownloadClient client, string infoHash, bool deleteFiles, CancellationToken cancellationToken);
}

/// <summary>What to add to a torrent client; exactly one of the two payloads is set.</summary>
/// <param name="InfoHash">The torrent's infohash, lower-case hex.</param>
/// <param name="TorrentFile">The `.torrent` file's bytes, or <see langword="null"/> when a magnet is added.</param>
/// <param name="MagnetUrl">The magnet link, or <see langword="null"/> when a file is added.</param>
public sealed record TorrentAddRequest(string InfoHash, byte[]? TorrentFile, string? MagnetUrl);

/// <summary>Where a torrent is in its life, as Wondarr sees it.</summary>
public enum TorrentStatus
{
    /// <summary>A magnet whose metadata has not arrived yet, so the files are not known.</summary>
    FetchingMetadata,

    /// <summary>The torrent is stopped; nothing is transferred.</summary>
    Stopped,

    /// <summary>Queueing is on and the torrent waits for its turn.</summary>
    Queued,

    /// <summary>The torrent is downloading.</summary>
    Downloading,

    /// <summary>The torrent is downloading but no peer has the data.</summary>
    Stalled,

    /// <summary>The client is checking or allocating the torrent's data.</summary>
    Checking,

    /// <summary>The client is moving the torrent's data.</summary>
    Moving,

    /// <summary>The torrent has finished downloading.</summary>
    Completed,

    /// <summary>The client reports the torrent as failed.</summary>
    Error,
}

/// <summary>One torrent as the client sees it. The paths are already mapped to Wondarr's view.</summary>
/// <param name="InfoHash">The torrent's infohash, lower-case hex.</param>
/// <param name="Name">The torrent's name.</param>
/// <param name="Status">Where the torrent is.</param>
/// <param name="Progress">How much is downloaded, 0 to 1.</param>
/// <param name="SavePath">The client's save path as Wondarr sees it.</param>
/// <param name="ContentPath">The torrent's content path as Wondarr sees it.</param>
/// <param name="HasMetadata">Whether the files are known yet; false while a magnet's metadata is fetching.</param>
/// <param name="Error">Why the torrent failed, or <see langword="null"/>.</param>
public sealed record TorrentInfo(
    string InfoHash,
    string Name,
    TorrentStatus Status,
    double Progress,
    string SavePath,
    string ContentPath,
    bool HasMetadata,
    string? Error);

/// <summary>One file of a torrent.</summary>
/// <param name="Index">The file's index in the client's list; what the priority call names it by.</param>
/// <param name="Path">The file's path inside the torrent.</param>
/// <param name="Size">The file's size in bytes.</param>
/// <param name="Progress">How much of the file is downloaded, 0 to 1.</param>
/// <param name="Priority">The file's priority: <c>0</c> skipped, <c>1</c> wanted, higher values the client's own.</param>
public sealed record TorrentFileInfo(int Index, string Path, long Size, double Progress, int Priority);
