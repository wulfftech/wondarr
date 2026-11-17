using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Domain;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.MusicVideos;

/// <summary>What a song list shows of a song's music video.</summary>
/// <param name="Status">Where the video stands.</param>
/// <param name="Height">The picture height in pixels, or <see langword="null"/> when unknown.</param>
public sealed record MusicVideoState(MusicVideoStatus Status, int? Height);

/// <summary>What removing a song's music video did.</summary>
/// <param name="Path">Where the video file was, or <see langword="null"/> when the row had no file yet.</param>
/// <param name="RecycledPaths">Where the video and its sidecars are now, in the recycle bin.</param>
/// <param name="Blocklisted">Whether the YouTube video is now blocklisted for the song.</param>
public sealed record MusicVideoRemoval(string? Path, IReadOnlyList<string> RecycledPaths, bool Blocklisted);

/// <summary>
/// The video file lies inside a reference library: Wondarr never moves or deletes anything there, so the video
/// is left where it is.
/// </summary>
public sealed class MusicVideoInReferenceLibraryException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="MusicVideoInReferenceLibraryException"/> class.</summary>
    public MusicVideoInReferenceLibraryException()
        : base("The video file is inside a reference library, which Wondarr never writes into, so it was left where it is.")
    {
    }
}

/// <summary>
/// The path a row holds is not one Wondarr may act on: it is outside the music video folder (or, under
/// <c>beside_track</c>, outside every library), or it is a song's audio file. Nothing was touched.
/// </summary>
public sealed class MusicVideoPathException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="MusicVideoPathException"/> class.</summary>
    public MusicVideoPathException()
        : base("The video's path is outside the music video folder")
    {
    }
}

/// <summary>A file could not be moved into the recycle bin; the files already moved were put back.</summary>
public sealed class MusicVideoFileException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="MusicVideoFileException"/> class.</summary>
    /// <param name="path">The file that could not be moved.</param>
    /// <param name="busy">Whether the file is busy or locked (an I/O or permission failure).</param>
    /// <param name="innerException">What failed.</param>
    public MusicVideoFileException(string path, bool busy, Exception innerException)
        : base($"The file '{path}' could not be moved to the recycle bin: {innerException?.Message}", innerException)
    {
        Path = path;
        Busy = busy;
    }

    /// <summary>Gets the file that could not be moved.</summary>
    public string Path { get; }

    /// <summary>Gets a value indicating whether the file is busy or locked.</summary>
    public bool Busy { get; }
}

/// <summary>Reads and removes the music video of a song.</summary>
public interface IMusicVideoService
{
    /// <summary>The song's music video row.</summary>
    /// <param name="songId">The song.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The row, or <see langword="null"/> when the song has none.</returns>
    Task<MusicVideo?> GetAsync(long songId, CancellationToken cancellationToken);

