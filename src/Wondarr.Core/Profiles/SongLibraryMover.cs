using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Compaction;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Profiles;

/// <summary>How moving one song to another library ended.</summary>
public enum SongMoveOutcome
{
    /// <summary>The song is in the target library; its file, if it had one, is filed under the target's root.</summary>
    Moved,

    /// <summary>The song was already in the target library; nothing was done.</summary>
    AlreadyThere,

    /// <summary>The song cannot be moved right now; <see cref="SongMoveResult.Reason"/> says why.</summary>
    Refused,

    /// <summary>The move was attempted and failed; the file and the rows are as they were.</summary>
    Failed,
}

/// <summary>One move's outcome, and the reason it was refused or failed.</summary>
/// <param name="Outcome">How the move ended.</param>
/// <param name="Reason">Why the move was refused or failed, or <see langword="null"/> when it did not.</param>
public sealed record SongMoveResult(SongMoveOutcome Outcome, string? Reason = null)
{
    /// <summary>A move that succeeded.</summary>
    public static SongMoveResult Moved => new(SongMoveOutcome.Moved);

    /// <summary>A move that had nothing to do.</summary>
    public static SongMoveResult AlreadyThere => new(SongMoveOutcome.AlreadyThere);
}

/// <summary>
/// Moves a song from its library to another one (DECISIONS build session 7 #3): the song's row points
/// at the target library, and a file Wondarr placed is re-filed under the target's root and naming
/// template through the same organizer an import uses. A reference file — or no file at all — is
/// never touched: only the song's library changes.
/// </summary>
public interface ISongLibraryMover
{
    /// <summary>Moves one song to another library.</summary>
    /// <param name="songId">The song to move.</param>
    /// <param name="targetLibraryId">The library it goes to.</param>
    /// <param name="cancellationToken">Cancels between steps.</param>
    /// <returns>How the move ended, and why when it did not happen.</returns>
    Task<SongMoveResult> MoveAsync(long songId, long targetLibraryId, CancellationToken cancellationToken);
}

/// <summary>The one implementation of <see cref="ISongLibraryMover"/>.</summary>
public sealed partial class SongLibraryMover : ISongLibraryMover
{
    /// <summary>The history payload's shape: camelCase, nulls left out.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WondarrDbContext _database;
    private readonly ILibraryOrganizer _organizer;
    private readonly IDiskOperations _disk;
    private readonly IRecycleBin _recycleBin;
    private readonly IPlexLibraryUpdater _updater;
    private readonly ISongFileLock _songFileLock;
    private readonly ILogger<SongLibraryMover> _logger;

