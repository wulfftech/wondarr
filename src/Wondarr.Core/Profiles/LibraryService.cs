using System.Text.Json;
using Wondarr.Core.Sources;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Wondarr.Core.Organizer;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Core.Profiles;

/// <summary>Reads and edits the libraries songs are filed in.</summary>
public interface ILibraryService
{
    /// <summary>Lists every library, ordered by id.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<Library>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Reads one library, or <see langword="null"/> when the id is unknown.</summary>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<Library?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Reads the library new songs go to, or <see langword="null"/> when none is marked default.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<Library?> GetDefaultAsync(CancellationToken cancellationToken);

    /// <summary>Validates and replaces the stored library with the same id.</summary>
    /// <param name="library">The new values; <see cref="EntityBase.Id"/> selects the row.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ProfileValidationException">The library is not valid, or its id is unknown.</exception>
    Task<Library> UpdateAsync(Library library, CancellationToken cancellationToken);

    /// <summary>Validates and stores a new library.</summary>
    /// <param name="library">The library to create; its id is ignored.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored library, with its id.</returns>
    /// <exception cref="ProfileValidationException">The library is not valid.</exception>
    Task<Library> CreateAsync(Library library, CancellationToken cancellationToken);

    /// <summary>Deletes a library that holds nothing and nothing files into.</summary>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ProfileValidationException">The library cannot be deleted, or its id is unknown.</exception>
    Task DeleteAsync(long id, CancellationToken cancellationToken);
}

/// <summary>
/// The business rules of a library: the name is unique, the root is an absolute path, the naming
/// template names the track, the album-policy threshold stays in range and the sidecar options are a
/// JSON object. Exactly one library is the default, so marking one clears the others in the same
/// transaction and the current default cannot be cleared on its own.
/// </summary>
public sealed class LibraryService : ILibraryService
{
    /// <summary>The token a naming template must contain to place the file name.</summary>
    public const string TrackTitleToken = "{Track Title}";

    /// <summary>The lowest accepted <see cref="Library.MinTracksPerRealAlbum"/>.</summary>
    public const int MinTracksPerRealAlbum = 1;

    /// <summary>The highest accepted <see cref="Library.MinTracksPerRealAlbum"/>.</summary>
    public const int MaxTracksPerRealAlbum = 10;

