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

        if (destination.Split('/', '\\').Any(segment => segment is ".." or "."))
        {
            throw new ArgumentException("The destination must be a relative folder without traversal.", nameof(destination));
        }

        var release = candidate.Release
            ?? throw new DownloadClientException("The candidate carries no release to download.");

        var scope = _scopes.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var services = scope.ServiceProvider;
            var indexer = await services.GetRequiredService<IIndexerService>().GetAsync(release.IndexerId, cancellationToken).ConfigureAwait(false)
                ?? throw new DownloadClientException($"The indexer that listed '{release.Title}' no longer exists.");
            var client = await ChooseClientAsync(services, indexer, cancellationToken).ConfigureAwait(false);

            var staging = Path.Combine(_import.CurrentValue.ContainerStagingPath, destination.Replace('/', Path.DirectorySeparatorChar));
            var filePath = release.FileIndex is { } index ? release.Files?.FirstOrDefault(file => file.Index == index)?.Path : null;
            List<string> wanted = [.. filePath is null ? [] : new[] { filePath }, .. release.AlsoWanted ?? []];

            var grab = _protocol == DownloadProtocol.Torrent
                ? await GrabTorrentAsync(indexer, client, release, filePath, wanted, staging, cancellationToken).ConfigureAwait(false)
                : await GrabUsenetAsync(services, indexer, client, release, filePath, wanted, staging, cancellationToken).ConfigureAwait(false);

            return new GrabHandle(SourceType, grab.Serialize());
        }
    }

    /// <inheritdoc />
    public async Task<DownloadStatus> GetStatusAsync(GrabHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var grab = ContainerGrab.Deserialize(handle.Value);
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
                    ? await TorrentStatusAsync(client, grab, cancellationToken).ConfigureAwait(false)
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

        var client = indexer.DownloadClientId is { } id
            ? clients.FirstOrDefault(row => row.Id == id && row.Enabled)
            : clients
                .Where(row => row.Enabled && row.Protocol == _protocol)
                .OrderBy(row => row.Priority)
                .ThenBy(row => row.Id)
                .FirstOrDefault();

        return client ?? throw new DownloadClientException(
            _protocol == DownloadProtocol.Torrent ? "No torrent download client is enabled." : "No usenet download client is enabled.");
    }

    private async Task<ContainerGrab> GrabTorrentAsync(
        Indexer indexer,
        DownloadClient client,
        ContainerRelease release,
        string? filePath,
        List<string> wanted,
        string staging,
        CancellationToken cancellationToken)
    {
        var hash = release.InfoHash;

        // In the client already: a bundle partner's grab, or an earlier grab of this release. Only
        // the wanted files are switched on; nothing else of the torrent is touched.
        if (hash is not null && await _torrents.GetAsync(client, hash, cancellationToken).ConfigureAwait(false) is not null)
        {
            if (wanted.Count > 0)
            {
                await SelectAsync(client, hash, wanted, exclusive: false, cancellationToken).ConfigureAwait(false);
            }

            return new ContainerGrab(client.Id, indexer.Id, release.ReleaseId, hash, null, filePath, staging, release.Song);
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

        if (torrentFile is not null)
        {
            hash = TorrentMetainfo.Parse(torrentFile).InfoHash;

            if (await _torrents.GetAsync(client, hash, cancellationToken).ConfigureAwait(false) is null)
            {
                await _torrents.AddAsync(client, new TorrentAddRequest(hash, torrentFile, null), cancellationToken).ConfigureAwait(false);
            }

            // A .torrent is added stopped: every file but the wanted ones goes to priority 0, then it starts.
            await SelectAsync(client, hash, wanted, exclusive: true, cancellationToken).ConfigureAwait(false);
        }
        else if (magnet is not null)
        {
            hash = InfoHashOf(magnet)
                ?? throw new DownloadClientException("The magnet link carries no info-hash Wondarr can use.");

            // A magnet runs until its metadata arrives (stopCondition=MetadataReceived); the files are
            // chosen on the first status poll that sees it stopped with metadata.
            if (await _torrents.GetAsync(client, hash, cancellationToken).ConfigureAwait(false) is null)
            {
                await _torrents.AddAsync(client, new TorrentAddRequest(hash, null, magnet), cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            throw new DownloadClientException($"'{release.Title}' has neither a .torrent nor a magnet link.");
        }

        return new ContainerGrab(client.Id, indexer.Id, release.ReleaseId, hash, null, filePath, staging, release.Song);
    }

    /// <summary>
    /// Switches the wanted files on and starts the torrent; with <paramref name="exclusive"/>, every
    /// other file is switched off first (a torrent Wondarr has just added).
    /// </summary>
    private async Task SelectAsync(DownloadClient client, string hash, IReadOnlyList<string> wanted, bool exclusive, CancellationToken cancellationToken)
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

        var off = selected.Where(file => file.Priority == 0).Select(file => file.Index).ToList();

        if (off.Count > 0 || exclusive)
        {
            await _torrents.SetFilePriorityAsync(client, hash, [.. selected.Select(file => file.Index)], 1, cancellationToken).ConfigureAwait(false);
        }

        await _torrents.StartAsync(client, hash, cancellationToken).ConfigureAwait(false);
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

    private async Task<DownloadStatus> TorrentStatusAsync(DownloadClient client, ContainerGrab grab, CancellationToken cancellationToken)
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

        if (file is null)
        {
            return Failed(grab.FilePath is null
                ? "The song is not in this torrent."
                : "The wanted file is no longer in the torrent.");
        }

        if (file.Progress >= 1)
        {
            var staged = Stage(Path.Combine(info.SavePath, ToLocal(file.Path)), grab.StagingDir, link: true);

            return new DownloadStatus(DownloadState.Completed, 1, file.Size, file.Size, CompletedPath: staged);
        }

        var bytes = (long)(file.Size * file.Progress);

        return info.Status switch
        {
            TorrentStatus.Downloading => new DownloadStatus(DownloadState.Downloading, file.Progress, bytes, file.Size),
            TorrentStatus.Stalled => new DownloadStatus(DownloadState.RemotelyQueued, file.Progress, bytes, file.Size, Message: "Waiting for peers"),
            TorrentStatus.Stopped => new DownloadStatus(DownloadState.RemotelyQueued, file.Progress, bytes, file.Size, Message: "Stopped in the download client"),
            TorrentStatus.Checking => new DownloadStatus(DownloadState.RemotelyQueued, file.Progress, bytes, file.Size, Message: "Checking"),
            _ => new DownloadStatus(DownloadState.RemotelyQueued, file.Progress, bytes, file.Size, Message: "Queued in the download client"),
        };
    }

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

        if (info.Status == TorrentStatus.Stopped && files.All(entry => entry.Priority > 0))
        {
            await SelectAsync(client, info.InfoHash, [file.Path], exclusive: true, cancellationToken).ConfigureAwait(false);
        }
        else if (file.Priority == 0)
        {
            await _torrents.SetFilePriorityAsync(client, info.InfoHash, [file.Index], 1, cancellationToken).ConfigureAwait(false);
        }

        return file;
    }

    /// <summary>
    /// Switches this song's file off, and removes the torrent — with its data — only when no other
    /// queue item uses it and nothing of it has downloaded (DECISIONS build session 8 #5, #6).
    /// </summary>
    private async Task CancelTorrentAsync(DownloadClient client, ContainerGrab grab, bool shared, CancellationToken cancellationToken)
    {
        if (grab.InfoHash is not { } hash || await _torrents.GetAsync(client, hash, cancellationToken).ConfigureAwait(false) is not { } info)
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

        if (!shared && info.Progress <= 0 && files.All(file => file.Progress <= 0))
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
        var existing = await JobOfReleaseAsync(services, release.ReleaseId, client.Id, cancellationToken).ConfigureAwait(false);

        if (existing is not null && await _usenet.GetAsync(client, existing, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new ContainerGrab(client.Id, indexer.Id, release.ReleaseId, null, existing, filePath, staging, release.Song);
        }

        var download = await _clients.GetClient(indexer)
            .DownloadAsync(indexer, ToIndexerRelease(release, indexer), cancellationToken)
            .ConfigureAwait(false);

        var nzb = download.Content ?? throw new DownloadClientException($"The indexer returned no NZB for '{release.Title}'.");
        var jobId = await _usenet.AddAsync(client, new UsenetAddRequest(release.Title, nzb, null, Paused: true), cancellationToken).ConfigureAwait(false);

        try
        {
            await TrimAsync(client, jobId, wanted, cancellationToken).ConfigureAwait(false);
        }
        catch (DownloadClientException exception)
        {
            // Trimming is opportunistic: the post downloads whole instead.
            LogTrimFailed(_logger, release.Title, exception);
        }

        await _usenet.ResumeAsync(client, jobId, cancellationToken).ConfigureAwait(false);

        return new ContainerGrab(client.Id, indexer.Id, release.ReleaseId, null, jobId, filePath, staging, release.Song);
    }

    /// <summary>Deletes the audio files no wanted song needs, when the post is clean (DECISIONS build session 8 #10).</summary>
    private async Task TrimAsync(DownloadClient client, string jobId, List<string> wanted, CancellationToken cancellationToken)
    {
        if (wanted.Count == 0)
        {
            return;
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

        foreach (var fileId in NzbTrimmer.FilesToDelete(files, name => names.Contains(Path.GetFileName(name))))
        {
            await _usenet.DeleteFileAsync(client, jobId, fileId, cancellationToken).ConfigureAwait(false);
        }
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
                return Failed(job.FailMessage ?? "The download client reports the download as failed.");

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
            return Failed($"The finished folder ({job.StoragePath ?? "none reported"}) is not visible here; check the client's remote path mappings.");
        }

        var files = _disk.EnumerateFiles(folder).ToList();
        var source = grab.FilePath is { } path
            ? files.FirstOrDefault(file => string.Equals(Path.GetFileName(file), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
            : MatchUnpacked(folder, files, grab);

        if (source is null)
        {
            return Failed("The song is not in the downloaded post.");
        }

        var staged = Stage(source, grab.StagingDir, link: false);

        if (!await IsSharedAsync(services, grab, cancellationToken).ConfigureAwait(false))
        {
            await RemoveJobAsync(client, jobId, cancellationToken).ConfigureAwait(false);
        }

        return new DownloadStatus(DownloadState.Completed, 1, _disk.GetFileSize(staged), CompletedPath: staged);
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

        foreach (var other in await ActiveGrabsAsync(services, key, cancellationToken).ConfigureAwait(false))
        {
            if (other.ClientId == grab.ClientId
                && !string.Equals(other.StagingDir, grab.StagingDir, StringComparison.Ordinal)
                && (grab.InfoHash is not null ? other.InfoHash == grab.InfoHash : other.JobId == grab.JobId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The job an in-flight grab of the same post has in the same client, if any.</summary>
    private async Task<string?> JobOfReleaseAsync(IServiceProvider services, string releaseId, long clientId, CancellationToken cancellationToken)
    {
        foreach (var other in await ActiveGrabsAsync(services, releaseId, cancellationToken).ConfigureAwait(false))
        {
            if (other.ClientId == clientId && other.ReleaseId == releaseId && other.JobId is { } jobId)
            {
                return jobId;
            }
        }

        return null;
    }

    /// <summary>This source's grabs still in flight whose handle mentions <paramref name="key"/>.</summary>
    private async Task<List<ContainerGrab>> ActiveGrabsAsync(IServiceProvider services, string key, CancellationToken cancellationToken)
    {
        var database = services.GetRequiredService<WondarrDbContext>();
        var sourceType = SourceType;

        var handles = await database.QueueItems
            .AsNoTracking()
            .Where(item => item.SourceType == sourceType
                && (item.State == QueueItemState.Queued
                    || item.State == QueueItemState.RemotelyQueued
                    || item.State == QueueItemState.Downloading)
                && item.Handle != null
                && item.Handle.Contains(key))
            .Select(item => item.Handle)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. handles.Select(ContainerGrab.TryDeserialize).OfType<ContainerGrab>()];
    }

    /// <summary>
    /// The release as the indexer client wants it back for a download; only the links and the names
    /// matter there.
    /// </summary>
    private IndexerRelease ToIndexerRelease(ContainerRelease release, Indexer indexer) =>
        new(
            release.Title,
            release.ReleaseId,
            release.DownloadUrl,
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

    private static DownloadStatus Failed(string message) => new(DownloadState.Failed, 0, 0, Message: message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trimming the usenet post {Title} failed; it downloads whole")]
    private static partial void LogTrimFailed(ILogger logger, string title, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The usenet job {JobId} could not be removed from the client")]
    private static partial void LogJobRemoveFailed(ILogger logger, string jobId, Exception exception);
}
