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

namespace Wondarr.Core.Songs;

/// <summary>How a re-file of one song ended.</summary>
public enum SongRefileOutcome
{
    /// <summary>The file was moved to the folder its album names, and retagged.</summary>
    Refiled,

    /// <summary>There was no managed file to move: the song has none, or only a reference file.</summary>
    NothingToMove,

    /// <summary>The file was already where its album puts it, so it was retagged in place.</summary>
    AlreadyThere,

    /// <summary>The song could not be re-filed now (a compaction or an import has it); nothing changed.</summary>
    Refused,

    /// <summary>The move failed; the file is where it was, or the reason says where it is.</summary>
    Failed,
}

/// <summary>The result of re-filing a song.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Reason">Why it was refused or failed, or <see langword="null"/>.</param>
public sealed record SongRefileResult(SongRefileOutcome Outcome, string? Reason = null);

/// <summary>
/// Re-files one song under the album it is now pinned to: the file moves to the path the library's
/// naming template renders for that album and is retagged, through the same organizer imports use.
/// </summary>
public interface ISongRefiler
{
    /// <summary>Moves and retags the song's managed file for its current album context.</summary>
    /// <param name="songId">The song to re-file.</param>
    /// <param name="cancellationToken">Cancels the work before the file is touched.</param>
    /// <returns>How it ended; a reference file is never touched.</returns>
    Task<SongRefileResult> RefileAsync(long songId, CancellationToken cancellationToken);
}

/// <summary>
/// The one implementation of <see cref="ISongRefiler"/>. It is the library mover's move step with the
/// song's own library as the target (the mover itself refuses a same-library move, and belongs to
/// another task), so the rules match: the per-song file lock, the refusal while a compaction or an
/// import has the song, sidecars that travel with the file, the emptied folder tidied, and a partial
/// Plex scan of the old and the new folder.
/// </summary>
public sealed partial class SongRefiler : ISongRefiler
{
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
    private readonly ILogger<SongRefiler> _logger;

