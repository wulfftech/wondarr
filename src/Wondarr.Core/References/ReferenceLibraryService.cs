using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
using Wondarr.Core.Paging;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;

namespace Wondarr.Core.References;

/// <summary>Everything a caller may set on a reference library.</summary>
/// <param name="Name">The name the user gave the library; unique, case-insensitively.</param>
/// <param name="RootPath">The absolute path of the folder to walk.</param>
/// <param name="Mode">What the scan may do with the files it finds.</param>
/// <param name="LibraryId">The managed library adopted songs are filed under, or <see langword="null"/>.</param>
/// <param name="Enabled">Whether the daily scan covers this library.</param>
/// <param name="ReplaceBelowCutoff">
/// Whether a song held by one of the library's files may be replaced by a better copy while the file is
/// below the song's cutoff (DECISIONS build session 14 #32).
/// </param>
public sealed record ReferenceLibraryInput(
    string Name,
    string RootPath,
    ReferenceLibraryMode Mode,
    long? LibraryId,
    bool Enabled,
    bool ReplaceBelowCutoff = false);

/// <summary>One reference library with the state of its files, as the API and the UI read it.</summary>
/// <param name="Library">The stored row.</param>
/// <param name="Counts">How many of its files are in each state.</param>
public sealed record ReferenceLibrarySummary(ReferenceLibrary Library, ReferenceLibraryCounts Counts);

/// <summary>How many of a reference library's files are in each <see cref="ReferenceFileState"/>.</summary>
/// <param name="Total">Every file row of the library.</param>
/// <param name="Pending">Scanned, not yet identified.</param>
/// <param name="Identified">Identified as a song.</param>
/// <param name="Ambiguous">Ranked candidates the user has to choose between.</param>
/// <param name="Unmatched">Nothing was found.</param>
/// <param name="Adopted">Moved into a managed library.</param>
/// <param name="Unreadable">Not decodable audio.</param>
/// <param name="Missing">Gone from disk.</param>
/// <param name="Skipped">The user asked Wondarr to leave the file alone.</param>
/// <param name="LikelyYouTubeRips">Files still on disk that are most likely YouTube rips (lossy at 48 kHz).</param>
/// <param name="Replaced">Identified files whose song now holds a library file, so they can be deleted.</param>
public sealed record ReferenceLibraryCounts(
    int Total,
    int Pending,
    int Identified,
    int Ambiguous,
    int Unmatched,
    int Adopted,
    int Unreadable,
    int Missing,
    int Skipped,
    int LikelyYouTubeRips = 0,
    int Replaced = 0)
{
    /// <summary>The counts of a library that has never been scanned.</summary>
    public static readonly ReferenceLibraryCounts Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// An identified reference file whose song now holds a library file (DECISIONS build session 14 #32): a
/// download replaced it, or the song had a library copy first. Nothing uses the file any more, so the user
/// may delete it; Wondarr never does.
/// </summary>
/// <param name="Id">The reference file id.</param>
/// <param name="ReferenceLibraryId">The library the file belongs to.</param>
/// <param name="ReferenceLibraryName">That library's name.</param>
/// <param name="RelativePath">The path relative to the library root, separated by <c>/</c>.</param>
/// <param name="Path">The file's absolute path on this machine.</param>
/// <param name="LikelyYouTubeRip">Whether the file is most likely a YouTube rip.</param>
/// <param name="QualityName">The quality the file is rated at, or <see langword="null"/> when it was never measured.</param>
/// <param name="SongId">The song.</param>
/// <param name="ArtistCredit">The song's artist credit.</param>
/// <param name="Title">The song's title.</param>
/// <param name="LibraryPath">The library file the song holds now.</param>
/// <param name="LibraryQualityName">That file's quality.</param>
/// <param name="LibraryFileSince">When that file was imported, in UTC.</param>
/// <param name="LibraryCopyRatesLower">
/// Whether the library file rates below the reference file: the song had a worse library copy first, so
/// the user's file is the better one to keep.
/// </param>
public sealed record ReplacedReferenceFile(
    long Id,
    long ReferenceLibraryId,
    string ReferenceLibraryName,
    string RelativePath,
    string Path,
    bool LikelyYouTubeRip,
    string? QualityName,
    long SongId,
    string ArtistCredit,
    string Title,
    string LibraryPath,
    string LibraryQualityName,
    DateTime LibraryFileSince,
    bool LibraryCopyRatesLower);

/// <summary>
/// Thrown when a reference library fails validation. The field is the API resource field name
/// (<c>name</c>, <c>rootPath</c>, <c>mode</c>, <c>libraryId</c>), so a controller can turn it straight
/// into an RFC 7807 validation problem, the way <c>ProfileValidationException</c> is used.
/// </summary>
public sealed class ReferenceLibraryValidationException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="ReferenceLibraryValidationException"/> class.</summary>
    /// <param name="field">The API field the problem is about.</param>
    /// <param name="message">What is wrong with it.</param>
    public ReferenceLibraryValidationException(string field, string message)
        : base(string.Concat(field, ": ", message))
    {
        Field = field;
        Detail = message;
    }

    /// <summary>Initialises a new instance of the <see cref="ReferenceLibraryValidationException"/> class.</summary>
    /// <param name="field">The API field the problem is about.</param>
    /// <param name="message">What is wrong with it.</param>
    /// <param name="innerException">The failure behind this one, for example the unique-index violation.</param>
    public ReferenceLibraryValidationException(string field, string message, Exception innerException)
        : base(string.Concat(field, ": ", message), innerException)
    {
        Field = field;
        Detail = message;
    }

    /// <summary>Gets the API field the problem is about.</summary>
    public string Field { get; }

    /// <summary>Gets what is wrong with the field, without the field name in front of it.</summary>
    public string Detail { get; }
}