    private readonly WondarrDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="LibraryService"/> class.</summary>
    /// <param name="context">The database context.</param>
    public LibraryService(WondarrDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Library>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.Libraries
            .AsNoTracking()
            .OrderBy(library => library.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Library?> GetAsync(long id, CancellationToken cancellationToken) =>
        await _context.Libraries
            .AsNoTracking()
            .FirstOrDefaultAsync(library => library.Id == id, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Library?> GetDefaultAsync(CancellationToken cancellationToken) =>
        await _context.Libraries
            .AsNoTracking()
            .FirstOrDefaultAsync(library => library.IsDefault, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Library> UpdateAsync(Library library, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);

        var stored = await _context.Libraries
            .FirstOrDefaultAsync(candidate => candidate.Id == library.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ProfileValidationException([("id", $"Library {library.Id} does not exist.")]);

        var errors = await ValidateAsync(library, stored, cancellationToken).ConfigureAwait(false);
        errors.AddRange(await RootOverlapErrorsAsync(library.RootPath, library.Id, cancellationToken).ConfigureAwait(false));

        if (!PathRules.AreEqual(stored.RootPath, library.RootPath)
            && await _context.Songs
                .AsNoTracking()
                .AnyAsync(
                    // A reference file lives in the user's own folder, not under the root: it does
                    // not move with it.
                    song => song.LibraryId == library.Id && song.File != null && song.File.SourceType != SourceTypes.Reference,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            // The rows would say the files are where they no longer are: the move is the user's to
            // make first, song by song or by hand.
            errors.Add((
                "rootPath",
                "the library holds songs with files; move or delete them first, or move the folder yourself and point the library at it."));
        }

        if (errors.Count > 0)
        {
            throw new ProfileValidationException(errors);
        }

        if (library.IsDefault)
        {
            // Only one library is the default; the flag moves between rows in one SaveChanges.
            var previousDefaults = await _context.Libraries
                .Where(candidate => candidate.Id != library.Id && candidate.IsDefault)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var previous in previousDefaults)
            {
                previous.IsDefault = false;
            }
        }

        stored.Name = library.Name;
        stored.RootPath = library.RootPath;
        stored.Layout = library.Layout;
        stored.NamingTemplate = library.NamingTemplate;
        stored.SidecarOptions = library.SidecarOptions;

        // The policy is stored in its canonical form — version 2, every rule spelled out — so a
        // column written by one version reads the same in the next.
        stored.OutputPolicy = library.OutputPolicy is null
            ? null
            : LibraryOutputPolicy.Parse(library.OutputPolicy).ToJson();
        stored.AlbumPolicy = library.AlbumPolicy;
        stored.MinTracksPerRealAlbum = library.MinTracksPerRealAlbum;
        stored.PlexSectionId = library.PlexSectionId;
        stored.PlexLibraryPath = library.PlexLibraryPath;
        stored.ReplayGain = library.ReplayGain;
        stored.IsDefault = library.IsDefault;

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return stored;
    }

    /// <inheritdoc />
    public async Task<Library> CreateAsync(Library library, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);

        library.Id = 0;

        // An empty template takes the layout's preset, so a library created with nothing but a name
        // and a root still files its songs the way its layout says.
        if (string.IsNullOrWhiteSpace(library.NamingTemplate))
        {
            library.NamingTemplate = NamingTemplate.PresetTemplates[library.Layout];
        }

        // A fresh library: nothing is stored under this id yet, so nothing can collide with it.
        var errors = await ValidateAsync(library, new Library(), cancellationToken).ConfigureAwait(false);
        errors.AddRange(await RootOverlapErrorsAsync(library.RootPath, excludeId: null, cancellationToken).ConfigureAwait(false));

        if (errors.Count > 0)
        {
            throw new ProfileValidationException(errors);
        }

        if (library.IsDefault)
        {
            await ClearOtherDefaultsAsync(library.Id, cancellationToken).ConfigureAwait(false);
        }

        // The policy is stored in its canonical form — version 2, every rule spelled out.
        library.OutputPolicy = library.OutputPolicy is null
            ? null
            : LibraryOutputPolicy.Parse(library.OutputPolicy).ToJson();

        _context.Libraries.Add(library);

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return library;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var stored = await _context.Libraries
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ProfileValidationException([("id", $"Library {id} does not exist.")]);

        var errors = new List<(string Property, string Message)>();

        if (stored.IsDefault)
        {
            errors.Add(("isDefault", "the default library cannot be deleted; make another library the default first."));
        }

        var songs = await _context.Songs
            .AsNoTracking()
            .CountAsync(song => song.LibraryId == id, cancellationToken)
            .ConfigureAwait(false);

        if (songs > 0)
        {
            errors.Add(("songs", $"holds {songs} songs; move or delete them first"));
        }

        var lists = await _context.ImportLists
            .AsNoTracking()
            .Where(list => list.LibraryId == id)
            .Select(list => list.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (lists.Count > 0)
        {
            errors.Add(("importLists", $"import lists file into it: {string.Join(", ", lists)}"));
        }

        if (errors.Count > 0)
        {
            throw new ProfileValidationException(errors);
        }

        // Files are never touched: a library that holds none has none to leave behind.
        _context.Libraries.Remove(stored);

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clears the default flag on every library but <paramref name="id"/>.</summary>
    private async Task ClearOtherDefaultsAsync(long id, CancellationToken cancellationToken)
    {
        // Only one library is the default; the flag moves between rows in one SaveChanges.
        var previousDefaults = await _context.Libraries
            .Where(candidate => candidate.Id != id && candidate.IsDefault)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var previous in previousDefaults)
        {
            previous.IsDefault = false;
        }
    }

    /// <summary>
    /// Whether <paramref name="rootPath"/> equals, sits inside or contains the root of another
    /// library or a reference library. Full paths are compared case-insensitively, each with a
    /// trailing separator, so <c>/data/music2</c> beside <c>/data/music</c> does not count.
    /// </summary>
    private async Task<List<(string Property, string Message)>> RootOverlapErrorsAsync(
        string rootPath,
        long? excludeId,
        CancellationToken cancellationToken)
    {
        var errors = new List<(string Property, string Message)>();

        string normalized;

        try
        {
            normalized = NormalizedRoot(rootPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a usable path; the field's own rule (an absolute path) names it.
            return errors;
        }

        var libraries = await _context.Libraries
            .AsNoTracking()
            .Where(library => excludeId == null || library.Id != excludeId)
            .Select(library => new { library.Name, library.RootPath })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var library in libraries)
        {
            if (RootsOverlap(normalized, NormalizedRoot(library.RootPath)))
            {
                errors.Add(("rootPath", $"rootPath overlaps the library '{library.Name}'."));
            }
        }

        var references = await _context.ReferenceLibraries
            .AsNoTracking()
            .Select(reference => new { reference.Name, reference.RootPath })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var reference in references)
        {
            if (RootsOverlap(normalized, NormalizedRoot(reference.RootPath)))
            {
                errors.Add(("rootPath", $"rootPath overlaps the reference library '{reference.Name}'."));
            }
        }

        return errors;
    }

    /// <summary>Whether two roots are the same folder, or one holds the other.</summary>
    private static bool RootsOverlap(string first, string second) =>
        first.StartsWith(second, PathComparison)
        || second.StartsWith(first, PathComparison);

    /// <summary>Paths compare as the platform does: case-insensitive on Windows, exact elsewhere.</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>A full path with a trailing separator, so a prefix comparison cannot bite a sibling.</summary>
    private static string NormalizedRoot(string path) =>
        string.Concat(
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.DirectorySeparatorChar);

    /// <summary>Checks <paramref name="library"/> against the rules and returns every problem found.</summary>
    private async Task<List<(string Property, string Message)>> ValidateAsync(
        Library library,
        Library stored,
        CancellationToken cancellationToken)
    {
        var errors = new List<(string Property, string Message)>();

        if (string.IsNullOrWhiteSpace(library.Name))
        {
            errors.Add(("name", "name must not be empty."));
        }
        else if (await _context.Libraries
            .AnyAsync(
                // NOCASE is SQLite's ASCII case-insensitive collation: "Music" and "music" collide.
                candidate => candidate.Id != library.Id
                    && EF.Functions.Collate(candidate.Name, "NOCASE") == library.Name,
                cancellationToken)
            .ConfigureAwait(false))
        {
            errors.Add(("name", $"A library named '{library.Name}' already exists."));
        }

        if (!IsAbsolutePath(library.RootPath))
        {
            errors.Add(("rootPath", @"rootPath must be an absolute path, for example /data/music or D:\Music."));
        }

        if (string.IsNullOrWhiteSpace(library.NamingTemplate))
        {
            errors.Add(("namingTemplate", "namingTemplate must not be empty."));
        }
        else if (!library.NamingTemplate.Contains(TrackTitleToken, StringComparison.Ordinal))
        {
            errors.Add(("namingTemplate", $"namingTemplate must contain {TrackTitleToken}."));
        }

        if (library.MinTracksPerRealAlbum is < MinTracksPerRealAlbum or > MaxTracksPerRealAlbum)
        {
            errors.Add((
                "minTracksPerRealAlbum",
                $"minTracksPerRealAlbum must be between {MinTracksPerRealAlbum} and {MaxTracksPerRealAlbum}."));
        }

        if (!IsJsonObject(library.SidecarOptions))
        {
            errors.Add(("sidecarOptions", "sidecarOptions must be a JSON object."));
        }

        // The output policy is parsed, not just checked for shape: every failure names the JSON key
        // it came from, so the Settings UI can point at the offending line.
        try
        {
            LibraryOutputPolicy.Parse(library.OutputPolicy);
        }
        catch (ProfileValidationException exception)
        {
            errors.AddRange(exception.Errors);
        }

        if (!library.IsDefault && stored.IsDefault)
        {
            // Clearing the flag would leave the install without a default library, so the caller has
            // to point another library at it first.
            errors.Add(("isDefault", "isDefault cannot be cleared on the default library."));
        }

        return errors;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is rooted: a Unix path (the container's <c>/data/music</c>) or
    /// a Windows drive root, which development runs use.
    /// </summary>
    private static bool IsAbsolutePath(string path) =>
        !string.IsNullOrEmpty(path)
        && (path[0] == '/'
            || (path.Length >= 3
                && char.IsAsciiLetter(path[0])
                && path[1] == ':'
                && path[2] is '\\' or '/'));

    /// <summary>Whether <paramref name="json"/> parses as a JSON object.</summary>
    private static bool IsJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
