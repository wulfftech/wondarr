using System.Globalization;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Core.Compaction;

/// <summary>Works out what the Compact library task would do, without doing any of it (LIBRARY_OUTPUT.md §7.3).</summary>
public interface ICompactPlanner
{
    /// <summary>Plans the compaction of one library: which songs would change album, and where they would go.</summary>
    /// <param name="libraryId">The library to plan.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The plan. Nothing is moved, tagged or written.</returns>
    /// <exception cref="KeyNotFoundException">The library does not exist.</exception>
    Task<CompactPlan> PlanAsync(long libraryId, CancellationToken cancellationToken);
}

/// <summary>
/// The dry run behind the Compact library task: it re-plans every song of a library under its album
/// policy as if none were placed yet, and reports the songs whose album would change. It reads the
/// database and renders paths; it writes nothing at all, so the same plan can be shown, discarded or
/// executed later.
/// </summary>
public sealed class CompactPlanner : ICompactPlanner
{
    private readonly WondarrDbContext _database;
    private readonly ISongService _songs;

    /// <summary>Initialises a new instance of the <see cref="CompactPlanner"/> class.</summary>
    /// <param name="database">The Wondarr database, read for the library and the songs' files.</param>
    /// <param name="songs">The song service, which owns the re-planning itself.</param>
    public CompactPlanner(WondarrDbContext database, ISongService songs)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(songs);