    /// <summary>Initialises a new instance of the <see cref="SongLibraryMover"/> class.</summary>
    /// <param name="database">The Wondarr database, which holds the songs and their files.</param>
    /// <param name="organizer">The organizer an import uses, which re-files the file under the target library.</param>
    /// <param name="disk">The file system.</param>
    /// <param name="recycleBin">Where a lone <c>cover.jpg</c> an emptied folder is left holding goes.</param>
    /// <param name="updater">The debounced Plex scan, asked for both folders.</param>
    /// <param name="songFileLock">The per-song file lock, held for the whole move.</param>
    /// <param name="logger">The logger.</param>
    public SongLibraryMover(
        WondarrDbContext database,
        ILibraryOrganizer organizer,
        IDiskOperations disk,
        IRecycleBin recycleBin,
        IPlexLibraryUpdater updater,
        ISongFileLock songFileLock,
        ILogger<SongLibraryMover> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(organizer);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(recycleBin);
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(songFileLock);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _organizer = organizer;
        _disk = disk;
        _recycleBin = recycleBin;
        _updater = updater;
        _songFileLock = songFileLock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SongMoveResult> MoveAsync(long songId, long targetLibraryId, CancellationToken cancellationToken)
    {
        // The song is loaded exactly as the import loads it: the album context decides the folder and
        // the tags, and the credits are what the tag writer writes.
        var song = await _database.Songs
            .Include(candidate => candidate.AlbumContext)
            .Include(candidate => candidate.File).ThenInclude(file => file!.Quality)
            .Include(candidate => candidate.Artists).ThenInclude(credit => credit.Artist)
            .FirstOrDefaultAsync(candidate => candidate.Id == songId, cancellationToken)
            .ConfigureAwait(false);

        if (song is null)
        {
            return new SongMoveResult(SongMoveOutcome.Refused, $"song {songId} does not exist.");
        }

        var target = await _database.Libraries
            .AsNoTracking()
            .FirstOrDefaultAsync(library => library.Id == targetLibraryId, cancellationToken)
            .ConfigureAwait(false);

        if (target is null)
        {
            return new SongMoveResult(SongMoveOutcome.Refused, $"library {targetLibraryId} does not exist.");
        }

        if (song.LibraryId == targetLibraryId)
        {
            return SongMoveResult.AlreadyThere;
        }

        var fromLibrary = await _database.Libraries
            .AsNoTracking()
            .FirstOrDefaultAsync(library => library.Id == song.LibraryId, cancellationToken)
            .ConfigureAwait(false);

        // The per-song file guard, held for the whole move: an import or a compaction can never act
        // on this song's file while it is changing libraries.
        await using var _ = await _songFileLock
            .AcquireAsync(song.Id, cancellationToken)
            .ConfigureAwait(false);

        if (await _database.CompactMoves
                .AsNoTracking()
                .Where(CompactMoveRules.IsUnfinished)
                .AnyAsync(row => row.SongId == song.Id, cancellationToken)
                .ConfigureAwait(false))
        {
            return new SongMoveResult(SongMoveOutcome.Refused, "the song has an unfinished compaction move.");
        }

        if (await _database.QueueItems
                .AsNoTracking()
                .AnyAsync(item => item.SongId == song.Id && item.State == QueueItemState.Importing, cancellationToken)
                .ConfigureAwait(false))
        {
            return new SongMoveResult(SongMoveOutcome.Refused, "the song is being imported.");
        }

        var fromPath = song.File?.Path;

        if (song.File is null || song.File.SourceType == SourceTypes.Reference)
        {
            // The user's own file is never moved: only the song's library changes, and the target
            // library's Compact task re-plans the album context if it is not pinned.
            song.LibraryId = targetLibraryId;

            WriteHistory(song.Id, fromLibrary?.Id, targetLibraryId, fromPath, fromPath);

            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            RequestScans(fromLibrary, target, fromPath, fromPath);

            return SongMoveResult.Moved;
        }

        if (song.AlbumContext is not { } album)
        {
            return new SongMoveResult(SongMoveOutcome.Refused, "the song has no album context.");
        }

        var file = song.File;

        // The organizer reads the song, so it must already point at the target library when it runs.
        song.LibraryId = targetLibraryId;

        // The probe's own measurements, rebuilt from the row: they are what the naming tokens read.
        var media = new MediaInfo(
            file.Codec,
            file.Container,
            file.BitrateKbps,
            file.SampleRate,
            file.BitDepth,
            file.Channels,
            file.DurationMs ?? 0,
            file.Quality.Lossless,
            file.Size);

        var extension = Path.GetExtension(file.Path).TrimStart('.').ToLowerInvariant();

        var credits = song.Artists
            .OrderBy(credit => credit.Position)
            .Select(credit => (credit.Artist, credit.Role))
            .ToList();

        OrganizeResult placement;

        try
        {
            placement = await _organizer
                .OrganizeAsync(
                    new OrganizeRequest(
                        song,
                        album,
                        credits,
                        target,
                        file.Path,
                        extension,
                        media,
                        file.Quality,
                        file.SourceType,
                        file.AcoustId,
                        KeepSource: false,
                        ReplacesPath: null,

                        // The file carries whatever lyrics the import gave it, and its sidecar is
                        // coming with it: asking LRCLIB again could only overwrite the user's own.
                        LookUpLyrics: false),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The organizer moves the file at its last step, so a file it failed on is exactly where
            // it was; nothing is saved with the failure.
            LogMoveFailed(_logger, song.Id, targetLibraryId, exception.Message);

            _database.ChangeTracker.Clear();

            return new SongMoveResult(SongMoveOutcome.Failed, exception.Message);
        }

        if (!placement.Success || placement.FinalPath is null)
        {
            LogMoveFailed(_logger, song.Id, targetLibraryId, placement.Error ?? "the organizer gave no final path");

            _database.ChangeTracker.Clear();

            return new SongMoveResult(SongMoveOutcome.Failed, placement.Error ?? "the organizer gave no final path");
        }

        var finalPath = placement.FinalPath;

        // The sidecars travel with their file, under the placed file's own name.
        foreach (var (sidecar, beside, targetTaken) in SongFileRules.SidecarMoves(_disk, file.Path, finalPath))
        {
            if (targetTaken)
            {
                LogSidecarLeftBehind(_logger, sidecar, beside);

                continue;
            }

            try
            {
                _disk.MoveFile(sidecar, beside);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogSidecarNotMoved(_logger, sidecar, exception.Message);
            }
        }

        try
        {
            file.Path = finalPath;
            file.Size = _disk.GetFileSize(finalPath);

            WriteHistory(song.Id, fromLibrary?.Id, targetLibraryId, fromPath, finalPath);

            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The file is in the target library already; only the bookkeeping failed. The least the
            // rows must say is where the file is and which library it belongs to, written on their own
            // (as the compaction executor does); the history is lost and the reason is returned.
            LogMoveFailed(_logger, song.Id, targetLibraryId, exception.Message);
            _database.ChangeTracker.Clear();

            var fileId = file.Id;
            var movedSongId = song.Id;
            await _database.SongFiles
                .Where(candidate => candidate.Id == fileId)
                .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.Path, finalPath), cancellationToken)
                .ConfigureAwait(false);
            await _database.Songs
                .Where(candidate => candidate.Id == movedSongId)
                .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.LibraryId, targetLibraryId), cancellationToken)
                .ConfigureAwait(false);

