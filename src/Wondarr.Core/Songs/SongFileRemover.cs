using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Compaction;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.Lyrics;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Songs;

/// <summary>What removing a song's file did.</summary>
/// <param name="Path">Where the file was in the library.</param>
/// <param name="RecycledPath">Where it is now, in the recycle bin, or <see langword="null"/> when it was already gone from disk.</param>
/// <param name="Blocklisted">Whether the download it came from is now blocklisted for the song.</param>
public sealed record SongFileRemoval(string Path, string? RecycledPath, bool Blocklisted);

/// <summary>
/// A song's file came from a reference library: the user's own file, or a copy adopted from it. Wondarr never
/// removes either, and the next adoption would only bring an adopted copy back.
/// </summary>
public sealed class SongFileIsReferenceException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="SongFileIsReferenceException"/> class.</summary>
    public SongFileIsReferenceException()
        : base("The song's file came from a reference library (your own file, or a copy adopted from it), so Wondarr does "
            + "not remove it. To have Wondarr find another one, use Skip or Replace in the Match queue.")
    {
    }
}

/// <summary>Takes a wrong file away from its song, so the song is wanted again.</summary>
public interface ISongFileRemover
{
    /// <summary>
    /// Moves the song's file, and its lyrics sidecars, to the recycle bin, forgets it, and leaves the song
    /// missing, so the next search for it tries again. With <paramref name="blocklist"/> the download the file
    /// came from is blocklisted for the song (a Soulseek one also by its folder and file name), so no search
    /// grabs it again.
    /// </summary>
    /// <param name="songId">The song.</param>
    /// <param name="blocklist">Whether to blocklist the download the file came from.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What was done, or <see langword="null"/> when the song is unknown or has no file.</returns>
    /// <exception cref="SongFileIsReferenceException">The file is a reference library's, or adopted from one.</exception>
    /// <exception cref="SongsBusyException">A download or an import for the song is still in flight.</exception>
    /// <exception cref="SongsCompactingException">A compaction has the song's file.</exception>
    Task<SongFileRemoval?> RemoveAsync(long songId, bool blocklist, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class SongFileRemover : ISongFileRemover
{
    /// <summary>The blocklist reason and the history message of a removed file.</summary>
    internal const string Reason = "Removed from the library as the wrong file";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WondarrDbContext _database;
    private readonly IRecycleBin _recycleBin;
    private readonly IPlexLibraryUpdater? _plex;
    private readonly ILogger<SongFileRemover> _logger;

    /// <summary>Initialises a new instance of the <see cref="SongFileRemover"/> class.</summary>
    /// <param name="database">The database.</param>
    /// <param name="recycleBin">Where the file goes.</param>
    /// <param name="logger">Logs each removal.</param>
    /// <param name="plex">Asks Plex to rescan the folder the file left; none in a test host.</param>
    public SongFileRemover(
        WondarrDbContext database,
        IRecycleBin recycleBin,
        ILogger<SongFileRemover> logger,
        IPlexLibraryUpdater? plex = null)
    {
        _database = database;
        _recycleBin = recycleBin;
        _logger = logger;
        _plex = plex;
    }

    /// <inheritdoc />
    public async Task<SongFileRemoval?> RemoveAsync(long songId, bool blocklist, CancellationToken cancellationToken)
    {
        var song = await _database.Songs
            .Include(candidate => candidate.File)
            .Include(candidate => candidate.Artists)
                .ThenInclude(credit => credit.Artist)
            .FirstOrDefaultAsync(candidate => candidate.Id == songId, cancellationToken)
            .ConfigureAwait(false);

        if (song?.File is not { } file)
        {
            return null;
        }

        if (file.SourceType is SourceTypes.Reference or SourceTypes.Adopted)
        {
            throw new SongFileIsReferenceException();
        }

        // The same refusals as deleting the song: an import in flight would land a file the record no longer
        // expects, and a compaction holds the file in its staging folder.
        if (await _database.QueueItems
                .AsNoTracking()
                .AnyAsync(item => item.SongId == songId && SongEditorService.ActiveStates.Contains(item.State), cancellationToken)
                .ConfigureAwait(false))
        {
            throw new SongsBusyException([songId]);
        }

        if (await _database.CompactMoves
                .AsNoTracking()
                .Where(CompactMoveRules.IsUnfinished)
                .AnyAsync(row => row.SongId == songId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new SongsCompactingException([songId]);
        }

        var library = await _database.Libraries
            .AsNoTracking()
            .FirstAsync(candidate => candidate.Id == song.LibraryId, cancellationToken)
            .ConfigureAwait(false);

        // A file someone already deleted by hand is simply forgotten.
        string? recycled = null;
        if (File.Exists(file.Path))
        {
            recycled = await _recycleBin.RecycleAsync(file.Path, library.RootPath, cancellationToken).ConfigureAwait(false);
        }

        foreach (var extension in new[] { LyricsSidecar.SyncedExtension, LyricsSidecar.PlainExtension })
        {
            var sidecar = System.IO.Path.ChangeExtension(file.Path, extension);

            if (File.Exists(sidecar))
            {
                await _recycleBin.RecycleAsync(sidecar, library.RootPath, cancellationToken).ConfigureAwait(false);
            }
        }

        var blocklisted = blocklist && await BlocklistSourceAsync(song, file, cancellationToken).ConfigureAwait(false);

        _database.History.Add(new HistoryItem
        {
            SongId = song.Id,
            EventType = HistoryEventType.Deleted,
            QualityId = file.QualityId,
            Data = JsonSerializer.Serialize(new RemovalHistoryData(Reason, file.Path, recycled, blocklisted), Json),
        });

        _database.SongFiles.Remove(file);

        // The history, the blocklist rows and the forgotten file in one save.
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (System.IO.Path.GetDirectoryName(file.Path) is { Length: > 0 } folder)
        {
            _plex?.RequestFolder(song.LibraryId, folder);
        }

        LogRemoved(_logger, song.Id, file.Path, recycled ?? "(already gone)", blocklisted);

        return new SongFileRemoval(file.Path, recycled, blocklisted);
    }

    /// <summary>
    /// Blocklists the candidate the file was imported from, and a Soulseek file also by its folder and file
    /// name. False when the file says nothing about where it came from (an adopted or a pre-0.1 file).
    /// </summary>
    private async Task<bool> BlocklistSourceAsync(Song song, SongFile file, CancellationToken cancellationToken)
    {
        if (ReadSource(file.SourceRef) is not { } source)
        {
            return false;
        }

        var candidate = await _database.Candidates
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == source.CandidateId, cancellationToken)
            .ConfigureAwait(false);

        // Old candidates are purged by housekeeping; a Soulseek key can be rebuilt from the reference itself.
        var key = candidate?.BlocklistKey
            ?? (file.SourceType == SourceTypes.Soulseek && source.Provider is { Length: > 0 } user && source.RemotePath is { Length: > 0 } path
                ? BlocklistKeys.Soulseek(user, path)
                : null);

        if (key is null)
        {
            return false;
        }

        var keys = new List<string> { key };

        if (file.SourceType == SourceTypes.Soulseek
            && BlocklistKeys.SoulseekFile(candidate?.RemotePath ?? source.RemotePath ?? string.Empty, song.Title) is { } fileKey)
        {
            keys.Add(fileKey);
        }

        var existing = await _database.Blocklist
            .Where(entry => entry.SongId == song.Id && keys.Contains(entry.BlocklistKey))
            .Select(entry => entry.BlocklistKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var missing in keys.Except(existing, StringComparer.Ordinal))
        {
            _database.Blocklist.Add(new BlocklistItem
            {
                SongId = song.Id,
                SourceType = file.SourceType,
                BlocklistKey = missing,
                Reason = Reason,
                ExpiresAt = null,
            });
        }

        // A file that passed the artist rule on a compilation path only on its title, and that the user says is
        // the wrong one, counts as a miss like one the fingerprint refused (MATCHING_ENGINE §6.2).
        if (candidate is not null
            && LearnedMisses.HasSnapshot(candidate)
            && await LearnedMisses.MissRowAsync(_database, song, candidate, file.SourceType, Reason, cancellationToken)
                .ConfigureAwait(false) is { } miss)
        {
            _database.Blocklist.Add(miss);
        }

        return true;
    }

    private static FileSource? ReadSource(string? sourceRef)
    {
        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FileSource>(sourceRef, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The part of an imported file's <c>source_ref</c> the blocklist needs (the import writes the rest).</summary>
    private sealed record FileSource(string? Provider, string? RemotePath, long CandidateId);

    /// <summary>The history payload of a removed file.</summary>
    private sealed record RemovalHistoryData(string Reason, string Path, string? RecycledPath, bool Blocklisted);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed the file of song {SongId}, {Path}, to {RecycledPath} (download blocklisted: {Blocklisted})")]
    private static partial void LogRemoved(ILogger logger, long songId, string path, string recycledPath, bool blocklisted);
}