        _database = database;
        _songs = songs;
    }

    /// <inheritdoc />
    public async Task<CompactPlan> PlanAsync(long libraryId, CancellationToken cancellationToken)
    {
        var replanned = await _songs.ReplanLibraryAsync(libraryId, cancellationToken).ConfigureAwait(false);

        var library = await _database.Libraries
            .AsNoTracking()
            .FirstAsync(candidate => candidate.Id == libraryId, cancellationToken)
            .ConfigureAwait(false);

        // Every song that has an album context counts towards the album totals, pinned ones included:
        // a pinned song stays exactly where it is, so its album is one of the ones that remain.
        var songs = await _database.Songs
            .AsNoTracking()
            .Include(song => song.PrimaryArtist)
            .Include(song => song.AlbumContext)
            .Include(song => song.File).ThenInclude(file => file!.Quality)
            .Where(song => song.LibraryId == libraryId && song.AlbumContext != null)
            .OrderBy(song => song.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var proposed = new Dictionary<long, AlbumContext>(replanned.Count);
        foreach (var song in replanned)
        {
            proposed[song.SongId] = song.Proposed;
        }

        var before = new HashSet<string>(StringComparer.Ordinal);
        var after = new HashSet<string>(StringComparer.Ordinal);
        var byId = new Dictionary<long, Song>(songs.Count);

        foreach (var song in songs)
        {
            var current = song.AlbumContext!;

            byId[song.Id] = song;
            before.Add(current.AlbumKey);
            after.Add(proposed.TryGetValue(song.Id, out var to) ? to.AlbumKey : current.AlbumKey);
        }

        var moves = new List<CompactMove>();

        foreach (var song in replanned)
        {
            var stored = byId[song.SongId];
            var current = stored.AlbumContext!;

            // A song the engine left on its own album is not an album move, even when it was given
            // another track number: it stays exactly as it is, tags and album alike. (Its file may
            // still be in the wrong folder; the template pass below looks after that.)
            if (string.Equals(current.AlbumKey, song.Proposed.AlbumKey, StringComparison.Ordinal))
            {
                continue;
            }

            var (fromPath, toPath) = PathsFor(stored, song.Proposed, library);

            moves.Add(new CompactMove(
                song.SongId,
                stored.Title,
                stored.ArtistCredit,
                AlbumOf(current),
                AlbumOf(song.Proposed),
                fromPath,
                toPath,
                song.Proposed));
        }

        // The template pass: a song that stays on its album but whose file is not where the library's
        // naming template puts it now (the template changed after the file was filed) moves to the
        // rendered path, album unchanged. Pinned songs are included: pinning fixes the album, not the folder.
        var albumMoved = moves.Select(move => move.SongId).ToHashSet();

        foreach (var stored in songs)
        {
            if (albumMoved.Contains(stored.Id))
            {
                continue;
            }

            var current = stored.AlbumContext!;
            var (templateFrom, templateTo) = PathsFor(stored, current, library);

            if (templateFrom is null || templateTo is null || IsInPlace(templateFrom, templateTo))
            {
                continue;
            }

            moves.Add(new CompactMove(
                stored.Id,
                stored.Title,
                stored.ArtistCredit,
                AlbumOf(current),
                AlbumOf(current),
                templateFrom,
                templateTo,
                current));
        }

        moves.Sort(CompareMoves);

        return new CompactPlan(libraryId, before.Count, after.Count, replanned.Count, moves);
    }

    /// <summary>
    /// The two paths a move would touch, or two <see langword="null"/>s when it would touch none. A
    /// reference file belongs to the user's own folder, not to the library, so it is never moved: the
    /// move only changes the song's album context.
    /// </summary>
    private static (string? FromPath, string? ToPath) PathsFor(Song song, AlbumContext proposed, Library library)
    {
        var file = song.File;

        if (file is null || string.Equals(file.SourceType, SourceTypes.Reference, StringComparison.Ordinal))
        {
            return (null, null);
        }

        var extension = Path.GetExtension(file.Path).TrimStart('.').ToLowerInvariant();

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

        var relative = LibraryOrganizer.RelativePathFor(
            song,
            proposed,
            song.PrimaryArtist,
            media,
            file.Quality,
            file.SourceType,
            library,
            extension);

        // Joined exactly as the placer joins it, so the plan shows the path the file will really get.
        var target = Path.GetFullPath(Path.Combine(
            library.RootPath,
            relative.Replace('/', Path.DirectorySeparatorChar) + "." + extension));

        return (file.Path, target);
    }

    /// <summary>
    /// Whether a file already is where the template puts it: at the rendered path, or at one of its
    /// <c>(2)</c>…<c>(99)</c> collision variants (the placer's names for a taken file name). Folder and
    /// file name are compared the way the file system compares them, so a second Compact right after
    /// the first plans nothing.
    /// </summary>
    internal static bool IsInPlace(string currentPath, string renderedPath)
    {
        var currentFolder = Path.GetDirectoryName(Path.GetFullPath(currentPath)) ?? string.Empty;
        var renderedFolder = Path.GetDirectoryName(renderedPath) ?? string.Empty;

        if (!PathRules.AreEqual(currentFolder, renderedFolder))
        {
            return false;
        }

        var currentName = Path.GetFileName(currentPath);
        var extension = Path.GetExtension(renderedPath);
        var name = Path.GetFileNameWithoutExtension(renderedPath);

        if (string.Equals(currentName, name + extension, PathRules.Comparison))
        {
            return true;
        }

        for (var suffix = 2; suffix <= FilePlacer.MaxCollisionSuffix; suffix++)
        {
            var variant = string.Concat(name, " (", suffix.ToString(CultureInfo.InvariantCulture), ")", extension);

            if (string.Equals(currentName, variant, PathRules.Comparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The plan's own ordering: from-album title, then to-album title, then song id.</summary>
    private static int CompareMoves(CompactMove left, CompactMove right)
    {
        var order = string.CompareOrdinal(left.From.AlbumTitle, right.From.AlbumTitle);

        if (order != 0)
        {
            return order;
        }

        order = string.CompareOrdinal(left.To.AlbumTitle, right.To.AlbumTitle);

        return order != 0 ? order : left.SongId.CompareTo(right.SongId);
    }

    private static CompactAlbum AlbumOf(AlbumContext context) =>
        new(context.AlbumKey, context.Kind, context.AlbumTitle, context.AlbumArtist);
}
