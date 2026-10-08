using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Logging;
using Wondarr.Core.Notifications;

namespace Wondarr.Sources.Torznab.Clients;

/// <summary>
/// <see cref="ITorrentClient"/> over qBittorrent: reads the row's settings, registers the password
/// with the secret registry so the log pipeline can redact it, and maps the paths the client
/// reports to Wondarr's view of them.
/// </summary>
public sealed partial class QBittorrentClient : ITorrentClient
{
    private static readonly ConcurrentDictionary<string, bool> LoggedUnknownStates = [];

    private readonly QBittorrentProxy _proxy;
    private readonly ISecretRegistry _secrets;
    private readonly ILogger<QBittorrentClient> _logger;

    /// <summary>Initialises a new instance of the <see cref="QBittorrentClient"/> class.</summary>
    /// <param name="proxy">The qBittorrent HTTP layer.</param>
    /// <param name="secrets">Where the password is registered so the log pipeline can redact it.</param>
    /// <param name="logger">The log sink; never sees a password.</param>
    public QBittorrentClient(QBittorrentProxy proxy, ISecretRegistry secrets, ILogger<QBittorrentClient> logger)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);

        _proxy = proxy;
        _secrets = secrets;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AddAsync(DownloadClient client, TorrentAddRequest request, CancellationToken cancellationToken)
    {
        var (session, settings) = Resolve(client);

        if ((request.TorrentFile is { Length: > 0 }) == (request.MagnetUrl is { Length: > 0 }))
        {
            throw new ArgumentException("Exactly one of the torrent file and the magnet URL must be set.", nameof(request));
        }

        await _proxy.AddTorrentAsync(session, settings, request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TorrentInfo?> GetAsync(DownloadClient client, string infoHash, CancellationToken cancellationToken)
    {
        var (session, settings) = Resolve(client);

        IReadOnlyList<QBittorrentTorrent> torrents;

        try
        {
            torrents = await _proxy.GetTorrentsAsync(session, settings, infoHash, cancellationToken).ConfigureAwait(false);
        }
        catch (TorrentNotFoundException)
        {
            return null;
        }

        if (torrents.Count == 0)
        {
            return null;
        }

        var torrent = torrents[0];

        // qBittorrent reports has_metadata from 2.11.0; before that an empty file list is a magnet
        // whose metadata has not arrived.
        var hasMetadata = torrent.HasMetadata ?? await InferHasMetadataAsync(session, settings, torrent, cancellationToken).ConfigureAwait(false);
        var status = MapState(torrent.State, hasMetadata);

        if (!IsKnownState(torrent.State) && LoggedUnknownStates.TryAdd(torrent.State, true))
        {
            LogUnknownState(_logger, torrent.State);
        }

        return new TorrentInfo(
            torrent.Hash.ToLowerInvariant(),
            torrent.Name,
            status,
            torrent.Progress,
            RemotePathMapper.Map(torrent.SavePath, settings.RemotePathMappings),
            RemotePathMapper.Map(torrent.ContentPath, settings.RemotePathMappings),
            hasMetadata,
            ErrorFor(torrent.State));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TorrentFileInfo>> GetFilesAsync(DownloadClient client, string infoHash, CancellationToken cancellationToken)
    {
        var (session, settings) = Resolve(client);

        var files = await _proxy.GetTorrentFilesAsync(session, settings, infoHash, cancellationToken).ConfigureAwait(false);

        return [.. files.Select(file => new TorrentFileInfo(file.Index, file.Name, file.Size, file.Progress, file.Priority))];
    }

    /// <inheritdoc />
    public async Task SetFilePriorityAsync(DownloadClient client, string infoHash, IReadOnlyCollection<int> fileIndexes, int priority, CancellationToken cancellationToken)
    {
        if (priority is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "The priority must be 0 (skip the file) or 1 (download it).");
        }

        var (session, settings) = Resolve(client);

        await _proxy.SetFilePriorityAsync(session, settings, infoHash, fileIndexes, priority, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StartAsync(DownloadClient client, string infoHash, CancellationToken cancellationToken)
    {
        var (session, settings) = Resolve(client);

        return _proxy.StartTorrentAsync(session, settings, infoHash, cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(DownloadClient client, string infoHash, CancellationToken cancellationToken)
    {
        var (session, settings) = Resolve(client);

        return _proxy.StopTorrentAsync(session, settings, infoHash, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(DownloadClient client, string infoHash, bool deleteFiles, CancellationToken cancellationToken)
    {
        var (session, settings) = Resolve(client);

        return _proxy.RemoveTorrentAsync(session, settings, infoHash, deleteFiles, cancellationToken);
    }

    /// <summary>
    /// Maps a raw qBittorrent state name — both the 4.x (<c>pausedDL</c>) and the 5.x
    /// (<c>stoppedDL</c>) generations — to where the torrent is, as Wondarr sees it.
    /// </summary>
    /// <param name="state">The raw state name.</param>
    /// <param name="hasMetadata">Whether the files are known; a magnet stopped by its stop condition before the files are known is still fetching metadata.</param>
    public static TorrentStatus MapState(string state, bool hasMetadata) => state switch
    {
        "metaDL" or "forcedMetaDL" => TorrentStatus.FetchingMetadata,
        "pausedDL" or "stoppedDL" => hasMetadata ? TorrentStatus.Stopped : TorrentStatus.FetchingMetadata,
        "pausedUP" or "stoppedUP" or "uploading" or "stalledUP" or "queuedUP" or "forcedUP" => TorrentStatus.Completed,
        "queuedDL" => TorrentStatus.Queued,
        "downloading" or "forcedDL" => TorrentStatus.Downloading,
        "stalledDL" => TorrentStatus.Stalled,
        "checkingDL" or "checkingUP" or "checkingResumeData" or "allocating" => TorrentStatus.Checking,
        "moving" => TorrentStatus.Moving,
        "error" or "missingFiles" => TorrentStatus.Error,
        _ => TorrentStatus.Downloading,
    };

    private static bool IsKnownState(string state) => state is
        "metaDL" or "forcedMetaDL" or
        "pausedDL" or "stoppedDL" or
        "pausedUP" or "stoppedUP" or "uploading" or "stalledUP" or "queuedUP" or "forcedUP" or
        "queuedDL" or
        "downloading" or "forcedDL" or
        "stalledDL" or
        "checkingDL" or "checkingUP" or "checkingResumeData" or "allocating" or
        "moving" or
        "error" or "missingFiles";

    private static string? ErrorFor(string state) => state switch
    {
        "error" => "qBittorrent is reporting an error",
        "missingFiles" => "The download is missing files",
        _ => null,
    };

    /// <summary>Reads the row's settings, registers its password and picks the row's proxy session.</summary>
    private (QBittorrentSession Session, QBittorrentSettings Settings) Resolve(DownloadClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        var settings = QBittorrentSettings.FromJson(NotificationSecrets.Read(client.Settings));

        _secrets.Register(settings.Password);

        return (_proxy.Session(QBittorrentProxy.SessionKey(client.Id, client.Settings)), settings);
    }

    /// <summary>Whether the files are known, for a qBittorrent that does not report <c>has_metadata</c>.</summary>
    private async Task<bool> InferHasMetadataAsync(
        QBittorrentSession session,
        QBittorrentSettings settings,
        QBittorrentTorrent torrent,
        CancellationToken cancellationToken)
    {
        if (torrent.State is "metaDL" or "forcedMetaDL")
        {
            return false;
        }

        var files = await _proxy.GetTorrentFilesAsync(session, settings, torrent.Hash, cancellationToken).ConfigureAwait(false);

        return files.Count > 0;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "qBittorrent reported the unknown torrent state {State}; treating it as downloading")]
    private static partial void LogUnknownState(ILogger logger, string state);
}
