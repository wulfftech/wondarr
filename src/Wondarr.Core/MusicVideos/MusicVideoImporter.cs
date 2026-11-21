using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.MusicVideos;

/// <summary>
/// A music video download failed and the row says why. The video file is gone; for a check that the video failed,
/// the video id is blocklisted for the song.
/// </summary>
public sealed class MusicVideoImportException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="MusicVideoImportException"/> class.</summary>
    /// <param name="message">What went wrong.</param>
    public MusicVideoImportException(string message)
        : base(message)
    {
    }
}

/// <summary>Downloads, checks and files one music video.</summary>
public interface IMusicVideoImporter
{
    /// <summary>
    /// Downloads the video, probes it, files it by the configured layout and fills in the song's row. A row that is
    /// gone by the time the download is done is not written back: the temporary files are dropped.
    /// </summary>
    /// <param name="songId">The song.</param>
    /// <param name="videoId">The YouTube video id.</param>
    /// <param name="foundBy">How the video was found.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <param name="videoTitle">The video's title as the source showed it, kept on the row; <see langword="null"/> when not known.</param>
    /// <returns>What was done, for the command's message.</returns>
    /// <exception cref="MusicVideoImportException">The download or the check failed; the row says why.</exception>
    Task<string> ImportAsync(long songId, string videoId, MusicVideoFoundBy foundBy, CancellationToken cancellationToken, string? videoTitle = null);
}