            return new SongMoveResult(
                SongMoveOutcome.Failed,
                $"Moved to {finalPath}, but recording it failed: {exception.Message}");
        }

        await CleanUpFolderAsync(fromLibrary, fromPath, cancellationToken).ConfigureAwait(false);

        RequestScans(fromLibrary, target, fromPath, finalPath);

        return SongMoveResult.Moved;
    }

    /// <summary>Records the move in the song's history: a <see cref="HistoryEventType.Renamed"/> row.</summary>
    private void WriteHistory(long songId, long? fromLibraryId, long toLibraryId, string? from, string? to) =>
        _database.History.Add(new HistoryItem
        {
            SongId = songId,
            EventType = HistoryEventType.Renamed,
            Data = JsonSerializer.Serialize(
                new LibraryMoveHistoryData("library", fromLibraryId, toLibraryId, from, to),
                Json),
        });

    /// <summary>
    /// Recycles the <c>cover.jpg</c> of an album folder the move emptied, then the folder, then the
    /// artist folder it leaves empty — the same rule the Compact task's clean-up applies.
    /// </summary>
    private async Task CleanUpFolderAsync(Library? library, string? movedFrom, CancellationToken cancellationToken)
    {
        if (library is null || movedFrom is null)
        {
            return;
        }

        var folder = Path.GetDirectoryName(movedFrom);

        if (folder is null || !_disk.DirectoryExists(folder) || !PathRules.IsStrictlyInside(library.RootPath, folder))
        {
            return;
        }

        if (!SongFileRules.HoldsOnlyCovers(_disk, folder, out var covers))
        {
            return;
        }

        try
        {
            foreach (var cover in covers)
            {
                await _recycleBin.RecycleAsync(cover, library.RootPath, cancellationToken).ConfigureAwait(false);
            }

            _disk.DeleteEmptyDirectory(folder);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A tidy-up that fails never undoes the move; the folder keeps whatever it is holding.
            LogFolderNotCleaned(_logger, folder, exception.Message);

            return;
        }

        var parent = Path.GetDirectoryName(folder);

        if (parent is not null
            && !PathRules.AreEqual(parent, library.RootPath)
            && PathRules.IsStrictlyInside(library.RootPath, parent))
        {
            try
            {
                _disk.DeleteEmptyDirectory(parent);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFolderNotCleaned(_logger, parent, exception.Message);
            }
        }
    }

    /// <summary>Asks Plex to scan the folder the file left and the one it arrived in.</summary>
    private void RequestScans(Library? fromLibrary, Library target, string? from, string? to)
    {
        if (fromLibrary is not null && from is not null)
        {
            _updater.RequestFolder(fromLibrary.Id, Path.GetDirectoryName(from) ?? fromLibrary.RootPath);
        }

        if (to is not null)
        {
            _updater.RequestFolder(target.Id, Path.GetDirectoryName(to) ?? target.RootPath);
        }
    }

    /// <summary>The history payload of a library move: which libraries, and where the file went.</summary>
    private sealed record LibraryMoveHistoryData(
        string Reason,
        long? FromLibraryId,
        long ToLibraryId,
        string? From,
        string? To);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Moving song {SongId} to library {LibraryId} failed: {Message}")]
    private static partial void LogMoveFailed(ILogger logger, long songId, long libraryId, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sidecar {Sidecar} stayed behind: {Target} already exists")]
    private static partial void LogSidecarLeftBehind(ILogger logger, string sidecar, string target);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sidecar {Sidecar} could not be moved: {Message}")]
    private static partial void LogSidecarNotMoved(ILogger logger, string sidecar, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The folder {Folder} could not be cleaned up: {Message}")]
    private static partial void LogFolderNotCleaned(ILogger logger, string folder, string message);
}
