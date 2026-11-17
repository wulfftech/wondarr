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
    /// <returns>What was done, for the command's message.</returns>
    /// <exception cref="MusicVideoImportException">The download or the check failed; the row says why.</exception>
    Task<string> ImportAsync(long songId, string videoId, MusicVideoFoundBy foundBy, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class MusicVideoImporter : IMusicVideoImporter
{
    /// <summary>How far the probed length may be from the length the download reported, in milliseconds (ADR-0014).</summary>
    public const int LengthToleranceMs = 15_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly WondarrDbContext _database;
    private readonly IOptionsMonitor<MusicVideoOptions> _options;
    private readonly IMusicVideoPathResolver _resolver;
    private readonly IMusicVideoDownloader _downloader;
    private readonly IMediaProbe _probe;
    private readonly IMusicVideoService _videos;
    private readonly IDiskOperations _disk;
    private readonly ILogger<MusicVideoImporter> _logger;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoImporter"/> class.</summary>
    /// <param name="database">The database.</param>
    /// <param name="options">The <c>music_videos</c> settings.</param>
    /// <param name="resolver">Decides where the video goes.</param>
    /// <param name="downloader">Downloads the video.</param>
    /// <param name="probe">Measures the downloaded file.</param>
    /// <param name="videos">Recycles the video a new one replaces.</param>
    /// <param name="disk">Places the files.</param>
    /// <param name="logger">Logs each step.</param>
    public MusicVideoImporter(
        WondarrDbContext database,
        IOptionsMonitor<MusicVideoOptions> options,
        IMusicVideoPathResolver resolver,
        IMusicVideoDownloader downloader,
        IMediaProbe probe,
        IMusicVideoService videos,
        IDiskOperations disk,
        ILogger<MusicVideoImporter> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(videos);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _options = options;
        _resolver = resolver;
        _downloader = downloader;
        _probe = probe;
        _videos = videos;
        _disk = disk;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> ImportAsync(long songId, string videoId, MusicVideoFoundBy foundBy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);

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

        var early = await _resolver.ResolveAsync(songId, cancellationToken).ConfigureAwait(false);

        if (!early.CanWrite)
        {
            await DropFreshRowAsync(songId, fresh).ConfigureAwait(false);

            return prefix + "deferred, " + early.DeferredReason;
        }

        if (fresh)
        {
            await _database.MusicVideos
                .Where(video => video.SongId == songId && video.Status == MusicVideoStatus.Queued)
                .ExecuteUpdateAsync(set => set.SetProperty(video => video.Status, MusicVideoStatus.Downloading), cancellationToken)
                .ConfigureAwait(false);
        }

        MusicVideoDownload download;

        try
        {
            download = await _downloader.DownloadAsync(videoId, options.MaxHeight, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var reason = $"The download failed: {exception.Message}";
            await FailAsync(songId, videoId, fresh, reason, blocklist: false).ConfigureAwait(false);

            throw new MusicVideoImportException(reason);
        }

        try
        {
            return await CheckAndFileAsync(songId, videoId, foundBy, fresh, download, options, prefix, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _downloader.Discard(download);
        }
    }

    private async Task<string> CheckAndFileAsync(
        long songId,
        string videoId,
        MusicVideoFoundBy foundBy,
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
            await DropFreshRowAsync(songId, fresh).ConfigureAwait(false);

            return prefix + "deferred, " + placement.DeferredReason + " The downloaded files were dropped.";
        }

        var destination = placement.VideoPath!;

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

    /// <summary>Moves the video and its sidecars into place, and recycles the video it replaces.</summary>
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
        var samePlace = old?.Path is { Length: > 0 } oldPath && PathRules.AreEqual(oldPath, destination);

        _disk.CreateDirectory(Path.GetDirectoryName(destination)!);

        // A video that is replaced where it lies has to leave first: nothing is ever written over a file.
        if (old is not null && samePlace)
        {
            recycled.AddRange(await _videos.RecycleFilesAsync(old, cancellationToken).ConfigureAwait(false));
        }

        if (_disk.FileExists(destination))
        {
            throw new MusicVideoException($"A file is already at {destination}.");
        }

        Place(download.VideoPath, destination);

        if (old is not null && !samePlace && !string.IsNullOrWhiteSpace(old.Path))
        {
            try
            {
                recycled.AddRange(await _videos.RecycleFilesAsync(old, cancellationToken).ConfigureAwait(false));
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

        await WriteSidecarsAsync(songId, stem, download, options, cancellationToken).ConfigureAwait(false);

        return new MusicVideoFiled(recycled);
    }

    private async Task WriteSidecarsAsync(
        long songId,
        string stem,
        MusicVideoDownload download,
        MusicVideoOptions options,
        CancellationToken cancellationToken)
    {
        // Sidecars are a convenience: one that cannot be written never fails a video that is already in place.
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
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogSidecarFailed(_logger, songId, "-thumb.jpg", exception);
            }
        }
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
            writer.WriteElementString("title", song.Title);

            foreach (var name in song.Artists.OrderBy(credit => credit.Position).Select(credit => credit.Artist.Name))
            {
                writer.WriteElementString("artist", name);
            }

            if (!string.IsNullOrWhiteSpace(song.AlbumContext?.AlbumTitle))
            {
                writer.WriteElementString("album", song.AlbumContext.AlbumTitle);
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
            await _database.MusicVideos
                .Where(video => video.SongId == songId && video.YouTubeVideoId == videoId && video.Status != MusicVideoStatus.Present)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(video => video.Status, MusicVideoStatus.Failed)
                        .SetProperty(video => video.FailureReason, reason),
                    CancellationToken.None)
                .ConfigureAwait(false);
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

    /// <summary>Removes the row a deferred download made, so the song is searchable again. A filed video's row stays.</summary>
    private async Task DropFreshRowAsync(long songId, bool fresh)
    {
        if (fresh)
        {
            await _database.MusicVideos
                .Where(video => video.SongId == songId && video.Status != MusicVideoStatus.Present)
                .ExecuteDeleteAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private sealed record WantedRow(MusicVideo? Row, bool Replacing, string? Message);

    private sealed record MusicVideoFiled(IReadOnlyList<string> RecycledPaths);

    private sealed record ImportedHistoryData(string YouTubeVideoId, string FoundBy, string Path, IReadOnlyList<string> RecycledPaths);

    [LoggerMessage(Level = LogLevel.Information, Message = "Filed the music video {YouTubeVideoId} of song {SongId} at {Path}")]
    private static partial void LogFiled(ILogger logger, string youTubeVideoId, long songId, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The music video {YouTubeVideoId} of song {SongId} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string youTubeVideoId, long songId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The music video of song {SongId} was removed while its file was being put in place; the file is at {Path}")]
    private static partial void LogRowGone(ILogger logger, long songId, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The new music video of song {SongId} is in place, but the old one at {Path} could not be recycled and stays where it is")]
    private static partial void LogOldVideoLeft(ILogger logger, long songId, string path, Exception exception);

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
