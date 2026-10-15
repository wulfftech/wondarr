using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.Indexers;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using Wondarr.Sources.Torznab.Clients;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Sources.Torznab.Parsing;

namespace Wondarr.Sources.Torznab.Searching;

/// <summary>
/// The grab half of the indexer source (P7-07; DECISIONS build session 8 #5, #6, #9, #10): a container
/// goes to the user's client and only as much of it downloads as the wanted songs need; a finished
/// torrent file is hard-linked into the grab's staging folder (the torrent keeps seeding, its data is
/// never moved or deleted), a finished usenet file is moved there and the job deleted once no queue
/// item needs it any more.
/// </summary>
public sealed partial class IndexerSourceProvider
{
    /// <summary>How long to wait for a just-added container to list its files, between tries.</summary>
    private static readonly TimeSpan FileListWait = TimeSpan.FromMilliseconds(500);

    /// <summary>How many times to look for a just-added container's files.</summary>
    private const int FileListTries = 20;

    /// <inheritdoc />
    public async Task<GrabHandle> GrabAsync(Candidate candidate, string destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (Path.IsPathRooted(destination) || destination.Split('/', '\\').Any(segment => segment is ".." or "."))
        {
            throw new ArgumentException("The destination must be a relative folder without traversal.", nameof(destination));
        }

        var release = candidate.Release
            ?? throw new DownloadClientException("The candidate carries no release to download.");

        var scope = _scopes.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var services = scope.ServiceProvider;
            // A pushed release has no indexer row: its pseudo-indexer only downloads the pushed link.
            var indexer = release.IndexerId == PushedReleaseClient.IndexerId
                ? PushedReleaseClient.Row(release.IndexerName, _protocol)
                : await services.GetRequiredService<IIndexerService>().GetAsync(release.IndexerId, cancellationToken).ConfigureAwait(false)
                    ?? throw new DownloadClientException($"The indexer that listed '{release.Title}' no longer exists.");
            var client = await ChooseClientAsync(services, indexer, cancellationToken).ConfigureAwait(false);

            var staging = Path.Combine(_import.CurrentValue.ContainerStagingPath, destination.Replace('/', Path.DirectorySeparatorChar));
            var filePath = release.FileIndex is { } index ? release.Files?.FirstOrDefault(file => file.Index == index)?.Path : null;
            List<string> wanted = [.. filePath is null ? [] : new[] { filePath }, .. release.AlsoWanted ?? []];

            var grab = _protocol == DownloadProtocol.Torrent
                ? await GrabTorrentAsync(services, indexer, client, release, filePath, wanted, staging, cancellationToken).ConfigureAwait(false)
                : await GrabUsenetAsync(services, indexer, client, release, filePath, wanted, staging, cancellationToken).ConfigureAwait(false);

            return new GrabHandle(SourceType, grab.Serialize());
        }
    }

    /// <inheritdoc />
    public async Task<DownloadStatus> GetStatusAsync(GrabHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (ContainerGrab.TryDeserialize(handle.Value) is not { } grab)
        {
            return Failed("The grab's handle could not be read.");
        }

        var scope = _scopes.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var services = scope.ServiceProvider;
            var client = await services.GetRequiredService<IDownloadClientService>().GetAsync(grab.ClientId, cancellationToken).ConfigureAwait(false);

            if (client is null)
            {
                return Failed("The download client this grab went to was deleted.");
            }

            try
            {
                return _protocol == DownloadProtocol.Torrent
                    ? await TorrentStatusAsync(services, client, grab, cancellationToken).ConfigureAwait(false)
                    : await UsenetStatusAsync(services, client, grab, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The file is finished but cannot be staged: a mapping or a permission problem the
                // user has to fix, not a reason to wait.
                return Failed($"The finished file could not be staged: {exception.Message}");
            }
        }
    }

    /// <inheritdoc />
    public async Task CancelAsync(GrabHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var grab = ContainerGrab.TryDeserialize(handle.Value);

        if (grab is null)
        {
            return;
        }

        var scope = _scopes.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var services = scope.ServiceProvider;
            var client = await services.GetRequiredService<IDownloadClientService>().GetAsync(grab.ClientId, cancellationToken).ConfigureAwait(false);

            if (client is null)
            {
                return;
            }

            var shared = await IsSharedAsync(services, grab, cancellationToken).ConfigureAwait(false);

            if (_protocol == DownloadProtocol.Torrent)
            {
                await CancelTorrentAsync(client, grab, shared, cancellationToken).ConfigureAwait(false);
            }
            else if (!shared && grab.JobId is { } jobId)
            {
                await RemoveJobAsync(client, jobId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The indexer's own client when it names one, else the first enabled client of the protocol.</summary>
    private async Task<DownloadClient> ChooseClientAsync(IServiceProvider services, Indexer indexer, CancellationToken cancellationToken)
    {
        var clients = await services.GetRequiredService<IDownloadClientService>().ListAsync(cancellationToken).ConfigureAwait(false);

        if (indexer.DownloadClientId is { } id)
        {
            return clients.FirstOrDefault(row => row.Id == id && row.Enabled && row.Protocol == _protocol)
                ?? throw new DownloadClientException(
                    $"The download client the indexer '{indexer.Name}' names is disabled, deleted or of the other protocol.");
        }

        var client = clients
            .Where(row => row.Enabled && row.Protocol == _protocol)
            .OrderBy(row => row.Priority)
            .ThenBy(row => row.Id)
            .FirstOrDefault();

        return client ?? throw new DownloadClientException(
            _protocol == DownloadProtocol.Torrent ? "No torrent download client is enabled." : "No usenet download client is enabled.");
    }

    private async Task<ContainerGrab> GrabTorrentAsync(
        IServiceProvider services,
        Indexer indexer,
        DownloadClient client,
        ContainerRelease release,
        string? filePath,
        List<string> wanted,
        string staging,
        CancellationToken cancellationToken)
    {
        var hash = release.InfoHash;

        // Bundle partners already in flight for this release or torrent. Their handles know the hash
        // even when the indexer never gives one (Gazelle), so a partner's grab does not download the
        // .torrent a second time, and they say whether Wondarr added the torrent.
        var partners = await ActiveGrabsAsync(
            services,
            other => other.ClientId == client.Id
                && other.InfoHash is not null
                && ((hash is not null && string.Equals(other.InfoHash, hash, StringComparison.OrdinalIgnoreCase))
                    || (other.IndexerId == indexer.Id && string.Equals(other.ReleaseId, release.ReleaseId, StringComparison.Ordinal))),
            cancellationToken).ConfigureAwait(false);

        hash ??= partners.FirstOrDefault()?.InfoHash;

        // In the client already: a bundle partner's grab, an earlier grab of this release, or the
        // user's own torrent. Only the wanted files are switched on; nothing else of it is touched.
        if (hash is not null && await _torrents.GetAsync(client, hash, cancellationToken).ConfigureAwait(false) is not null)
        {
            var owned = AddedByPartner(partners, hash);

            if (wanted.Count > 0)
            {
                await SelectAsync(client, hash, wanted, exclusive: false, owned, cancellationToken).ConfigureAwait(false);
            }

            return new ContainerGrab(client.Id, indexer.Id, release.ReleaseId, hash, null, filePath, staging, release.Song, owned);
        }

        byte[]? torrentFile = null;
        var magnet = release.MagnetUrl;

        if (release.DownloadUrl is { } url && !url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            var download = await _clients.GetClient(indexer)
                .DownloadAsync(indexer, ToIndexerRelease(release, indexer), cancellationToken)
                .ConfigureAwait(false);

            torrentFile = download.Content;
            magnet = download.MagnetUrl ?? magnet;
        }
        else if (release.DownloadUrl is { } link)
        {
            magnet ??= link;
        }

        bool added;

        if (torrentFile is not null)
        {
            hash = TorrentMetainfo.Parse(torrentFile).InfoHash;
            added = await AddIfMissingAsync(client, new TorrentAddRequest(hash, torrentFile, null), cancellationToken).ConfigureAwait(false);

            // A .torrent is added stopped: every file but the wanted ones goes to priority 0, then it
            // starts. A torrent that was already there (a partner's, or the user's own, whose hash
            // only the download could tell) keeps its other files: they are only ever raised.
            try
            {
                await SelectAsync(client, hash, wanted, exclusive: added, owned: added || AddedByPartner(partners, hash), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (added && exception is DownloadClientException or OperationCanceledException)
            {
                // Nothing has downloaded yet, so the torrent Wondarr has just added goes again.
                await RemoveJustAddedAsync(client, hash, exception).ConfigureAwait(false);
                throw;
            }
        }
        else if (magnet is not null)
        {
            hash = InfoHashOf(magnet)
                ?? throw new DownloadClientException("The magnet link carries no info-hash Wondarr can use.");

            // A magnet runs until its metadata arrives (stopCondition=MetadataReceived); the files are
            // chosen on the first status poll that sees it stopped with metadata.
            added = await AddIfMissingAsync(client, new TorrentAddRequest(hash, null, magnet), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new DownloadClientException($"'{release.Title}' has neither a .torrent nor a magnet link.");
        }

        return new ContainerGrab(client.Id, indexer.Id, release.ReleaseId, hash, null, filePath, staging, release.Song, added || AddedByPartner(partners, hash));
    }

    /// <summary>Whether an in-flight grab of the same torrent in the same client recorded that Wondarr added it.</summary>
    private static bool AddedByPartner(List<ContainerGrab> partners, string hash) =>
        partners.Any(other => other.AddedByWondarr && string.Equals(other.InfoHash, hash, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds the torrent unless the client has it; true when it was added just now.</summary>
    private async Task<bool> AddIfMissingAsync(DownloadClient client, TorrentAddRequest request, CancellationToken cancellationToken)
    {
        if (await _torrents.GetAsync(client, request.InfoHash, cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }

        await _torrents.AddAsync(client, request, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>Removes a torrent this grab has just added and could not set up; it has no data yet.</summary>
    private async Task RemoveJustAddedAsync(DownloadClient client, string hash, Exception cause)
    {
        try
        {
            await _torrents.RemoveAsync(client, hash, deleteFiles: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DownloadClientException exception)
        {
            LogTorrentCleanupFailed(_logger, hash, cause is OperationCanceledException, exception);
        }
    }

    /// <summary>
    /// Switches the wanted files on and starts the torrent. With <paramref name="exclusive"/> (a torrent
    /// Wondarr has just added) every other file is switched off first; otherwise the selection only
    /// raises the wanted files to priority 1 and never lowers another file. A torrent Wondarr did not
    /// add (<paramref name="owned"/> false) is only resumed when it is stopped and a wanted file still
    /// needs data.
    /// </summary>
    private async Task SelectAsync(DownloadClient client, string hash, IReadOnlyList<string> wanted, bool exclusive, bool owned, CancellationToken cancellationToken)
    {
        var files = await WaitForFilesAsync(client, hash, cancellationToken).ConfigureAwait(false);
        var selected = files.Where(file => wanted.Any(path => SamePath(file.Path, path))).ToList();

        if (selected.Count == 0)
        {
            throw new DownloadClientException("None of the wanted files is in the torrent the client added.");
        }

        if (exclusive)
        {
            var others = files.Where(file => file.Priority != 0 && !selected.Contains(file)).Select(file => file.Index).ToList();

            if (others.Count > 0)
            {
                await _torrents.SetFilePriorityAsync(client, hash, others, 0, cancellationToken).ConfigureAwait(false);
            }
        }

        // Only the files that are off are raised: a file the client has at a higher priority stays there.
        var off = selected.Where(file => file.Priority == 0).Select(file => file.Index).ToList();

        if (off.Count > 0)
        {
            await _torrents.SetFilePriorityAsync(client, hash, off, 1, cancellationToken).ConfigureAwait(false);
        }

        if (owned)
        {
            await _torrents.StartAsync(client, hash, cancellationToken).ConfigureAwait(false);
        }
        else if (selected.Any(file => file.Progress < 1))
        {
            await ResumeForeignAsync(client, hash, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Starts a torrent Wondarr did not add, when it is stopped and a wanted file needs data; says so in the log.</summary>
    private async Task ResumeForeignAsync(DownloadClient client, string hash, CancellationToken cancellationToken)
    {
        if (await _torrents.GetAsync(client, hash, cancellationToken).ConfigureAwait(false) is { Status: TorrentStatus.Stopped })
        {
            await _torrents.StartAsync(client, hash, cancellationToken).ConfigureAwait(false);
            LogForeignResumed(_logger, hash);
        }
    }

    /// <summary>The torrent's files, waiting a little for a torrent the client has only just been given.</summary>
    private async Task<IReadOnlyList<TorrentFileInfo>> WaitForFilesAsync(DownloadClient client, string hash, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var files = await _torrents.GetFilesAsync(client, hash, cancellationToken).ConfigureAwait(false);

                if (files.Count > 0 || attempt >= FileListTries)
                {
                    return files;
                }
            }
            catch (TorrentNotFoundException) when (attempt < FileListTries)
            {
                // Added a moment ago; the client has not registered it yet.
            }

            await Task.Delay(FileListWait, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DownloadStatus> TorrentStatusAsync(IServiceProvider services, DownloadClient client, ContainerGrab grab, CancellationToken cancellationToken)
    {
        var hash = grab.InfoHash ?? throw new InvalidOperationException("A torrent grab without an info-hash.");
        var info = await _torrents.GetAsync(client, hash, cancellationToken).ConfigureAwait(false);

        if (info is null)
        {
            return Failed("The torrent is no longer in the download client.");
        }

        if (info.Status == TorrentStatus.Error)
        {
            return Failed(info.Error ?? "The download client reports the torrent as failed.");
        }

        if (!info.HasMetadata)
        {
            return new DownloadStatus(DownloadState.RemotelyQueued, 0, 0, Message: "Fetching the torrent's metadata");
        }

        var files = await _torrents.GetFilesAsync(client, hash, cancellationToken).ConfigureAwait(false);
        var file = grab.FilePath is { } path
            ? files.FirstOrDefault(entry => SamePath(entry.Path, path))
            : await SelectAfterMetadataAsync(client, info, files, grab, cancellationToken).ConfigureAwait(false);

        if (file is null && grab.FilePath is null)
        {
            // The release's files are known now and the song is not among them: this release is not the
            // song, for good. What Wondarr added of it goes (a torrent it did not add is left as it is),
            // and the candidate is blocklisted for the song so no later search grabs it again.
            var shared = await IsSharedAsync(services, grab, cancellationToken).ConfigureAwait(false);

            await CancelTorrentAsync(client, grab, shared, cancellationToken).ConfigureAwait(false);

            return Failed("The song is not in this torrent.", blocklistCandidate: true);
        }

        if (file is null)
        {
            return Failed("The wanted file is no longer in the torrent.");
        }

        if (file.Progress >= 1)
        {
            // While the torrent is incomplete the client may keep its files in the "keep incomplete
            // torrents in" folder, and moves them to the save path when the whole torrent is done (R-017).
            var alreadyStaged = _disk.FileExists(StagedPath(grab, file));

            // Never link or copy a file while the client is moving it.
            if (info.Status == TorrentStatus.Moving && !alreadyStaged)
            {
                return new DownloadStatus(DownloadState.RemotelyQueued, 1, file.Size, file.Size, Message: "The client is moving the finished files");
            }

            var source = FindFinishedFile(info, file);

            if (source is null && info.Status != TorrentStatus.Completed && !alreadyStaged)
            {
                return new DownloadStatus(DownloadState.RemotelyQueued, 1, file.Size, file.Size, Message: "Waiting for the client to finish the torrent");
            }

            var staged = Stage(source ?? Path.Combine(info.SavePath, ToLocal(file.Path)), grab.StagingDir, link: true);

            return new DownloadStatus(DownloadState.Completed, 1, file.Size, file.Size, CompletedPath: staged);
        }

        var bytes = (long)(file.Size * file.Progress);

        return info.Status switch
        {
            TorrentStatus.Downloading or TorrentStatus.Unknown => new DownloadStatus(DownloadState.Downloading, file.Progress, bytes, file.Size),
            TorrentStatus.Stalled => new DownloadStatus(DownloadState.RemotelyQueued, file.Progress, bytes, file.Size, Message: "Waiting for peers"),
            TorrentStatus.Stopped => new DownloadStatus(DownloadState.RemotelyQueued, file.Progress, bytes, file.Size, Message: "Stopped in the download client"),
            TorrentStatus.Checking => new DownloadStatus(DownloadState.RemotelyQueued, file.Progress, bytes, file.Size, Message: "Checking"),
            _ => new DownloadStatus(DownloadState.RemotelyQueued, file.Progress, bytes, file.Size, Message: "Queued in the download client"),
        };
    }

    /// <summary>
    /// Where a 100 % file is on disk: below the incomplete folder (the client's <c>download_path</c>, else
    /// the folder above its <c>content_path</c>) while the torrent is incomplete, then below the save
    /// path, which is where a completed torrent keeps it. Null when it is in neither.
    /// </summary>
    private string? FindFinishedFile(TorrentInfo info, TorrentFileInfo file)
    {
        var relative = ToLocal(file.Path);
        var roots = new List<string>(2);

        if (info.Status != TorrentStatus.Completed)
        {
            if (!string.IsNullOrEmpty(info.DownloadPath))
            {
                roots.Add(info.DownloadPath);
            }
            else if (!string.IsNullOrEmpty(info.ContentPath)
                && Path.GetDirectoryName(info.ContentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } parent)
            {
                roots.Add(parent);
            }
        }

        roots.Add(info.SavePath);

        return roots.Select(root => Path.Combine(root, relative)).FirstOrDefault(_disk.FileExists);
    }

    /// <summary>Where an earlier poll would have staged the file.</summary>
    private static string StagedPath(ContainerGrab grab, TorrentFileInfo file) =>
        Path.Combine(grab.StagingDir, Path.GetFileName(ToLocal(file.Path)));

    /// <summary>
    /// A magnet's file, found by the matcher once the metadata is there. The first poll that sees the
    /// torrent stopped with its metadata (qBittorrent's stop condition) makes the selection — every
    /// other file off — and starts it; later polls only make sure this song's file stays on.
    /// </summary>
    private async Task<TorrentFileInfo?> SelectAfterMetadataAsync(
        DownloadClient client,
        TorrentInfo info,
        IReadOnlyList<TorrentFileInfo> files,
        ContainerGrab grab,
        CancellationToken cancellationToken)
    {
        if (grab.Song is not { } song)
        {
            return null;
        }

        var match = ContainerMatcher.Find([.. files.Select(file => new ContainerFile(file.Index, file.Path, file.Size))], song);
        var file = match is null ? null : files.First(entry => entry.Index == match.File.Index);

        if (file is null)
        {
            return null;
        }

        if (grab.AddedByWondarr && info.Status == TorrentStatus.Stopped && files.All(entry => entry.Priority > 0))
        {
            // Wondarr's own magnet, stopped by the stop condition with every file still wanted.
            await SelectAsync(client, info.InfoHash, [file.Path], exclusive: true, owned: true, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Anything else only ever has this song's file raised (a torrent Wondarr did not add keeps
            // the rest as it was).
            if (file.Priority == 0)
            {
                await _torrents.SetFilePriorityAsync(client, info.InfoHash, [file.Index], 1, cancellationToken).ConfigureAwait(false);
            }

            if (!grab.AddedByWondarr && info.Status == TorrentStatus.Stopped && file.Progress < 1)
            {
                await _torrents.StartAsync(client, info.InfoHash, cancellationToken).ConfigureAwait(false);
                LogForeignResumed(_logger, info.InfoHash);
            }
        }

        return file;
    }

    /// <summary>
    /// A torrent Wondarr did not add is left exactly as it is. One it added is removed — with its data,
    /// which is none — only when no other queue item uses it, every file reports progress 0 and the
    /// client is not checking, moving, fetching metadata or reporting an error (the progress it reports
    /// then is not to be trusted); otherwise only this song's file is switched off (DECISIONS build
    /// session 8 #5, #6; R-016).
    /// </summary>
    private async Task CancelTorrentAsync(DownloadClient client, ContainerGrab grab, bool shared, CancellationToken cancellationToken)
    {
        if (!grab.AddedByWondarr
            || grab.InfoHash is not { } hash
            || await _torrents.GetAsync(client, hash, cancellationToken).ConfigureAwait(false) is not { } info)
        {
            return;
        }

        var files = info.HasMetadata
            ? await _torrents.GetFilesAsync(client, hash, cancellationToken).ConfigureAwait(false)
            : [];

        var mine = grab.FilePath is { } path
            ? files.FirstOrDefault(file => SamePath(file.Path, path))
            : grab.Song is { } song && ContainerMatcher.Find([.. files.Select(file => new ContainerFile(file.Index, file.Path, file.Size))], song) is { } match
                ? files.First(file => file.Index == match.File.Index)
                : null;

        if (mine is { Priority: > 0 })
        {
            await _torrents.SetFilePriorityAsync(client, hash, [mine.Index], 0, cancellationToken).ConfigureAwait(false);
        }

        // A magnet still fetching its metadata has no data and no files to look at.
        if (!shared && info.Status == TorrentStatus.FetchingMetadata)
        {
            await _torrents.RemoveAsync(client, hash, deleteFiles: false, cancellationToken).ConfigureAwait(false);

            return;
        }

        // An allow-list: any other state, a state Wondarr does not know included, is not trusted.
        var removable = !shared
            && info.HasMetadata
            && info.Status is TorrentStatus.Stopped or TorrentStatus.Queued or TorrentStatus.Downloading or TorrentStatus.Stalled
            && info.Progress <= 0
            && files.All(file => file.Progress <= 0);

        if (removable)
        {
            await _torrents.RemoveAsync(client, hash, deleteFiles: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ContainerGrab> GrabUsenetAsync(
        IServiceProvider services,
        Indexer indexer,
        DownloadClient client,
        ContainerRelease release,
        string? filePath,
        List<string> wanted,
        string staging,
        CancellationToken cancellationToken)
    {
        // A bundle partner (or an earlier grab of the same post) already put the job in the client.
        // It is reused only while it still holds this song's file: a job trimmed for another song has
        // lost it (R-078).
        var partners = await ActiveGrabsAsync(
            services,
            other => other.ClientId == client.Id
                && other.JobId is not null
                && string.Equals(other.ReleaseId, release.ReleaseId, StringComparison.Ordinal),
            cancellationToken).ConfigureAwait(false);

        foreach (var existing in partners.Select(other => other.JobId!).Distinct(StringComparer.Ordinal))
        {
            if (await _usenet.GetAsync(client, existing, cancellationToken).ConfigureAwait(false) is { } job
                && await HoldsWantedAsync(client, job, wanted, cancellationToken).ConfigureAwait(false))
            {
                // Only Wondarr's own queue items name a job here, so it is Wondarr's job.
                return new ContainerGrab(client.Id, indexer.Id, release.ReleaseId, null, existing, filePath, staging, release.Song, AddedByWondarr: true);
            }
        }

        // The search read this post's file list from the same NZB: the indexer already counted that
        // download against its daily quota, so a second one is spent only when the cache lost it.
        var nzbKey = NzbCache.KeyOf(release.IndexerId, release.DownloadUrl, release.ReleaseId);
        var downloaded = false;

        if (!_nzbCache.TryGet(nzbKey, out var nzb))
        {
            nzb = await DownloadNzbAsync(indexer, release, cancellationToken).ConfigureAwait(false);
            downloaded = true;
        }

        var jobId = await _usenet.AddAsync(client, new UsenetAddRequest(release.Title, nzb, null, Paused: true), cancellationToken).ConfigureAwait(false);

        if (downloaded)
        {
            // The client took it, so it is a real NZB: kept, so that a grab of the same post for
            // another song (this one retried, or a bundle partner) does not spend another download.
            _nzbCache.Put(nzbKey, nzb);
        }

        try
        {
            // A post with no named file was never meant to be trimmed; the search already held its size to
            // the limit. A named file means the search saw a clean post and priced it as trimmed.
            var trimmed = wanted.Count == 0;

            try
            {
                trimmed = await TrimAsync(client, jobId, wanted, cancellationToken).ConfigureAwait(false);
            }
            catch (DownloadClientException exception)
            {
                // Trimming is opportunistic: the post downloads whole instead, if that is affordable.
                LogTrimFailed(_logger, release.Title, exception);
            }

            if (!trimmed && release.Size is { } size && size > MaxContainerBytes)
            {
                // The job is paused, so nothing has been downloaded; the catch below removes it.
                throw new DownloadClientException(
                    string.Concat(
                        "The post could not be trimmed and is larger than the ",
                        (MaxContainerBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture),
                        " MB limit."));
            }

            await _usenet.ResumeAsync(client, jobId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DownloadClientException or OperationCanceledException)
        {
            // The job Wondarr has just added would sit paused for ever; no other item knows it yet.
            await RemoveJobAsync(client, jobId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new ContainerGrab(client.Id, indexer.Id, release.ReleaseId, null, jobId, filePath, staging, release.Song, AddedByWondarr: true);
    }

    /// <summary>
    /// Downloads a release's NZB for a grab. An indexer whose quota is spent answers 200 with a sentence,
    /// an error document, a 429 or a 403: that fails the grab with the indexer's own words (redacted),
    /// so the download client is never handed something that is not an NZB and the user sees why.
    /// </summary>
    private async Task<byte[]> DownloadNzbAsync(Indexer indexer, ContainerRelease release, CancellationToken cancellationToken)
    {
        IndexerDownload download;

        try
        {
            download = await _clients.GetClient(indexer)
                .DownloadAsync(indexer, ToIndexerRelease(release, indexer), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IndexerException exception) when (exception.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
        {
            throw new DownloadClientException(string.Concat(indexer.Name, ": ", NzbAnswer.Clean(exception.Message, Redact)), exception);
        }

        var nzb = download.Content ?? throw new DownloadClientException($"The indexer returned no NZB for '{release.Title}'.");

        // Any error answer fails the grab, whatever its code: this release is the one wanted.
        if (NzbAnswer.Check(nzb, Redact) is { IsNzb: false } verdict)
        {
            throw new DownloadClientException(string.Concat(indexer.Name, ": ", verdict.Text));
        }

        return nzb;
    }

    /// <summary>
    /// Whether a job in the client still has the wanted files: they are in its queue listing, or the
    /// job is finished and they are in its folder. A job with no named file wanted (an obfuscated post)
    /// was never trimmed. A file a running job has already finished leaves the listing, so that job is
    /// not reused either: a second copy of the post costs a download, a missing file costs the song.
    /// </summary>
    private async Task<bool> HoldsWantedAsync(DownloadClient client, UsenetJobInfo job, List<string> wanted, CancellationToken cancellationToken)
    {
        // A failed job is never reused; a job whose files cannot be listed is treated as not holding them.
        if (job.Status == UsenetStatus.Failed)
        {
            return false;
        }

        var names = wanted.Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (names.Count == 0)
        {
            return true;
        }

        IReadOnlyList<UsenetFileInfo> listed;

        try
        {
            listed = await _usenet.GetFilesAsync(client, job.JobId, cancellationToken).ConfigureAwait(false);
        }
        catch (DownloadClientException)
        {
            return false;
        }

        names.ExceptWith(listed.Select(file => Path.GetFileName(file.FileName)));

        if (names.Count == 0)
        {
            return true;
        }

        if (job.Status == UsenetStatus.Completed && job.StoragePath is { } folder && _disk.DirectoryExists(folder))
        {
            var present = _disk.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

            return names.All(present.Contains);
        }

        return false;
    }

    /// <summary>
    /// Deletes the audio files no wanted song needs, when the post is clean (DECISIONS build session 8 #10).
    /// Tells whether the job now holds only what is wanted: false when the client listed nothing, listed
    /// something that is not a clean post, or listed none of the wanted names, in which case the job is left
    /// whole.
    /// </summary>
    private async Task<bool> TrimAsync(DownloadClient client, string jobId, List<string> wanted, CancellationToken cancellationToken)
    {
        if (wanted.Count == 0)
        {
            return true;
        }

        IReadOnlyList<UsenetFileInfo> files = [];

        // SABnzbd lists a job's files once it has read the NZB, a moment after the add.
        for (var attempt = 1; attempt <= FileListTries / 4 && files.Count == 0; attempt++)
        {
            files = await _usenet.GetFilesAsync(client, jobId, cancellationToken).ConfigureAwait(false);

            if (files.Count == 0)
            {
                await Task.Delay(FileListWait, _time, cancellationToken).ConfigureAwait(false);
            }
        }

        var names = wanted.Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = files.Select((file, index) => NzbFileList.FromFileName(index, file.FileName, file.SizeBytes)).ToList();

        // The same tests the trimmer applies before it deletes anything: a trim that would delete nothing
        // because the list is empty, is not a clean post, or holds none of the wanted names has not trimmed.
        if (entries.Count == 0
            || !NzbFileList.IsClean(entries)
            || !entries.Any(entry => entry.IsAudio && names.Contains(Path.GetFileName(entry.FileName ?? string.Empty))))
        {
            return false;
        }

        foreach (var fileId in NzbTrimmer.FilesToDelete(files, name => names.Contains(Path.GetFileName(name))))
        {
            await _usenet.DeleteFileAsync(client, jobId, fileId, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<DownloadStatus> UsenetStatusAsync(
        IServiceProvider services,
        DownloadClient client,
        ContainerGrab grab,
        CancellationToken cancellationToken)
    {
        var jobId = grab.JobId ?? throw new InvalidOperationException("A usenet grab without a job id.");

        // An earlier poll moved the file already (the job may be gone by now).
        if (Staged(grab.StagingDir) is { } done)
        {
            return new DownloadStatus(DownloadState.Completed, 1, _disk.GetFileSize(done), CompletedPath: done);
        }

        var job = await _usenet.GetAsync(client, jobId, cancellationToken).ConfigureAwait(false);

        if (job is null)
        {
            return Failed("The job is no longer in the download client.");
        }

        switch (job.Status)
        {
            case UsenetStatus.Failed:
                return await FailedJobAsync(services, client, grab, jobId, job.FailMessage ?? "The download client reports the download as failed.", cancellationToken).ConfigureAwait(false);

            case UsenetStatus.Completed:
                break;

            case UsenetStatus.Downloading:
                return new DownloadStatus(DownloadState.Downloading, job.Progress, (long)((job.SizeBytes ?? 0) * job.Progress), job.SizeBytes);

            default:
                return new DownloadStatus(
                    DownloadState.RemotelyQueued,
                    job.Progress,
                    (long)((job.SizeBytes ?? 0) * job.Progress),
                    job.SizeBytes,
                    Message: job.Status switch
                    {
                        UsenetStatus.Paused => "Paused in the download client",
                        UsenetStatus.Verifying => "Verifying",
                        UsenetStatus.Repairing => "Repairing",
                        UsenetStatus.Extracting => "Unpacking",
                        UsenetStatus.Moving => "Moving",
                        _ => "Queued in the download client",
                    });
        }

        if (job.StoragePath is not { } folder || !_disk.DirectoryExists(folder))
        {
            // A path mapping problem the user can fix: the item fails, but the finished download stays.
            return Failed($"The finished folder ({job.StoragePath ?? "none reported"}) is not visible here; check the client's remote path mappings.");
        }

        var files = _disk.EnumerateFiles(folder).ToList();
        var source = grab.FilePath is { } path
            ? files.FirstOrDefault(file => string.Equals(Path.GetFileName(file), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
            : MatchUnpacked(folder, files, grab);

        if (source is null)
        {
            // A post listed by name that lost its file is the client's doing; one with no names, unpacked
            // without the song, is not the song's release, for good.
            return await FailedJobAsync(services, client, grab, jobId, "The song is not in the downloaded post.", cancellationToken, blocklistCandidate: grab.FilePath is null).ConfigureAwait(false);
        }

        var staged = Stage(source, grab.StagingDir, link: false);

        if (!await IsSharedAsync(services, grab, cancellationToken).ConfigureAwait(false))
        {
            await RemoveJobAsync(client, jobId, cancellationToken).ConfigureAwait(false);
        }

        return new DownloadStatus(DownloadState.Completed, 1, _disk.GetFileSize(staged), CompletedPath: staged);
    }

    /// <summary>
    /// A failed item whose job cannot be recovered (the client reports it failed, or the song is not in
    /// the post) leaves nothing behind (R-081): its job goes from the client, history included, once no
    /// other queue item uses it — the same rule as for a finished job. The job is Wondarr's own: only a
    /// handle Wondarr wrote names it. A finished job whose folder Wondarr cannot see is kept.
    /// </summary>
    private async Task<DownloadStatus> FailedJobAsync(
        IServiceProvider services,
        DownloadClient client,
        ContainerGrab grab,
        string jobId,
        string message,
        CancellationToken cancellationToken,
        bool blocklistCandidate = false)
    {
        if (!await IsSharedAsync(services, grab, cancellationToken).ConfigureAwait(false))
        {
            await RemoveJobAsync(client, jobId, cancellationToken).ConfigureAwait(false);
        }

        return Failed(message, blocklistCandidate);
    }

    /// <summary>An unpacked post's file for the song, by the matcher over the files' paths below the job folder.</summary>
    private string? MatchUnpacked(string folder, List<string> files, ContainerGrab grab)
    {
        if (grab.Song is not { } song)
        {
            return null;
        }

        var listed = files
            .Select((file, index) => new ContainerFile(index, Path.GetRelativePath(folder, file).Replace('\\', '/'), _disk.GetFileSize(file)))
            .ToList();

        return ContainerMatcher.Find(listed, song) is { } match ? files[match.File.Index] : null;
    }

    /// <summary>
    /// Puts a finished file into the grab's staging folder: a torrent's is hard-linked (copied when the
    /// link fails, another filesystem) so the torrent keeps seeding; a usenet file is moved.
    /// </summary>
    private string Stage(string source, string stagingDir, bool link)
    {
        var target = Path.Combine(stagingDir, Path.GetFileName(source));

        if (_disk.FileExists(target))
        {
            return target;
        }

        if (!_disk.FileExists(source))
        {
            throw new IOException($"the client reports the file at {source}, which is not there; check the client's remote path mappings");
        }

        _disk.CreateDirectory(stagingDir);

        if (!link)
        {
            _disk.MoveFile(source, target);
        }
        else if (!_disk.TryCreateHardLink(source, target))
        {
            _disk.CopyFile(source, target);
        }

        return target;
    }

    /// <summary>The file an earlier poll staged, if any.</summary>
    private string? Staged(string stagingDir) =>
        _disk.DirectoryExists(stagingDir) ? _disk.EnumerateFiles(stagingDir).FirstOrDefault() : null;

    private async Task RemoveJobAsync(DownloadClient client, string jobId, CancellationToken cancellationToken)
    {
        try
        {
            await _usenet.RemoveAsync(client, jobId, deleteFiles: true, cancellationToken).ConfigureAwait(false);
        }
        catch (DownloadClientException exception)
        {
            LogJobRemoveFailed(_logger, jobId, exception);
        }
    }

    /// <summary>Whether another queue item still in flight uses the same torrent or job.</summary>
    private async Task<bool> IsSharedAsync(IServiceProvider services, ContainerGrab grab, CancellationToken cancellationToken)
    {
        var key = grab.InfoHash ?? grab.JobId;

        if (key is null)
        {
            return false;
        }

        var others = await ActiveGrabsAsync(
            services,
            other => other.ClientId == grab.ClientId
                && !string.Equals(other.StagingDir, grab.StagingDir, StringComparison.Ordinal)
                && (grab.InfoHash is not null ? other.InfoHash == grab.InfoHash : other.JobId == grab.JobId),
            cancellationToken).ConfigureAwait(false);

        return others.Count > 0;
    }

    /// <summary>
    /// This source's grabs still in flight that satisfy <paramref name="match"/>. The handles are read
    /// back and compared on their values: a guid with <c>&amp;</c> or <c>+</c> is escaped in the
    /// serialised JSON, so a substring search over it would never find it (R-079). The queue holds few
    /// items in flight, so the filter runs in memory.
    /// </summary>
    private async Task<List<ContainerGrab>> ActiveGrabsAsync(IServiceProvider services, Func<ContainerGrab, bool> match, CancellationToken cancellationToken)
    {
        var database = services.GetRequiredService<WondarrDbContext>();
        var sourceType = SourceType;

        var handles = await database.QueueItems
            .AsNoTracking()
            .Where(item => item.SourceType == sourceType
                && (item.State == QueueItemState.Queued
                    || item.State == QueueItemState.RemotelyQueued
                    || item.State == QueueItemState.Downloading)
                && item.Handle != null)
            .Select(item => item.Handle)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. handles.Select(ContainerGrab.TryDeserialize).OfType<ContainerGrab>().Where(match)];
    }

    /// <summary>
    /// The release as the indexer client wants it back for a grab; only the links and the names matter
    /// there. A Gazelle link gets its freeleech token here and nowhere else (R-006): the stored link
    /// and the search-time download never carry it.
    /// </summary>
    private IndexerRelease ToIndexerRelease(ContainerRelease release, Indexer indexer) =>
        new(
            release.Title,
            release.ReleaseId,
            release.DownloadUrl is { } url && string.Equals(indexer.Type, "gazelle", StringComparison.Ordinal) ? GazelleClient.ForGrab(indexer, url) : release.DownloadUrl,
            release.MagnetUrl,
            release.InfoHash,
            release.Size,
            release.PublishDate,
            null,
            null,
            null,
            [],
            null,
            null,
            _protocol,
            indexer.Id,
            indexer.Name,
            null);

    /// <summary>Two paths inside a container name the same file: equal, or one ends with the other on a segment boundary.</summary>
    private static bool SamePath(string clientPath, string wanted)
    {
        var a = clientPath.Replace('\\', '/').Trim('/');
        var b = wanted.Replace('\\', '/').Trim('/');

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || a.EndsWith("/" + b, StringComparison.OrdinalIgnoreCase)
            || b.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToLocal(string containerPath) => containerPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static DownloadStatus Failed(string message, bool blocklistCandidate = false) =>
        new(DownloadState.Failed, 0, 0, Message: message, BlocklistCandidate: blocklistCandidate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trimming the usenet post {Title} failed; it downloads whole")]
    private static partial void LogTrimFailed(ILogger logger, string title, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resumed the stopped torrent {InfoHash}, which Wondarr did not add, because a wanted file still needs data")]
    private static partial void LogForeignResumed(ILogger logger, string infoHash);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The torrent {InfoHash} Wondarr had just added could not be removed after its setup failed (cancelled: {Cancelled})")]
    private static partial void LogTorrentCleanupFailed(ILogger logger, string infoHash, bool cancelled, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The usenet job {JobId} could not be removed from the client")]
    private static partial void LogJobRemoveFailed(ILogger logger, string jobId, Exception exception);
}
