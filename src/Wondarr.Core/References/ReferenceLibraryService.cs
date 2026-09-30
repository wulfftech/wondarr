using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;

namespace Wondarr.Core.References;

/// <summary>Everything a caller may set on a reference library.</summary>
/// <param name="Name">The name the user gave the library; unique, case-insensitively.</param>
/// <param name="RootPath">The absolute path of the folder to walk.</param>
/// <param name="Mode">What the scan may do with the files it finds.</param>
/// <param name="LibraryId">The managed library adopted songs are filed under, or <see langword="null"/>.</param>
/// <param name="Enabled">Whether the daily scan covers this library.</param>
public sealed record ReferenceLibraryInput(
    string Name,
    string RootPath,
    ReferenceLibraryMode Mode,
    long? LibraryId,
    bool Enabled);

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
public sealed record ReferenceLibraryCounts(
    int Total,
    int Pending,
    int Identified,
    int Ambiguous,
    int Unmatched,
    int Adopted,
    int Unreadable,
    int Missing,
    int Skipped)
{
    /// <summary>The counts of a library that has never been scanned.</summary>
    public static readonly ReferenceLibraryCounts Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
}

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
    /// Deletes a reference library: its files and candidates go with it, and every song owned through
    /// one of its files becomes wanted again.
    /// </summary>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when a library was deleted.</returns>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);
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

    /// <summary>Initialises a new instance of the <see cref="ReferenceLibraryService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    public ReferenceLibraryService(WondarrDbContext database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
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
    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var library = await _database.ReferenceLibraries
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (library is null)
        {
            return false;
        }

        // The files and their candidates cascade in the database.
        _database.ReferenceLibraries.Remove(library);

        // The songs owned through this library's files are wanted again. Only a reference song_file is
        // ever removed here: a library file belongs to Wondarr and another reference file belongs to
        // another library, and both are left exactly as they are.
        var owned = await _database.SongFiles
            .Where(file => file.SourceType == SourceTypes.Reference && file.SourceRef != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        _database.SongFiles.RemoveRange(owned.Where(file => OwnedBy(file.SourceRef, id)));

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
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

        return rows
            .GroupBy(row => row.ReferenceLibraryId)
            .ToDictionary(
                group => group.Key,
                group => Build(group.ToDictionary(row => row.State, row => row.Count)));
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