    /// <summary>Initialises a new instance of the <see cref="SongRefiler"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="organizer">Tags, names and places the file.</param>
    /// <param name="disk">Every file-system call.</param>
    /// <param name="recycleBin">Where the emptied album folder's <c>cover.jpg</c> goes.</param>
    /// <param name="updater">The debounced partial Plex scan.</param>
    /// <param name="songFileLock">The per-song lock imports and compactions also take.</param>
    /// <param name="logger">The log sink.</param>
    public SongRefiler(
        WondarrDbContext database,
        ILibraryOrganizer organizer,
        IDiskOperations disk,
        IRecycleBin recycleBin,
        IPlexLibraryUpdater updater,
        ISongFileLock songFileLock,
        ILogger<SongRefiler> logger)
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
    public async Task<SongRefileResult> RefileAsync(long songId, CancellationToken cancellationToken)
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
            return new SongRefileResult(SongRefileOutcome.Refused, $"song {songId} does not exist.");
        }

        if (song.File is null || song.File.SourceType == SourceTypes.Reference)
        {
            // The user's own file is never moved: only the database knew about the album.
            return new SongRefileResult(SongRefileOutcome.NothingToMove);
        }

        var library = await _database.Libraries
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == song.LibraryId, cancellationToken)
            .ConfigureAwait(false);

        if (library is null || song.AlbumContext is not { } album)
        {
            return new SongRefileResult(SongRefileOutcome.Refused, "the song has no library or album context.");
        }

        // The per-song file guard, held for the whole move: an import or a compaction can never act on
        // this song's file while it is being re-filed.
        await using var _ = await _songFileLock
            .AcquireAsync(song.Id, cancellationToken)
            .ConfigureAwait(false);

        if (await _database.CompactMoves
                .AsNoTracking()
                .Where(CompactMoveRules.IsUnfinished)
                .AnyAsync(row => row.SongId == song.Id, cancellationToken)
                .ConfigureAwait(false))
        {
            return new SongRefileResult(SongRefileOutcome.Refused, "the song has an unfinished compaction move.");
        }

        if (await _database.QueueItems
                .AsNoTracking()
                .AnyAsync(item => item.SongId == song.Id && item.State == QueueItemState.Importing, cancellationToken)
                .ConfigureAwait(false))
        {
            return new SongRefileResult(SongRefileOutcome.Refused, "the song is being imported.");
        }

        var file = song.File;
        var fromPath = file.Path;

        if (!_disk.FileExists(fromPath))
        {
            return new SongRefileResult(SongRefileOutcome.Failed, $"the file {fromPath} was not found.");
        }

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

        var extension = Path.GetExtension(fromPath).TrimStart('.').ToLowerInvariant();

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
                        library,
                        fromPath,
                        extension,
                        media,
                        file.Quality,
                        file.SourceType,
                        file.AcoustId,
                        KeepSource: false,
                        ReplacesPath: null,

                        // The file carries whatever lyrics the import gave it, and its sidecar is
                        // coming with it: asking LRCLIB again could only overwrite the user's own.
                        LookUpLyrics: false,
                        ReplayGainDb: file.ReplayGainDb,
                        ReplayGainPeak: file.ReplayGainPeak),
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
            LogRefileFailed(_logger, song.Id, exception.Message);

            _database.ChangeTracker.Clear();

            return new SongRefileResult(SongRefileOutcome.Failed, exception.Message);
        }

        // A result that is not a success can still have put the file at FinalPath (a chmod that failed
        // after the move): then the file is there, and the database has to say so.
        var finalPath = placement.FinalPath;
        var placed = finalPath is not null
            && (placement.Success || (!PathRules.AreEqual(finalPath, fromPath) && _disk.FileExists(finalPath)));

        if (!placed || finalPath is null)
        {
            var reason = placement.Error ?? "the organizer gave no final path";

            LogRefileFailed(_logger, song.Id, reason);

            _database.ChangeTracker.Clear();

            return new SongRefileResult(SongRefileOutcome.Failed, reason);
        }

        var moved = !PathRules.AreEqual(finalPath, fromPath);

        if (moved)
        {
            // The sidecars travel with their file, under the placed file's own name.
            foreach (var (sidecar, beside, targetTaken) in SongFileRules.SidecarMoves(_disk, fromPath, finalPath))
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
        }

        try
        {
            file.Path = finalPath;
            file.Size = _disk.GetFileSize(finalPath);
            file.TagsWritten = JsonSerializer.Serialize(placement.TagsWritten, Json);

            // The writer replaces every tag, so a library without ReplayGain holds none: the row must
            // not keep values the file no longer carries.
            if (!library.ReplayGain)
            {
                file.ReplayGainDb = null;
                file.ReplayGainPeak = null;
            }

            _database.History.Add(new HistoryItem
            {
                SongId = song.Id,
                EventType = HistoryEventType.Renamed,
                Data = JsonSerializer.Serialize(new RefileHistoryData(finalPath, fromPath, album.AlbumTitle), Json),
            });

            // The file has moved already: nothing a shutdown does may leave the database behind it.
            await _database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The file is at its new path already; only the bookkeeping failed. The least the row must
            // say is where the file is; the history is lost and the reason is returned.
            LogRefileFailed(_logger, song.Id, exception.Message);
            _database.ChangeTracker.Clear();

            var fileId = file.Id;

            await _database.SongFiles
                .Where(candidate => candidate.Id == fileId)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(candidate => candidate.Path, finalPath),
                    CancellationToken.None)
                .ConfigureAwait(false);

            return new SongRefileResult(
                SongRefileOutcome.Failed,
                $"Moved to {finalPath}, but recording it failed: {exception.Message}");
        }

        if (!moved)
        {
            _updater.RequestFolder(library.Id, Path.GetDirectoryName(finalPath) ?? library.RootPath);

            return new SongRefileResult(SongRefileOutcome.AlreadyThere);
        }

        await CleanUpFolderAsync(library, fromPath, cancellationToken).ConfigureAwait(false);

        // Plex learns about both folders, as it does after an import or a library move.
        _updater.RequestFolder(library.Id, Path.GetDirectoryName(fromPath) ?? library.RootPath);
        _updater.RequestFolder(library.Id, Path.GetDirectoryName(finalPath) ?? library.RootPath);

        return new SongRefileResult(SongRefileOutcome.Refiled);
    }

    /// <summary>
    /// Recycles the <c>cover.jpg</c> of an album folder the move emptied, then the folder, then the
    /// artist folder it leaves empty — the same rule the Compact task's clean-up applies.
    /// </summary>
    private async Task CleanUpFolderAsync(Library library, string movedFrom, CancellationToken cancellationToken)
    {
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

    /// <summary>The history payload of a re-filed song: where it went, where it came from, and the album.</summary>
    private sealed record RefileHistoryData(string Path, string RefiledFrom, string Album);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Re-filing song {SongId} under its chosen album failed: {Message}")]
    private static partial void LogRefileFailed(ILogger logger, long songId, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "The lyrics sidecar {Path} was left behind: {Target} is where the lyrics are now")]
    private static partial void LogSidecarLeftBehind(ILogger logger, string path, string target);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not move the lyrics sidecar {Path} with its file: {Message}; it stays where it is")]
    private static partial void LogSidecarNotMoved(ILogger logger, string path, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The emptied album folder {Path} was left as it is: {Reason}")]
    private static partial void LogFolderNotCleaned(ILogger logger, string path, string reason);
}