/// <inheritdoc />
public sealed partial class MusicVideoImporter : IMusicVideoImporter
{
    /// <summary>How far the probed length may be from the length the download reported, in milliseconds (ADR-0014).</summary>
    public const int LengthToleranceMs = 15_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What the new video is called beside its destination while the old one is recycled; no scan reads it.</summary>
    internal const string StagingSuffix = ".wondarr-tmp";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly WondarrDbContext _database;
    private readonly IOptionsMonitor<MusicVideoOptions> _options;
    private readonly IMusicVideoPathResolver _resolver;
    private readonly IMusicVideoDownloader _downloader;
    private readonly IMediaProbe _probe;
    private readonly IMusicVideoService _videos;
    private readonly IDiskOperations _disk;
    private readonly IOptionsMonitor<ImportOptions> _importOptions;
    private readonly ILogger<MusicVideoImporter> _logger;
    private readonly TimeProvider _time;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoImporter"/> class.</summary>
    /// <param name="database">The database.</param>
    /// <param name="options">The <c>music_videos</c> settings.</param>
    /// <param name="resolver">Decides where the video goes.</param>
    /// <param name="downloader">Downloads the video.</param>
    /// <param name="probe">Measures the downloaded file.</param>
    /// <param name="videos">Recycles the video a new one replaces.</param>
    /// <param name="disk">Places the files.</param>
    /// <param name="importOptions">The import settings, for the file and folder modes.</param>
    /// <param name="logger">Logs each step.</param>
    /// <param name="time">The clock the row's <c>updated_at</c> and a failed replacement's time are read from; the system clock when absent.</param>
    public MusicVideoImporter(
        WondarrDbContext database,
        IOptionsMonitor<MusicVideoOptions> options,
        IMusicVideoPathResolver resolver,
        IMusicVideoDownloader downloader,
        IMediaProbe probe,
        IMusicVideoService videos,
        IDiskOperations disk,
        IOptionsMonitor<ImportOptions> importOptions,
        ILogger<MusicVideoImporter> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(videos);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(importOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _options = options;
        _resolver = resolver;
        _downloader = downloader;
        _probe = probe;
        _videos = videos;
        _disk = disk;
        _importOptions = importOptions;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<string> ImportAsync(long songId, string videoId, MusicVideoFoundBy foundBy, CancellationToken cancellationToken, string? videoTitle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);

        try
        {
            return await ImportCoreAsync(songId, videoId, foundBy, videoTitle, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not MusicVideoImportException)
        {
            // A cancelled or crashed import must not leave a row Queued or Downloading for ever. A row that has
            // moved on (Present, Failed, removed) is not touched.
            var reason = exception is OperationCanceledException
                ? "The download was interrupted before it finished."
                : $"The download stopped unexpectedly: {exception.Message}";

            var now = _time.GetUtcNow().UtcDateTime;

            await _database.MusicVideos
                .Where(video => video.SongId == songId
                    && video.YouTubeVideoId == videoId
                    && (video.Status == MusicVideoStatus.Queued || video.Status == MusicVideoStatus.Downloading))
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(video => video.Status, MusicVideoStatus.Failed)
                        .SetProperty(video => video.FailureReason, reason)
                        .SetProperty(video => video.UpdatedAt, now),
                    CancellationToken.None)
                .ConfigureAwait(false);

            // A replacement that was cut short leaves the filed video as it is, and says so.
            await RecordReplacementFailureAsync(songId, videoId, reason, onlyIfDifferentVideo: false).ConfigureAwait(false);

            throw;
        }
    }

    private async Task<string> ImportCoreAsync(long songId, string videoId, MusicVideoFoundBy foundBy, string? videoTitle, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var prefix = $"Song {songId.ToString(CultureInfo.InvariantCulture)}: ";

        if (!options.Enabled)
        {
            return prefix + "music videos are turned off; nothing was downloaded.";
        }

        var wanted = await ReadRowAsync(songId, videoId, cancellationToken).ConfigureAwait(false);

        if (wanted.Message is not null)
        {
            return prefix + wanted.Message;
        }

        // A row that holds a filed video stays as it is until the replacement has been checked and filed.
        var fresh = !wanted.Replacing;
        var foundPath = FoundPathOf(wanted);

        // A video found in the folder is the user's own file. A person's pick may replace it, but only by moving it to
        // the recycle bin, and without one nothing is downloaded (ADR-0014, amended 2026-11-21).
        if (foundPath is not null && await _videos.ReplaceRefusalAsync(wanted.Row!, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            await FailAsync(songId, videoId, fresh, refusal, blocklist: false).ConfigureAwait(false);

            throw new MusicVideoImportException(refusal);
        }

        var early = await _resolver.ResolveAsync(songId, cancellationToken).ConfigureAwait(false);

        if (!early.CanWrite)
        {
            await FailAsync(songId, videoId, fresh, early.DeferredReason!, blocklist: false).ConfigureAwait(false);

            return prefix + "deferred, " + early.DeferredReason;
        }

        // The folder already holds a video by this name that a scan found: it is the user's, and is never filed over,
        // unless it is the very file this download replaces.
        if (await HeldByFoundFileAsync(early.VideoPath!, foundPath, cancellationToken).ConfigureAwait(false) is { } held)
        {
            await FailAsync(songId, videoId, fresh, held, blocklist: false).ConfigureAwait(false);

            throw new MusicVideoImportException(held);
        }

        MusicVideoDownload download;

        try
        {
            // The row stays Queued while the download waits for the shared yt-dlp gate, and is Downloading only
            // once the gate is held and the download really starts.
            download = await _downloader
                .DownloadAsync(
                    videoId,
                    options.MaxHeight,
                    cancellationToken,
                    fresh ? () => MarkDownloadingAsync(songId, cancellationToken) : null)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var reason = $"The download failed: {exception.Message}";
            await FailAsync(songId, videoId, fresh, reason, blocklist: false).ConfigureAwait(false);

            throw new MusicVideoImportException(reason);
        }

        try
        {
            return await CheckAndFileAsync(songId, videoId, foundBy, videoTitle, fresh, download, options, prefix, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _downloader.Discard(download);
        }
    }

    private async Task MarkDownloadingAsync(long songId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        await _database.MusicVideos
            .Where(video => video.SongId == songId && video.Status == MusicVideoStatus.Queued)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(video => video.Status, MusicVideoStatus.Downloading)
                    .SetProperty(video => video.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The path of the found file a download replaces, or <see langword="null"/> when it replaces none.</summary>
    private static string? FoundPathOf(WantedRow wanted) =>
        wanted.Replacing && wanted.Row is { FoundBy: MusicVideoFoundBy.Existing, Path: { Length: > 0 } path } ? path : null;

    private async Task<string> CheckAndFileAsync(
        long songId,
        string videoId,
        MusicVideoFoundBy foundBy,
        string? videoTitle,
        bool fresh,
        MusicVideoDownload download,
        MusicVideoOptions options,
        string prefix,
        CancellationToken cancellationToken)
    {
        // The row is read again: the song or its video may have been removed while the video downloaded.
        var wanted = await ReadRowAsync(songId, videoId, cancellationToken).ConfigureAwait(false);

        if (wanted.Message is not null)
        {
            return prefix + wanted.Message + " The downloaded files were dropped.";
        }

        var probe = await _probe.ProbeVideoAsync(download.VideoPath, cancellationToken).ConfigureAwait(false);

        if (!probe.Readable || probe.Info is not { } info)
        {
            var reason = $"The downloaded file could not be read: {probe.Error ?? "no reason given"}";

            // A tool that ran out of time says nothing about the video, so the video stays allowed.
            await FailAsync(songId, videoId, fresh, reason, blocklist: !probe.TimedOut).ConfigureAwait(false);

            throw new MusicVideoImportException(reason);
        }

        if (VerificationFailure(info, download.ReportedDurationMs) is { } failure)
        {
            await FailAsync(songId, videoId, fresh, failure, blocklist: true).ConfigureAwait(false);

            throw new MusicVideoImportException(failure);
        }

        var placement = await _resolver.ResolveAsync(songId, cancellationToken).ConfigureAwait(false);

        if (!placement.CanWrite)
        {
            await FailAsync(songId, videoId, fresh, placement.DeferredReason!, blocklist: false).ConfigureAwait(false);

            return prefix + "deferred, " + placement.DeferredReason + " The downloaded files were dropped.";
        }

        var destination = placement.VideoPath!;

        // The download took a while: a scan may have found a video of that name since.
        if (await HeldByFoundFileAsync(destination, FoundPathOf(wanted), cancellationToken).ConfigureAwait(false) is { } held)
        {
            await FailAsync(songId, videoId, fresh, held, blocklist: false).ConfigureAwait(false);

            throw new MusicVideoImportException(held);
        }

        MusicVideoFiled filed;

        try
        {
            filed = await FileAsync(songId, destination, download, options, wanted, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or MusicVideoException
            or MusicVideoInReferenceLibraryException
            or MusicVideoPathException
            or MusicVideoFileException
            or MusicVideoTimeoutException)
        {
            var reason = $"The video could not be filed: {exception.Message}";
            await FailAsync(songId, videoId, fresh, reason, blocklist: false).ConfigureAwait(false);

            throw new MusicVideoImportException(reason);
        }

        // From here the file is in place: the row and the history are written whatever the token says.
        var row = await _database.MusicVideos
            .FirstOrDefaultAsync(video => video.SongId == songId, CancellationToken.None)
            .ConfigureAwait(false);

        if (row is null)
        {
            // The row was removed after the check above; the filed video is the user's to remove, and is named.
            LogRowGone(_logger, songId, destination);

            return prefix + "the video was removed while it was being filed; the file is at " + destination + ".";
        }

        // The title belongs to the video: a different video drops the old title, the same one keeps it.
        row.VideoTitle = videoTitle ?? (string.Equals(row.YouTubeVideoId, videoId, StringComparison.Ordinal) ? row.VideoTitle : null);
        row.Status = MusicVideoStatus.Present;
        row.YouTubeVideoId = videoId;
        row.FoundBy = foundBy;
        row.Path = destination;
        row.Width = info.Width;
        row.Height = info.Height;
        row.VideoCodec = info.VideoCodec;
        row.AudioCodec = info.AudioCodec;
        row.SizeBytes = info.SizeBytes > 0 ? info.SizeBytes : null;
        row.DurationMs = info.DurationMs > 0 ? info.DurationMs : null;
        row.FailureReason = null;
        row.ReplaceFailureReason = null;
        row.ReplaceFailedAt = null;

        // A found file that this download replaced is not the song's any more. It is in the recycle bin, so its row is
        // deleted; one that could not be recycled stays where it lay, for the person to
        // match again. A row a scan may have made for the file just filed has no business being there.
        if (FoundPathOf(wanted) is { } oldPath && !PathRules.AreEqual(oldPath, destination))
        {
            var oldFile = await _database.MusicVideoFiles
                .FirstOrDefaultAsync(file => file.Path == oldPath, CancellationToken.None)
                .ConfigureAwait(false);

            if (oldFile is not null && filed.OldRecycled)
            {
                // The file is in the recycle bin: its row goes, so a copy the person restores is seen fresh by the next scan.
                _database.MusicVideoFiles.Remove(oldFile);
            }
            else if (oldFile is not null)
            {
                oldFile.SongId = null;
                oldFile.State = MusicVideoFileState.Ambiguous;
                oldFile.Candidates = MusicVideoFileRules.Write([new MusicVideoFileCandidate(songId, 1, MusicVideoFileRules.SongHasVideo)]);
            }
        }

        var racing = await _database.MusicVideoFiles
            .Where(file => EF.Functions.Collate(file.Path, "NOCASE") == destination)
            .ToListAsync(CancellationToken.None)
            .ConfigureAwait(false);
        _database.MusicVideoFiles.RemoveRange(racing);

        _database.History.Add(new HistoryItem
        {
            SongId = songId,
            EventType = HistoryEventType.MusicVideoImported,
            Data = JsonSerializer.Serialize(
                new ImportedHistoryData(videoId, foundBy.ToString(), destination, filed.RecycledPaths),
                Json),
        });

        await _database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        LogFiled(_logger, videoId, songId, destination);

        return prefix + "filed " + destination + ".";
    }

    /// <summary>
    /// What is wrong with the probed video, or <see langword="null"/> when it is a video: one video stream, one audio
    /// stream, and a length within <see cref="LengthToleranceMs"/> of the length the download reported.
    /// </summary>
    private static string? VerificationFailure(VideoInfo info, int? reportedMs)
    {
        if (info.VideoStreams < 1)
        {
            return "The downloaded file has no video stream.";
        }

        if (info.AudioStreams < 1)
        {
            return "The downloaded file has no audio stream.";
        }

        // Without a reported length there is nothing to hold the measured one against.
        if (reportedMs is { } reported && Math.Abs(info.DurationMs - reported) > LengthToleranceMs)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"The downloaded file is {info.DurationMs / 1000.0:0.#} s long but YouTube says {reported / 1000.0:0.#} s.");
        }

        return null;
    }

    /// <summary>
    /// Moves the video and its sidecars into place, and recycles the video it replaces. A video replaced where it
    /// lies has its old files recycled first; if putting the new one in place then fails, they are moved back.
    /// </summary>
    private async Task<MusicVideoFiled> FileAsync(
        long songId,
        string destination,
        MusicVideoDownload download,
        MusicVideoOptions options,
        WantedRow wanted,
        CancellationToken cancellationToken)
    {
        var recycled = new List<string>();
        var old = wanted.Replacing ? wanted.Row : null;

        // A person's pick replaces the old video, a found one included: it goes to the recycle bin first when the new
        // one takes its name, and after the new one is in place otherwise.
        var samePlace = old?.Path is { Length: > 0 } oldPath && PathRules.AreEqual(oldPath, destination);
        var oldRecycled = samePlace;

        var createdDirectories = MissingDirectories(Path.GetDirectoryName(destination)!);

        _disk.CreateDirectory(Path.GetDirectoryName(destination)!);

        // A video that is replaced where it lies has to leave first: nothing is ever written over a file. The new video
        // is staged beside its destination first (the slow copy may cross devices), then the old files are recycled and the
        // log says where they went, then the staged file is renamed into place: a kill during the copy loses nothing.
        IReadOnlyList<(string Original, string Recycled)> leftFirst = [];
        var staged = destination + StagingSuffix;

        try
        {
            // A leftover of a run that was killed after staging: it was never the video.
            if (_disk.FileExists(staged))
            {
                _disk.DeleteFile(staged);
            }

            if (old is not null && samePlace)
            {
                Place(download.VideoPath, staged);

                leftFirst = await _videos.RecycleFilesDetailedAsync(old, cancellationToken).ConfigureAwait(false);
                recycled.AddRange(leftFirst.Select(entry => entry.Recycled));
                var recycledText = string.Join("; ", recycled);
                LogRecycledBeforeSwap(_logger, songId, old.Path ?? destination, recycledText);

                if (_disk.FileExists(destination))
                {
                    throw new MusicVideoException($"A file is already at {destination}.");
                }

                _disk.MoveFile(staged, destination);
            }
            else
            {
                if (_disk.FileExists(destination))
                {
                    throw new MusicVideoException($"A file is already at {destination}.");
                }

                Place(download.VideoPath, destination);
            }
        }
        catch
        {
            PutBack(songId, leftFirst);
            DeleteQuietly(staged);

            throw;
        }

        if (old is not null && !samePlace && !string.IsNullOrWhiteSpace(old.Path))
        {
            try
            {
                recycled.AddRange(await _videos.RecycleFilesAsync(old, cancellationToken).ConfigureAwait(false));
                oldRecycled = true;
            }
            catch (Exception exception) when (exception is MusicVideoInReferenceLibraryException
                or MusicVideoPathException
                or MusicVideoFileException
                or MusicVideoTimeoutException)
            {
                // The new video is in place; the old one stays where it was, and the log says so.
                LogOldVideoLeft(_logger, songId, old.Path, exception);
            }
        }

        var stem = destination[..^MusicVideoPathResolver.VideoExtension.Length];
        // From here the video is in place: nothing that follows is cancelled half way.
        var sidecars = await WriteSidecarsAsync(songId, stem, download, options, CancellationToken.None).ConfigureAwait(false);

        try
        {
            PlacementPermissions.Apply(_disk, _importOptions.CurrentValue, [destination, .. sidecars], createdDirectories);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The video is in place; a mode that cannot be set is logged, as the audio placer does.
            LogPermissionsFailed(_logger, songId, exception);
        }

        return new MusicVideoFiled(recycled, oldRecycled);
    }

    /// <summary>Removes a leftover staging file; a file that cannot be removed is left, and the next import tries again.</summary>
    private void DeleteQuietly(string path)
    {
        try
        {
            if (_disk.FileExists(path))
            {
                _disk.DeleteFile(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not the video: a stale file under a name no scan reads.
            LogStagingLeft(_logger, path, exception);
        }
    }

    /// <summary>Moves recycled files back to where they were, never over anything; a failure is logged at Error with both paths.</summary>
    private void PutBack(long songId, IReadOnlyList<(string Original, string Recycled)> files)
    {
        for (var index = files.Count - 1; index >= 0; index--)
        {
            var (original, recycled) = files[index];

            try
            {
                _disk.MoveFile(recycled, original);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogPutBackFailed(_logger, songId, recycled, original, exception);
            }
        }
    }

    /// <summary>The folders from <paramref name="directory"/> upwards that do not exist yet, so only those get the folder mode.</summary>
    private List<string> MissingDirectories(string directory)
    {
        var missing = new List<string>();

        for (var current = directory; !string.IsNullOrEmpty(current) && !_disk.DirectoryExists(current); current = Path.GetDirectoryName(current))
        {
            missing.Add(current);
        }

        return missing;
    }

    private async Task<List<string>> WriteSidecarsAsync(
        long songId,
        string stem,
        MusicVideoDownload download,
        MusicVideoOptions options,
        CancellationToken cancellationToken)
    {
        // Sidecars are a convenience: one that cannot be written never fails a video that is already in place.
        var written = new List<string>();

        if (options.WriteNfo)
        {
            try
            {
                var song = await _database.Songs
                    .AsNoTracking()
                    .Include(candidate => candidate.AlbumContext)
                    .Include(candidate => candidate.Artists)
                        .ThenInclude(credit => credit.Artist)
                    .FirstOrDefaultAsync(candidate => candidate.Id == songId, cancellationToken)
                    .ConfigureAwait(false);

                if (song is not null)
                {
                    _disk.WriteNewFile(stem + ".nfo", Nfo(song));
                    written.Add(stem + ".nfo");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Xml.XmlException)
            {
                LogSidecarFailed(_logger, songId, ".nfo", exception);
            }
        }

        if (options.WriteThumbnail && download.ThumbnailPath is { Length: > 0 } thumbnail && _disk.FileExists(thumbnail))
        {
            var target = stem + "-thumb.jpg";

            try
            {
                if (!_disk.FileExists(target))
                {
                    Place(thumbnail, target);
                    written.Add(target);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogSidecarFailed(_logger, songId, "-thumb.jpg", exception);
            }
        }

        return written;
    }

    /// <summary>
    /// Puts a downloaded file at its final name without ever showing half of it there: a hard link when the disk
    /// allows one, otherwise a copy that is renamed into place. The download's own copy is removed afterwards.
    /// </summary>
    private void Place(string source, string target)
    {
        if (!_disk.TryCreateHardLink(source, target))
        {
            _disk.CopyFile(source, target);
        }

        _disk.DeleteFile(source);
    }

    /// <summary>The text without the characters XML cannot hold (control characters, lone surrogates).</summary>
    private static string XmlText(string text)
    {
        var builder = new StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                builder.Append(character).Append(text[index + 1]);
                index++;
            }
            else if (System.Xml.XmlConvert.IsXmlChar(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>The Kodi/Jellyfin <c>.nfo</c> of a song's video, UTF-8 without a byte order mark.</summary>
    private static byte[] Nfo(Song song)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            Indent = true,
            OmitXmlDeclaration = false,
        };

        using var stream = new MemoryStream();

        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartDocument(standalone: true);
            writer.WriteStartElement("musicvideo");
            writer.WriteElementString("title", XmlText(song.Title));

            foreach (var name in song.Artists.OrderBy(credit => credit.Position).Select(credit => credit.Artist.Name))
            {
                writer.WriteElementString("artist", XmlText(name));
            }

            if (!string.IsNullOrWhiteSpace(song.AlbumContext?.AlbumTitle))
            {
                writer.WriteElementString("album", XmlText(song.AlbumContext.AlbumTitle));
            }

            var date = song.AlbumContext?.Date;

            if (date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year))
            {
                writer.WriteElementString("year", year.ToString(CultureInfo.InvariantCulture));
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Why the video may not be filed at <paramref name="destination"/>: a video that a scan found already lies there
    /// (a file whose row says it has gone does not count, because nothing is there), and it is not the file this
    /// download replaces (<paramref name="replacedPath"/>). <see langword="null"/> when it may.
    /// </summary>
    private async Task<string?> HeldByFoundFileAsync(string destination, string? replacedPath, CancellationToken cancellationToken)
    {
        // Compared without regard to case: the share may be CIFS, where "A.mkv" and "a.mkv" are one file.
        var candidates = await _database.MusicVideoFiles
            .AsNoTracking()
            .Where(file => EF.Functions.Collate(file.Path, "NOCASE") == destination
                && file.State != MusicVideoFileState.Missing
                && file.MissingSince == null)
            .Select(file => file.Path)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The song's own found file is the one being replaced: it is recycled, not held against its replacement.
        candidates.RemoveAll(path => replacedPath is not null && PathRules.AreEqual(path, replacedPath));

        if (candidates.Count == 0)
        {
            return null;
        }

        // A row whose file is not on the disk (the user removed it) holds nothing. A disk that does not answer is
        // not an answer: the name stays held.
        bool onDisk;

        try
        {
            onDisk = await MusicVideoDisk
                .RunAsync(() => candidates.Exists(path => _disk.FileExists(path)), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MusicVideoTimeoutException)
        {
            onDisk = true;
        }

        return onDisk
            ? $"A video already in the folder has this name: {Path.GetFileName(destination)}; match it on the Match page"
            : null;
    }

    /// <summary>
    /// Reads the song's row for a download. <see cref="WantedRow.Message"/> says why there is nothing to do: the
    /// row is gone, or it now wants another video.
    /// </summary>
    private async Task<WantedRow> ReadRowAsync(long songId, string videoId, CancellationToken cancellationToken)
    {
        var row = await _database.MusicVideos
            .AsNoTracking()
            .FirstOrDefaultAsync(video => video.SongId == songId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return new WantedRow(null, false, "the video was removed, so nothing was downloaded.");
        }

        var replacing = row.Status == MusicVideoStatus.Present;

        // A queued row names the one video it wants; a filed one is replaced by whichever download finishes.
        if (!replacing && !string.Equals(row.YouTubeVideoId, videoId, StringComparison.Ordinal))
        {
            return new WantedRow(row, false, "another video was chosen, so this one was not downloaded.");
        }

        return new WantedRow(row, replacing, null);
    }

    /// <summary>
    /// Records a failure: the row of a song that holds no video says why, and, when the video itself is at fault, the
    /// id is blocklisted for the song. A filed video that a replacement failed to replace is left as it is.
    /// </summary>
    private async Task FailAsync(long songId, string videoId, bool fresh, string reason, bool blocklist)
    {
        LogFailed(_logger, videoId, songId, reason);

        if (fresh)
        {
            var now = _time.GetUtcNow().UtcDateTime;

            await _database.MusicVideos
                .Where(video => video.SongId == songId && video.YouTubeVideoId == videoId && video.Status != MusicVideoStatus.Present)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(video => video.Status, MusicVideoStatus.Failed)
                        .SetProperty(video => video.FailureReason, reason)
                        .SetProperty(video => video.UpdatedAt, now),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        else
        {
            await RecordReplacementFailureAsync(songId, videoId, reason, onlyIfDifferentVideo: false).ConfigureAwait(false);
        }

        if (!blocklist
            || await _database.MusicVideoBlocklist
                .AnyAsync(item => item.SongId == songId && item.YouTubeVideoId == videoId, CancellationToken.None)
                .ConfigureAwait(false)
            || !await _database.Songs.AnyAsync(song => song.Id == songId, CancellationToken.None).ConfigureAwait(false))
        {
            return;
        }

        _database.MusicVideoBlocklist.Add(new MusicVideoBlocklistItem { SongId = songId, YouTubeVideoId = videoId, Reason = reason });

        try
        {
            await _database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // Blocklisted by a request that overlapped this one: the video is blocklisted either way.
            _database.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// A replacement of a filed video failed: the row keeps its video and says why (until the next successful replacement
    /// or a dismissal), and the song's history gets a "Music video failed" event.
    /// </summary>
    private async Task RecordReplacementFailureAsync(long songId, string videoId, string reason, bool onlyIfDifferentVideo)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var updated = await _database.MusicVideos
            .Where(video => video.SongId == songId
                && video.Status == MusicVideoStatus.Present
                && (!onlyIfDifferentVideo || video.YouTubeVideoId != videoId))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(video => video.ReplaceFailureReason, reason)
                    .SetProperty(video => video.ReplaceFailedAt, now)
                    .SetProperty(video => video.UpdatedAt, now),
                CancellationToken.None)
            .ConfigureAwait(false);

        if (updated == 0)
        {
            return;
        }

        _database.History.Add(new HistoryItem
        {
            SongId = songId,
            EventType = HistoryEventType.MusicVideoFailed,
            Data = JsonSerializer.Serialize(new FailedHistoryData(videoId, reason, Replacement: true), Json),
        });

        await _database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private sealed record WantedRow(MusicVideo? Row, bool Replacing, string? Message);

    private sealed record MusicVideoFiled(IReadOnlyList<string> RecycledPaths, bool OldRecycled);

    private sealed record FailedHistoryData(string YouTubeVideoId, string Reason, bool Replacement);

    private sealed record ImportedHistoryData(string YouTubeVideoId, string FoundBy, string Path, IReadOnlyList<string> RecycledPaths);

    [LoggerMessage(Level = LogLevel.Information, Message = "Filed the music video {YouTubeVideoId} of song {SongId} at {Path}")]
    private static partial void LogFiled(ILogger logger, string youTubeVideoId, long songId, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The music video {YouTubeVideoId} of song {SongId} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string youTubeVideoId, long songId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The music video of song {SongId} was removed while its file was being put in place; the file is at {Path}")]
    private static partial void LogRowGone(ILogger logger, long songId, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "The old music video of song {SongId} ({Path}) is in the recycle bin ({Recycled}); the new one is renamed into its place next")]
    private static partial void LogRecycledBeforeSwap(ILogger logger, long songId, string path, string recycled);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove the leftover staging file {Path}")]
    private static partial void LogStagingLeft(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The new music video of song {SongId} is in place, but the old one at {Path} could not be recycled and stays where it is")]
    private static partial void LogOldVideoLeft(ILogger logger, long songId, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The new music video of song {SongId} could not be filed and the old files could not be put back: {Recycled} should return to {Original}")]
    private static partial void LogPutBackFailed(ILogger logger, long songId, string recycled, string original, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not set the file or folder mode of the music video of song {SongId}")]
    private static partial void LogPermissionsFailed(ILogger logger, long songId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the {Sidecar} sidecar of the music video of song {SongId}")]
    private static partial void LogSidecarFailed(ILogger logger, long songId, string sidecar, Exception exception);
}

/// <summary>A music video could not be put in place for a reason the message states (a file is already there).</summary>
public sealed class MusicVideoException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="MusicVideoException"/> class.</summary>
    /// <param name="message">What went wrong.</param>
    public MusicVideoException(string message)
        : base(message)
    {
    }
}