/// <summary>Manages the folders Wondarr scans for songs it does not have to download (LIBRARY_OUTPUT §7.6).</summary>
public interface IReferenceLibraryService
{
    /// <summary>Lists every reference library with its file counts, ordered by id.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>One summary per library.</returns>
    Task<IReadOnlyList<ReferenceLibrarySummary>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Reads one reference library with its file counts.</summary>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The summary, or <see langword="null"/> when the id is unknown.</returns>
    Task<ReferenceLibrarySummary?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Adds a reference library.</summary>
    /// <param name="input">The values to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored library.</returns>
    /// <exception cref="ReferenceLibraryValidationException">The input is not valid.</exception>
    Task<ReferenceLibrarySummary> AddAsync(ReferenceLibraryInput input, CancellationToken cancellationToken);

    /// <summary>Replaces a reference library's settings.</summary>
    /// <param name="id">The library id.</param>
    /// <param name="input">The new values.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The updated library, or <see langword="null"/> when the id is unknown.</returns>
    /// <exception cref="ReferenceLibraryValidationException">The input is not valid.</exception>
    Task<ReferenceLibrarySummary?> UpdateAsync(long id, ReferenceLibraryInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Counts the songs that deleting a library would affect: those owned through one of its files.
    /// </summary>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The count, or <see langword="null"/> when the library does not exist.</returns>
    Task<int?> CountOwnedSongsAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a reference library: its files and candidates go with it, and every song owned through
    /// one of its files loses that file. What happens to those songs is the caller's choice.
    /// </summary>
    /// <param name="id">The library id.</param>
    /// <param name="choice">What happens to the songs that were owned through this library.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>How many songs were owned through the library, or <see langword="null"/> when it does not exist.</returns>
    Task<int?> DeleteAsync(long id, ReferenceLibraryDeleteChoice choice, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the reference files a library copy has replaced: identified files whose song now holds a
    /// library file. Newest library file first.
    /// </summary>
    /// <param name="paging">The page and its size; the sort is fixed.</param>
    /// <param name="referenceLibraryId">Only files of this library, or <see langword="null"/> for all of them.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>One page of replaced files, and how many there are.</returns>
    Task<PagedResult<ReplacedReferenceFile>> GetReplacedAsync(
        PagingSpec paging,
        long? referenceLibraryId,
        CancellationToken cancellationToken);
}

/// <summary>What happens to the songs a deleted reference library owned.</summary>
public enum ReferenceLibraryDeleteChoice
{
    /// <summary>They are wanted again, and the caller starts a search for them at once.</summary>
    Search,

    /// <summary>They stay monitored and wanted; the scheduled missing search picks them up in its own time.</summary>
    Wanted,

    /// <summary>They are unmonitored, so nothing looks for them.</summary>
    Unmonitor,
}

/// <summary>
/// The rules of a reference library (LIBRARY_OUTPUT §7.6): the name is unique case-insensitively, the
/// root is an absolute path that neither sits inside a managed library's root nor contains one, the
/// library it adopts into exists, and adopt mode requires one. The folder need not exist when it is
/// saved — a share may be unmounted — so nothing here touches the disk.
/// </summary>
public sealed class ReferenceLibraryService : IReferenceLibraryService
{
    /// <summary>The JSON stored in <c>song_file.source_ref</c> of a reference file.</summary>
    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WondarrDbContext _database;
    private readonly IOptionsMonitor<ImportOptions> _importOptions;
    private readonly WondarrPaths _paths;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new instance of the <see cref="ReferenceLibraryService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="importOptions">The import settings, for the recycle bin.</param>
    /// <param name="paths">The resolved paths.</param>
    /// <param name="timeProvider">The clock a repointed file is stamped with, or <see langword="null"/> for the system's.</param>
    public ReferenceLibraryService(
        WondarrDbContext database,
        IOptionsMonitor<ImportOptions> importOptions,
        WondarrPaths paths,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(importOptions);
        ArgumentNullException.ThrowIfNull(paths);

        _database = database;
        _importOptions = importOptions;
        _paths = paths;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReferenceLibrarySummary>> ListAsync(CancellationToken cancellationToken)
    {
        var libraries = await _database.ReferenceLibraries
            .AsNoTracking()
            .OrderBy(library => library.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var counts = await CountsAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. libraries.Select(library => new ReferenceLibrarySummary(
                library,
                counts.TryGetValue(library.Id, out var libraryCounts) ? libraryCounts : ReferenceLibraryCounts.Empty)),
        ];
    }

    /// <inheritdoc />
    public async Task<ReferenceLibrarySummary?> GetAsync(long id, CancellationToken cancellationToken)
    {
        var library = await _database.ReferenceLibraries
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (library is null)
        {
            return null;
        }

        var counts = await CountsAsync(cancellationToken).ConfigureAwait(false);

        return new ReferenceLibrarySummary(
            library,
            counts.TryGetValue(id, out var libraryCounts) ? libraryCounts : ReferenceLibraryCounts.Empty);
    }

    /// <inheritdoc />
    public async Task<ReferenceLibrarySummary> AddAsync(
        ReferenceLibraryInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        await ValidateAsync(input, id: null, cancellationToken).ConfigureAwait(false);

        var library = new ReferenceLibrary();
        Apply(library, input);
        _database.ReferenceLibraries.Add(library);

        try
        {
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            // The name index is the only unique constraint here, and two callers can pass the check
            // above at the same time: the loser of that race reports it the same way as the winner.
            throw new ReferenceLibraryValidationException(
                "name",
                string.Concat("a reference library named '", input.Name, "' already exists."),
                exception);
        }

        return new ReferenceLibrarySummary(library, ReferenceLibraryCounts.Empty);
    }

    /// <inheritdoc />
    public async Task<ReferenceLibrarySummary?> UpdateAsync(
        long id,
        ReferenceLibraryInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var library = await _database.ReferenceLibraries
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (library is null)
        {
            return null;
        }

        await ValidateAsync(input, id, cancellationToken).ConfigureAwait(false);

        Apply(library, input);

        try
        {
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            throw new ReferenceLibraryValidationException(
                "name",
                string.Concat("a reference library named '", input.Name, "' already exists."),
                exception);
        }

        var counts = await CountsAsync(cancellationToken).ConfigureAwait(false);

        return new ReferenceLibrarySummary(
            library,
            counts.TryGetValue(id, out var libraryCounts) ? libraryCounts : ReferenceLibraryCounts.Empty);
    }

    /// <inheritdoc />
    public async Task<int?> CountOwnedSongsAsync(long id, CancellationToken cancellationToken)
    {
        var exists = await _database.ReferenceLibraries
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            return null;
        }

        var owned = await OwnedFilesAsync(id, track: false, cancellationToken).ConfigureAwait(false);
        var songIds = owned.Select(file => file.SongId).Distinct().ToList();

        // A song another library's Identified row also has is not affected: its file follows that row.
        var covered = await _database.ReferenceFiles
            .AsNoTracking()
            .Where(row => row.State == ReferenceFileState.Identified
                && row.ReferenceLibraryId != id
                && row.SongId != null
                && songIds.Contains(row.SongId.Value))
            .Select(row => row.SongId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return songIds.Count(songId => !covered.Contains(songId));
    }

    /// <inheritdoc />
    public async Task<int?> DeleteAsync(long id, ReferenceLibraryDeleteChoice choice, CancellationToken cancellationToken)
    {
        var library = await _database.ReferenceLibraries
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (library is null)
        {
            return null;
        }

        // The files and their candidates cascade in the database.
        _database.ReferenceLibraries.Remove(library);

        // The songs owned through this library's files lose them. Only a reference song_file is ever
        // removed here: a library file belongs to Wondarr and another reference file belongs to another
        // library, and both are left exactly as they are. A repoint or a release writes at once, and only
        // while the song's row is still the reference file read here (an import may have taken it over),
        // in a transaction committed with the library's delete.
        var owned = await OwnedFilesAsync(id, track: true, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var songIds = new List<long>();

        try
        {
            foreach (var file in owned)
            {
                // A second copy of the recording in another reference library keeps the song owned: the
                // song's file follows it, and the song does not become wanted.
                var other = await _database.ReferenceFiles
                    .Include(row => row.ReferenceLibrary)
                    .Where(row => row.SongId == file.SongId
                        && row.State == ReferenceFileState.Identified
                        && row.ReferenceLibraryId != id)
                    .OrderBy(row => row.Id)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (other is not null
                    && await ReferenceOwnership
                        .RepointAsync(_database, file.SongId, file.Path, other, other.ReferenceLibrary.RootPath, now, cancellationToken)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                if (await ReferenceOwnership
                    .ReleaseAsync(_database, file.SongId, file.Path, cancellationToken)
                    .ConfigureAwait(false))
                {
                    songIds.Add(file.SongId);
                }
            }

            if (choice == ReferenceLibraryDeleteChoice.Unmonitor && songIds.Count > 0)
            {
                var songs = await _database.Songs
                    .Where(song => songIds.Contains(song.Id))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (var song in songs)
                {
                    song.Monitored = false;
                }

                // The user's choice replaces any import list policy's unmonitor.
                await Songs.SongService.ClearPolicyFlagsAsync(_database, songIds, cancellationToken).ConfigureAwait(false);
            }

            await ReferenceOwnership.CommitAsync(_database, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ReferenceOwnership.RollbackAsync(_database).ConfigureAwait(false);

            throw;
        }

        return songIds.Count;
    }

    /// <summary>The reference <c>song_file</c> rows that point at one of this library's files.</summary>
    private async Task<List<SongFile>> OwnedFilesAsync(long referenceLibraryId, bool track, CancellationToken cancellationToken)
    {
        var query = track ? _database.SongFiles.AsQueryable() : _database.SongFiles.AsNoTracking();

        var candidates = await query
            .Where(file => file.SourceType == SourceTypes.Reference && file.SourceRef != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. candidates.Where(file => OwnedBy(file.SourceRef, referenceLibraryId))];
    }

    /// <summary>Whether a <c>source_ref</c> JSON blob names this reference library.</summary>
    private static bool OwnedBy(string? sourceRef, long referenceLibraryId)
    {
        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            return false;
        }

        try
        {
            return JsonSerializer.Deserialize<ReferenceSourceRef>(sourceRef, StoredJson)?.ReferenceLibraryId
                == referenceLibraryId;
        }
        catch (JsonException)
        {
            // A handle we cannot read names nothing: it is not this library's to delete.
            return false;
        }
    }

    /// <summary>How many files of each library are in each state.</summary>
    private async Task<Dictionary<long, ReferenceLibraryCounts>> CountsAsync(CancellationToken cancellationToken)
    {
        var rows = await _database.ReferenceFiles
            .AsNoTracking()
            .GroupBy(file => new { file.ReferenceLibraryId, file.State })
            .Select(group => new
            {
                group.Key.ReferenceLibraryId,
                group.Key.State,
                Count = group.Count(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rips = await _database.ReferenceFiles
            .AsNoTracking()
            .Where(file => file.LikelyYouTubeRip && file.State != ReferenceFileState.Missing)
            .GroupBy(file => file.ReferenceLibraryId)
            .Select(group => new { ReferenceLibraryId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.ReferenceLibraryId, group => group.Count, cancellationToken)
            .ConfigureAwait(false);

        var replaced = await Replaced(_database)
            .GroupBy(file => file.ReferenceLibraryId)
            .Select(group => new { ReferenceLibraryId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.ReferenceLibraryId, group => group.Count, cancellationToken)
            .ConfigureAwait(false);

        return rows
            .GroupBy(row => row.ReferenceLibraryId)
            .ToDictionary(
                group => group.Key,
                group => Build(group.ToDictionary(row => row.State, row => row.Count)) with
                {
                    LikelyYouTubeRips = rips.TryGetValue(group.Key, out var ripCount) ? ripCount : 0,
                    Replaced = replaced.TryGetValue(group.Key, out var replacedCount) ? replacedCount : 0,
                });
    }

    /// <inheritdoc />
    public async Task<PagedResult<ReplacedReferenceFile>> GetReplacedAsync(
        PagingSpec paging,
        long? referenceLibraryId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        var rows = Replaced(_database);

        if (referenceLibraryId is { } libraryId)
        {
            rows = rows.Where(row => row.ReferenceLibraryId == libraryId);
        }

        var query =
            from row in rows
            join file in _database.SongFiles.AsNoTracking() on row.SongId equals (long?)file.SongId
            select new
            {
                Row = row,
                row.ReferenceLibrary.Name,
                row.ReferenceLibrary.RootPath,
                row.Song!.ArtistCredit,
                row.Song.Title,
                File = file,
            };

        var totalRecords = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var page = await query
            .OrderByDescending(entry => entry.File.ImportedAt)
            .ThenBy(entry => entry.Row.Id)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var qualities = await _database.Qualities
            .AsNoTracking()
            .ToDictionaryAsync(quality => quality.Id, cancellationToken)
            .ConfigureAwait(false);

        var records = page
            .Select(entry =>
            {
                var rated = RatedQuality(entry.Row.Probe) is { } id && qualities.TryGetValue(id, out var quality) ? quality : null;
                var library = qualities.GetValueOrDefault(entry.File.QualityId);

                return new ReplacedReferenceFile(
                    entry.Row.Id,
                    entry.Row.ReferenceLibraryId,
                    entry.Name,
                    entry.Row.RelativePath,
                    ReferenceOwnership.AbsolutePath(entry.RootPath, entry.Row.RelativePath),
                    entry.Row.LikelyYouTubeRip,
                    rated?.Name,
                    entry.File.SongId,
                    entry.ArtistCredit,
                    entry.Title,
                    entry.File.Path,
                    library?.Name ?? string.Empty,
                    entry.File.ImportedAt,
                    rated is not null && library is not null && library.Rank < rated.Rank);
            })
            .ToList();

        return new PagedResult<ReplacedReferenceFile>(records, totalRecords);
    }

    /// <summary>
    /// The identified reference rows whose song now holds a library file: a download replaced the file,
    /// or the song had a library copy first. The scan deletes such a row once its file is gone.
    /// </summary>
    /// <param name="database">The Wondarr database.</param>
    /// <returns>The rows, untracked.</returns>
    internal static IQueryable<ReferenceFile> Replaced(WondarrDbContext database) =>
        database.ReferenceFiles
            .AsNoTracking()
            .Where(row => row.State == ReferenceFileState.Identified
                && row.SongId != null
                && database.SongFiles.Any(file => file.SongId == row.SongId && file.SourceType != SourceTypes.Reference));

    /// <summary>The quality a stored probe rates the file at, or <see langword="null"/> when it cannot be read.</summary>
    private static long? RatedQuality(string? probe)
    {
        if (string.IsNullOrWhiteSpace(probe))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MediaInfo>(probe, StoredJson) is { } media ? YouTubeRips.Rate(media) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Turns the per-state counts of one library into the nine numbers the API reports.</summary>
    private static ReferenceLibraryCounts Build(Dictionary<ReferenceFileState, int> counts)
    {
        var count = (ReferenceFileState state) => counts.TryGetValue(state, out var value) ? value : 0;

        return new ReferenceLibraryCounts(
            counts.Values.Sum(),
            count(ReferenceFileState.Pending),
            count(ReferenceFileState.Identified),
            count(ReferenceFileState.Ambiguous),
            count(ReferenceFileState.Unmatched),
            count(ReferenceFileState.Adopted),
            count(ReferenceFileState.Unreadable),
            count(ReferenceFileState.Missing),
            count(ReferenceFileState.Skipped));
    }

    /// <summary>Copies the caller's values onto the row.</summary>
    private static void Apply(ReferenceLibrary library, ReferenceLibraryInput input)
    {
        library.Name = input.Name.Trim();
        library.RootPath = input.RootPath.Trim();
        library.Mode = input.Mode;
        library.LibraryId = input.LibraryId;
        library.Enabled = input.Enabled;
        library.ReplaceBelowCutoff = input.ReplaceBelowCutoff;
    }

    /// <summary>
    /// Checks the input against every rule, or throws with the first problem found. The name is checked
    /// against the stored names, so the NOCASE index is mirrored here: "Music" and "music" collide.
    /// </summary>
    private async Task ValidateAsync(ReferenceLibraryInput input, long? id, CancellationToken cancellationToken)
    {
        var name = input.Name?.Trim();

        if (string.IsNullOrEmpty(name))
        {
            throw new ReferenceLibraryValidationException("name", "name must not be empty.");
        }

        if (await _database.ReferenceLibraries
            .AnyAsync(
                library => library.Name == name && (id == null || library.Id != id),
                cancellationToken)
            .ConfigureAwait(false))
        {
            throw new ReferenceLibraryValidationException(
                "name",
                string.Concat("a reference library named '", name, "' already exists."));
        }

        var rootPath = input.RootPath?.Trim();

        if (string.IsNullOrEmpty(rootPath))
        {
            throw new ReferenceLibraryValidationException("rootPath", "rootPath must not be empty.");
        }

        if (!IsAbsolute(rootPath))
        {
            throw new ReferenceLibraryValidationException("rootPath", "rootPath must be an absolute path.");
        }

        string normalized;

        try
        {
            normalized = Normalized(rootPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ReferenceLibraryValidationException("rootPath", "rootPath is not a usable path.");
        }

        // The recycle bin's cleanup deletes files older than N days anywhere under it, so a reference
        // library (the user's own collection) must never overlap it: be it, hold it or sit inside it.
        // The bin that is really used: the configured one made absolute, or <config>/recycle.
        var bin = _importOptions.CurrentValue.ResolveRecycleBinPath(_paths);

        if (PathRules.IsInside(bin, rootPath) || PathRules.IsInside(rootPath, bin))
        {
            throw new ReferenceLibraryValidationException(
                "rootPath",
                string.Concat(
                    "The reference library's folder overlaps the recycle bin (",
                    bin,
                    "); choose another folder."));
        }

        // Wondarr's own data directory (and the file system root) holds the API key and the database.
        // Only a folder being set is refused: a library already there from before this rule stays editable.
        var storedRoot = id is { } storedId
            ? await _database.ReferenceLibraries
                .AsNoTracking()
                .Where(library => library.Id == storedId)
                .Select(library => library.RootPath)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
            : null;

        if ((storedRoot is null || !PathRules.AreEqual(storedRoot, rootPath))
            && ProtectedPaths.Reason(rootPath, _paths) is { } reason)
        {
            throw new ReferenceLibraryValidationException(
                "rootPath",
                string.Concat("rootPath ", reason, ", where Wondarr keeps its API key, passwords and database."));
        }

        var managed = await _database.Libraries
            .AsNoTracking()
            .Select(library => new { library.Name, library.RootPath })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var library in managed)
        {
            var managedRoot = Normalized(library.RootPath);

            if (normalized.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new ReferenceLibraryValidationException(
                    "rootPath",
                    string.Concat("rootPath is inside the managed library '", library.Name, "'."));
            }

            if (managedRoot.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                throw new ReferenceLibraryValidationException(
                    "rootPath",
                    string.Concat("rootPath contains the managed library '", library.Name, "'."));
            }
        }

        if (input.LibraryId is { } libraryId)
        {
            if (!await _database.Libraries
                .AnyAsync(library => library.Id == libraryId, cancellationToken)
                .ConfigureAwait(false))
            {
                throw new ReferenceLibraryValidationException(
                    "libraryId",
                    string.Concat("Library ", libraryId.ToString(CultureInfo.InvariantCulture), " does not exist."));
            }
        }
        else if (input.Mode == ReferenceLibraryMode.Adopt)
        {
            throw new ReferenceLibraryValidationException(
                "libraryId",
                "libraryId is required when the mode is adopt.");
        }
    }

    /// <summary>
    /// Whether the caller named an absolute path. On Windows <see cref="Path.IsPathFullyQualified"/>
    /// refuses <c>/data/music</c> — rooted, but with no drive — although that is exactly the shape
    /// Wondarr's own config, its container volumes and its seeded library use, so a leading separator
    /// counts as absolute as well. A drive-relative path such as <c>C:music</c> is still refused.
    /// </summary>
    private static bool IsAbsolute(string path) =>
        Path.IsPathFullyQualified(path)
        || (path.Length > 0
            && (path[0] == Path.DirectorySeparatorChar || path[0] == Path.AltDirectorySeparatorChar));

    /// <summary>
    /// A path in one comparable form: made absolute, without a trailing separator, with a single
    /// separator appended so that <c>/data/music</c> is not a prefix of <c>/data/music2</c>.
    /// </summary>
    private static string Normalized(string path)
    {
        var full = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Concat(full, Path.DirectorySeparatorChar);
    }

    /// <summary>The <c>source_ref</c> of a reference file: which library and which row owns it.</summary>
    private sealed record ReferenceSourceRef(long ReferenceLibraryId, long ReferenceFileId);
}
