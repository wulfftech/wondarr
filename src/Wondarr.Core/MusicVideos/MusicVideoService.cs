using System.Text.Json;
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
    /// the YouTube video for the song, deletes the row and writes a history event.
    /// </summary>
    /// <param name="songId">The song.</param>
    /// <param name="blocklist">Whether the YouTube video is blocklisted for the song.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What was done, or <see langword="null"/> when the song has no video.</returns>
    /// <exception cref="MusicVideoInReferenceLibraryException">The file is inside a reference library.</exception>
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
    /// <param name="disk">Tells which files exist.</param>
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

        var files = FilesOf(video);

        if (files.Count > 0)
        {
            await GuardReferenceLibrariesAsync(files, cancellationToken).ConfigureAwait(false);
        }

        var binRoot = await BinRootAsync(video, cancellationToken).ConfigureAwait(false);
        var recycled = new List<string>(files.Count);

        foreach (var file in files)
        {
            recycled.Add(await _recycleBin.RecycleAsync(file, binRoot, cancellationToken).ConfigureAwait(false));
        }

        // Already blocklisted counts as blocklisted: the video will not come back either way.
        if (blocklist
            && !await _database.MusicVideoBlocklist
                .AnyAsync(item => item.SongId == songId && item.YouTubeVideoId == video.YouTubeVideoId, cancellationToken)
                .ConfigureAwait(false))
        {
            _database.MusicVideoBlocklist.Add(new MusicVideoBlocklistItem
            {
                SongId = songId,
                YouTubeVideoId = video.YouTubeVideoId,
                Reason = Reason,
            });
        }

        var blocklisted = blocklist;

        _database.History.Add(new HistoryItem
        {
            SongId = songId,
            EventType = HistoryEventType.MusicVideoDeleted,
            Data = JsonSerializer.Serialize(
                new RemovalHistoryData(Reason, video.YouTubeVideoId, video.Path, recycled, blocklisted),
                Json),
        });

        _database.MusicVideos.Remove(video);

        // The history, the blocklist row and the forgotten video in one save.
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogRemoved(_logger, video.YouTubeVideoId, songId, video.Path ?? "(no file yet)", blocklisted);

        return new MusicVideoRemoval(video.Path, recycled, blocklisted);
    }

    /// <summary>The video file and the sidecars that exist beside it.</summary>
    private List<string> FilesOf(MusicVideo video)
    {
        var files = new List<string>();

        if (string.IsNullOrWhiteSpace(video.Path))
        {
            return files;
        }

        if (_disk.FileExists(video.Path))
        {
            files.Add(video.Path);
        }

        var stem = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(video.Path) ?? string.Empty,
            System.IO.Path.GetFileNameWithoutExtension(video.Path));

        foreach (var suffix in SidecarSuffixes)
        {
            var sidecar = stem + suffix;

            if (_disk.FileExists(sidecar) && !files.Contains(sidecar, StringComparer.Ordinal))
            {
                files.Add(sidecar);
            }
        }

        return files;
    }

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

    /// <summary>
    /// The folder the video's path is shown relative to in the bin: the video root when the file is inside it, else
    /// the root of the song's library, else the file's own folder.
    /// </summary>
    private async Task<string> BinRootAsync(MusicVideo video, CancellationToken cancellationToken)
    {
        var videoRoot = _options.CurrentValue.RootPath;

        if (video.Path is null)
        {
            return videoRoot ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(videoRoot) && PathRules.IsInside(videoRoot, video.Path))
        {
            return videoRoot;
        }

        var libraryRoot = await _database.Songs
            .AsNoTracking()
            .Where(song => song.Id == video.SongId)
            .Join(_database.Libraries, song => song.LibraryId, library => library.Id, (song, library) => library.RootPath)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(libraryRoot) && PathRules.IsInside(libraryRoot, video.Path))
        {
            return libraryRoot;
        }

        return System.IO.Path.GetDirectoryName(video.Path) ?? string.Empty;
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
}