    /// <summary>
    /// The video state of every song of a page, in one query. <see langword="null"/> while music videos are
    /// turned off, so a song list shows no video column at all; a song without a video is absent from the result.
    /// </summary>
    /// <param name="songIds">The songs of the page.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyDictionary<long, MusicVideoState>?> GetStatesAsync(
        IReadOnlyCollection<long> songIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves the video file and its <c>.nfo</c> and thumbnail sidecars to the recycle bin, optionally blocklists
    /// the YouTube video for the song, deletes the row and writes a history event. If a move fails, the files
    /// already moved are put back and nothing else changes.
    /// </summary>
    /// <param name="songId">The song.</param>
    /// <param name="blocklist">Whether the YouTube video is blocklisted for the song.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What was done, or <see langword="null"/> when the song has no video.</returns>
    /// <exception cref="MusicVideoInReferenceLibraryException">The file is inside a reference library.</exception>
    /// <exception cref="MusicVideoPathException">The row's path is not one Wondarr may act on.</exception>
    /// <exception cref="MusicVideoFileException">A file could not be moved.</exception>
    /// <exception cref="MusicVideoTimeoutException">The disk did not answer in time.</exception>
    Task<MusicVideoRemoval?> DeleteAsync(long songId, bool blocklist, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class MusicVideoService : IMusicVideoService
{
    /// <summary>The blocklist reason and the history message of a removed video.</summary>
    internal const string Reason = "Removed as the wrong video";

    /// <summary>
    /// What may sit beside a video and belongs to it: the Kodi/Jellyfin <c>.nfo</c> and the thumbnail, named
    /// as the video without its extension plus one of these.
    /// </summary>
    internal static readonly string[] SidecarSuffixes = [".nfo", ".jpg", ".jpeg", ".png", ".webp", "-thumb.jpg"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WondarrDbContext _database;
    private readonly IOptionsMonitor<MusicVideoOptions> _options;
    private readonly IRecycleBin _recycleBin;
    private readonly IDiskOperations _disk;
    private readonly ILogger<MusicVideoService> _logger;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoService"/> class.</summary>
    /// <param name="database">The database.</param>
    /// <param name="options">The <c>music_videos</c> settings.</param>
    /// <param name="recycleBin">Where the files go, the same bin Remove file uses.</param>
    /// <param name="disk">Tells which files exist, and puts a file back when a later one fails.</param>
    /// <param name="logger">Logs each removal.</param>
    public MusicVideoService(
        WondarrDbContext database,
        IOptionsMonitor<MusicVideoOptions> options,
        IRecycleBin recycleBin,
        IDiskOperations disk,
        ILogger<MusicVideoService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(recycleBin);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _options = options;
        _recycleBin = recycleBin;
        _disk = disk;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<MusicVideo?> GetAsync(long songId, CancellationToken cancellationToken) =>
        _database.MusicVideos
            .AsNoTracking()
            .FirstOrDefaultAsync(video => video.SongId == songId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, MusicVideoState>?> GetStatesAsync(
        IReadOnlyCollection<long> songIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(songIds);

        if (!_options.CurrentValue.Enabled)
        {
            return null;
        }

        if (songIds.Count == 0)
        {
            return new Dictionary<long, MusicVideoState>();
        }

        var rows = await _database.MusicVideos
            .AsNoTracking()
            .Where(video => songIds.Contains(video.SongId))
            .Select(video => new { video.SongId, video.Status, video.Height })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(row => row.SongId, row => new MusicVideoState(row.Status, row.Height));
    }

    /// <inheritdoc />
    public async Task<MusicVideoRemoval?> DeleteAsync(long songId, bool blocklist, CancellationToken cancellationToken)
    {
        var video = await _database.MusicVideos
            .FirstOrDefaultAsync(row => row.SongId == songId, cancellationToken)
            .ConfigureAwait(false);

        if (video is null)
        {
            return null;
        }

        var files = new List<string>();
        string? binRoot = null;

        if (!string.IsNullOrWhiteSpace(video.Path))
        {
            var audioPath = await _database.SongFiles
                .AsNoTracking()
                .Where(file => file.SongId == songId)
                .Select(file => file.Path)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            // No path from a row is trusted: it is acted on only inside the folders Wondarr writes videos to.
            binRoot = await AllowedRootAsync(video.Path, audioPath, cancellationToken).ConfigureAwait(false)
                ?? throw new MusicVideoPathException();

            files = await FilesOfAsync(video.Path, audioPath, cancellationToken).ConfigureAwait(false);

            await GuardReferenceLibrariesAsync(files, cancellationToken).ConfigureAwait(false);
        }

        var recycledPaths = await Recycle(files, binRoot ?? string.Empty, cancellationToken).ConfigureAwait(false);

        // Already blocklisted counts as blocklisted: the video will not come back either way.
        MusicVideoBlocklistItem? blocked = null;

        if (blocklist
            && !await _database.MusicVideoBlocklist
                .AnyAsync(item => item.SongId == songId && item.YouTubeVideoId == video.YouTubeVideoId, cancellationToken)
                .ConfigureAwait(false))
        {
            blocked = new MusicVideoBlocklistItem
            {
                SongId = songId,
                YouTubeVideoId = video.YouTubeVideoId,
                Reason = Reason,
            };
            _database.MusicVideoBlocklist.Add(blocked);
        }

        _database.History.Add(new HistoryItem
        {
            SongId = songId,
            EventType = HistoryEventType.MusicVideoDeleted,
            Data = JsonSerializer.Serialize(
                new RemovalHistoryData(Reason, video.YouTubeVideoId, video.Path, recycledPaths, blocklist),
                Json),
        });

        _database.MusicVideos.Remove(video);

        try
        {
            try
            {
                // The history, the blocklist row and the forgotten video in one save.
                await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException exception) when (blocked is not null && IsUniqueViolation(exception))
            {
                // A concurrent delete blocklisted the same video first: that is what was asked for.
                _database.Entry(blocked).State = EntityState.Detached;
                await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The files are in the bin and the row is still there: say where they are, and do not guess.
            LogSaveFailed(_logger, songId, string.Join("; ", recycledPaths), exception);

            throw;
        }

        LogRemoved(_logger, video.YouTubeVideoId, songId, video.Path ?? "(no file yet)", blocklist);

        return new MusicVideoRemoval(video.Path, recycledPaths, blocklist);
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 } sqlite
        && sqlite.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Moves every file into the recycle bin. When one fails, the ones already moved are put back, never over
    /// anything, and the failure names the file.
    /// </summary>
    private async Task<List<string>> Recycle(List<string> files, string binRoot, CancellationToken cancellationToken)
    {
        var moved = new List<(string Original, string Recycled)>(files.Count);

        foreach (var file in files)
        {
            try
            {
                moved.Add((file, await _recycleBin.RecycleAsync(file, binRoot, cancellationToken).ConfigureAwait(false)));
            }
            catch (Exception exception)
            {
                // The bin may have moved the file and failed afterwards: that copy is in the bin too.
                if (exception is RecycleMovedException late)
                {
                    moved.Add((file, late.RecycledPath));
                }

                PutBack(moved);

                if (exception is OperationCanceledException)
                {
                    throw;
                }

                throw new MusicVideoFileException(file, busy: exception is IOException or UnauthorizedAccessException, exception);
            }
        }

        return [.. moved.Select(entry => entry.Recycled)];
    }

    private void PutBack(List<(string Original, string Recycled)> moved)
    {
        for (var index = moved.Count - 1; index >= 0; index--)
        {
            var (original, recycled) = moved[index];

            try
            {
                // MoveFile never overwrites: a file that appeared in the meantime is kept and this one stays in the bin.
                _disk.MoveFile(recycled, original);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogPutBackFailed(_logger, recycled, original, exception);
            }
        }
    }

    /// <summary>
    /// The folder the video's path is shown relative to in the bin, when the path may be acted on: strictly inside
    /// the video root, or under <c>beside_track</c> strictly inside a library root, and never a song's audio file.
    /// <see langword="null"/> when it may not.
    /// </summary>
    private async Task<string?> AllowedRootAsync(string path, string? audioPath, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return null;
            }

            if ((audioPath is not null && PathRules.AreEqual(audioPath, path))
                || await _database.SongFiles
                    .AsNoTracking()
                    .AnyAsync(file => file.Path == path, cancellationToken)
                    .ConfigureAwait(false))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(options.RootPath) && PathRules.IsStrictlyInside(options.RootPath, path))
            {
                return options.RootPath;
            }

            if (options.Layout == MusicVideoOptions.BesideTrackLayout)
            {
                var libraries = await _database.Libraries
                    .AsNoTracking()
                    .Select(library => library.RootPath)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                return libraries.FirstOrDefault(root => !string.IsNullOrWhiteSpace(root) && PathRules.IsStrictlyInside(root, path));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// The video file and the sidecars that exist beside it, asked of the disk with a time limit. A song's own audio
    /// file and its sidecars are never listed, even when an old row's video shares the audio's name.
    /// </summary>
    private Task<List<string>> FilesOfAsync(string videoPath, string? audioPath, CancellationToken cancellationToken) =>
        MusicVideoDisk.RunAsync(
            () =>
            {
                var files = new List<string>();

                if (_disk.FileExists(videoPath))
                {
                    files.Add(videoPath);
                }

                var directory = Path.GetDirectoryName(videoPath) ?? string.Empty;
                var stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(videoPath));

                // The same name as the audio file: the .nfo and the thumbnail beside it may be the audio's, so they stay.
                if (audioPath is not null
                    && PathRules.AreEqual(
                        Path.Combine(Path.GetDirectoryName(audioPath) ?? string.Empty, Path.GetFileNameWithoutExtension(audioPath)),
                        stem))
                {
                    return files;
                }

                foreach (var suffix in SidecarSuffixes)
                {
                    var sidecar = stem + suffix;

                    if (!files.Contains(sidecar, StringComparer.Ordinal)
                        && (audioPath is null || !PathRules.AreEqual(audioPath, sidecar))
                        && _disk.FileExists(sidecar))
                    {
                        files.Add(sidecar);
                    }
                }

                return files;
            },
            cancellationToken);

    private async Task GuardReferenceLibrariesAsync(IEnumerable<string> files, CancellationToken cancellationToken)
    {
        var roots = await _database.ReferenceLibraries
            .AsNoTracking()
            .Select(reference => reference.RootPath)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (files.Any(file => roots.Any(root => PathRules.IsInside(root, file))))
        {
            throw new MusicVideoInReferenceLibraryException();
        }
    }

    /// <summary>The history payload of a removed video.</summary>
    private sealed record RemovalHistoryData(
        string Reason,
        string YouTubeVideoId,
        string? Path,
        IReadOnlyList<string> RecycledPaths,
        bool Blocklisted);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed the music video {YouTubeVideoId} of song {SongId} ({Path}) (blocklisted: {Blocklisted})")]
    private static partial void LogRemoved(ILogger logger, string youTubeVideoId, long songId, string path, bool blocklisted);

    [LoggerMessage(Level = LogLevel.Error, Message = "The files of the music video of song {SongId} are in the recycle bin but the database change failed; they are at: {RecycledPaths}")]
    private static partial void LogSaveFailed(ILogger logger, long songId, string recycledPaths, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not put {Recycled} back to {Original} after a later file failed; it stays in the recycle bin")]
    private static partial void LogPutBackFailed(ILogger logger, string recycled, string original, Exception exception);
}
