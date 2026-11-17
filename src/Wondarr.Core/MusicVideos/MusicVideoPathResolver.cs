using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;

namespace Wondarr.Core.MusicVideos;

/// <summary>Where a song's video goes, or why it cannot be filed yet.</summary>
/// <param name="VideoPath">The absolute path of the <c>.mkv</c>, or <see langword="null"/> when filing is deferred.</param>
/// <param name="RootPath">The folder whose marker was checked: the video root, or the library root under <c>beside_track</c>.</param>
/// <param name="DeferredReason">Why nothing may be written now, or <see langword="null"/> when <paramref name="VideoPath"/> is set.</param>
/// <param name="FellBack">Whether <c>beside_track</c> fell back to the video root because the song has no managed file.</param>
public sealed record MusicVideoPlacement(string? VideoPath, string? RootPath, string? DeferredReason, bool FellBack = false)
{
    /// <summary>Gets a value indicating whether the video may be written to <see cref="VideoPath"/>.</summary>
    public bool CanWrite => VideoPath is not null;

    internal static MusicVideoPlacement Deferred(string reason) => new(null, null, reason);
}

/// <summary>Decides where a song's music video is filed (ADR-0014).</summary>
public interface IMusicVideoPathResolver
{
    /// <summary>
    /// The path for a song's video under the configured layout. Under <c>folder</c> it is the naming template
    /// under the video root. Under <c>beside_track</c> it is the managed audio file's folder and name plus the
    /// suffix; a song with no managed file (a reference file, or none) falls back to <c>folder</c>. Either way the
    /// destination root's <c>.wondarr-library</c> marker is checked first, and a root that cannot be written to
    /// defers the filing with the reason.
    /// </summary>
    /// <param name="songId">The song.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    Task<MusicVideoPlacement> ResolveAsync(long songId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class MusicVideoPathResolver : IMusicVideoPathResolver
{
    /// <summary>The extension every video is filed under. Never <c>.webm</c> (ADR-0006, ADR-0008).</summary>
    public const string VideoExtension = ".mkv";

    private readonly WondarrDbContext _database;
    private readonly IOptionsMonitor<MusicVideoOptions> _options;
    private readonly IOptionsMonitor<ImportOptions> _importOptions;
    private readonly ILibraryRoots _roots;
    private readonly WondarrPaths? _paths;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoPathResolver"/> class.</summary>
    /// <param name="database">The database.</param>
    /// <param name="options">The <c>music_videos</c> settings.</param>
    /// <param name="importOptions">Where the recycle bin is, for the root rules.</param>
    /// <param name="roots">Checks the marker of the destination root.</param>
    /// <param name="paths">
    /// Where Wondarr's data directory is, for the root rules. The host always registers it; a host that does not (a
    /// test of something else) still builds the resolver, which then defers every filing into the video root.
    /// </param>
    public MusicVideoPathResolver(
        WondarrDbContext database,
        IOptionsMonitor<MusicVideoOptions> options,
        IOptionsMonitor<ImportOptions> importOptions,
        ILibraryRoots roots,
        WondarrPaths? paths = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(importOptions);
        ArgumentNullException.ThrowIfNull(roots);

        _database = database;
        _options = options;
        _importOptions = importOptions;
        _roots = roots;
        _paths = paths;
    }

    /// <inheritdoc />
    public async Task<MusicVideoPlacement> ResolveAsync(long songId, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        var song = await _database.Songs
            .AsNoTracking()
            .Include(candidate => candidate.PrimaryArtist)
            .Include(candidate => candidate.AlbumContext)
            .Include(candidate => candidate.File)
            .FirstOrDefaultAsync(candidate => candidate.Id == songId, cancellationToken)
            .ConfigureAwait(false);

        if (song is null)
        {
            return MusicVideoPlacement.Deferred("The song no longer exists.");
        }

        var fellBack = false;

        if (options.Layout == MusicVideoOptions.BesideTrackLayout)
        {
            var beside = await BesideTrackAsync(song, options, cancellationToken).ConfigureAwait(false);

            if (beside is not null)
            {
                return beside;
            }

            fellBack = true;
        }

        var placement = await InVideoRootAsync(song, options, cancellationToken).ConfigureAwait(false);

        return fellBack ? placement with { FellBack = true } : placement;
    }

    /// <summary>
    /// The audio file's folder and name plus the suffix, or <see langword="null"/> when the song has no managed
    /// audio file: a song with no file, a reference file, or a file outside every library.
    /// </summary>
    private async Task<MusicVideoPlacement?> BesideTrackAsync(Song song, MusicVideoOptions options, CancellationToken cancellationToken)
    {
        if (song.File is not { } audio
            || audio.SourceType == SourceTypes.Reference
            || !Path.IsPathFullyQualified(audio.Path))
        {
            return null;
        }

        var libraries = await _database.Libraries.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var references = await _database.ReferenceLibraries
            .AsNoTracking()
            .Select(reference => reference.RootPath)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var library = libraries.Find(candidate =>
                !string.IsNullOrWhiteSpace(candidate.RootPath) && PathRules.IsStrictlyInside(candidate.RootPath, audio.Path));

            // Wondarr never writes into a reference library, so a file there is not a managed file.
            if (library is null || references.Any(root => PathRules.IsInside(root, audio.Path)))
            {
                return null;
            }

            var status = await _roots.CheckAsync(library.RootPath, cancellationToken).ConfigureAwait(false);

            if (status != LibraryRootStatus.Ready)
            {
                return MusicVideoPlacement.Deferred(_roots.Describe(library, status));
            }

            var directory = Path.GetDirectoryName(audio.Path) ?? library.RootPath;
            var name = Path.GetFileNameWithoutExtension(audio.Path) + options.BesideTrackSuffix + VideoExtension;
            var path = Path.Combine(directory, name);

            return PathRules.IsStrictlyInside(library.RootPath, path)
                ? new MusicVideoPlacement(path, library.RootPath, null)
                : MusicVideoPlacement.Deferred("The song's audio file name does not make a usable video name.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return MusicVideoPlacement.Deferred("The song's audio file path is not usable.");
        }
    }

    private async Task<MusicVideoPlacement> InVideoRootAsync(Song song, MusicVideoOptions options, CancellationToken cancellationToken)
    {
        var root = options.RootPath;

        if (string.IsNullOrWhiteSpace(root))
        {
            return MusicVideoPlacement.Deferred("music_videos.root_path is not set, so there is nowhere to file the video.");
        }

        if (_paths is null)
        {
            return MusicVideoPlacement.Deferred("Wondarr's data directory is not known, so the video root cannot be checked.");
        }

        var problems = await MusicVideoRootRules
            .ProblemsAsync(root, _database, _paths, _importOptions, cancellationToken)
            .ConfigureAwait(false);

        if (problems.Count > 0)
        {
            return MusicVideoPlacement.Deferred(string.Join(' ', problems));
        }

        var status = await _roots.CheckAsync(root, cancellationToken).ConfigureAwait(false);

        if (status != LibraryRootStatus.Ready)
        {
            return MusicVideoPlacement.Deferred(DescribeRoot(root, status));
        }

        string relative;

        try
        {
            relative = RenderTemplate(options.NamingTemplate, song);
        }
        catch (ArgumentException exception)
        {
            return MusicVideoPlacement.Deferred($"music_videos.naming_template cannot be used: {exception.Message}");
        }

        if (relative.Length == 0)
        {
            return MusicVideoPlacement.Deferred("music_videos.naming_template renders no name for this song.");
        }

        try
        {
            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar) + VideoExtension));

            return PathRules.IsStrictlyInside(root, path)
                ? new MusicVideoPlacement(path, root, null)
                : MusicVideoPlacement.Deferred("music_videos.naming_template renders a path outside the video root.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return MusicVideoPlacement.Deferred("music_videos.naming_template renders a path that is not usable.");
        }
    }

    private static string DescribeRoot(string root, LibraryRootStatus status) => status switch
    {
        LibraryRootStatus.Missing =>
            $"The music video folder {root} does not exist. Videos wait until it is back; if the share is unmounted, mount it.",
        LibraryRootStatus.Unmarked =>
            $"The music video folder {root} has no {LibraryRoots.MarkerFileName} file, so it looks like an unmounted share. Videos wait. If this is the right folder, confirm it under Settings → Music videos.",
        LibraryRootStatus.Unresponsive =>
            $"The music video folder {root} did not answer within 5 seconds, so its share may be hung. Videos wait until it answers.",
        _ => $"The music video folder {root} is ready.",
    };

    /// <summary>
    /// Renders the video template by handing it to the organizer's own renderer, token by token, so a video is
    /// named and cleaned of illegal characters exactly as an audio file is.
    /// </summary>
    private static string RenderTemplate(string template, Song song)
    {
        var album = song.AlbumContext;
        var artist = song.PrimaryArtist?.Name is { Length: > 0 } name ? name : song.ArtistCredit;

        // The renderer reads [ ] as an optional group; in a video template they are plain characters.
        var translated = template
            .Replace("[", "[[", StringComparison.Ordinal)
            .Replace("]", "]]", StringComparison.Ordinal)
            .Replace("{Artist}", "{Artist Name}", StringComparison.Ordinal)
            .Replace("{AlbumArtist}", "{Album Artist Name}", StringComparison.Ordinal)
            .Replace("{Title}", "{Track Title}", StringComparison.Ordinal)
            .Replace("{Album}", "{Album Title}", StringComparison.Ordinal)
            .Replace("{Year}", "{Release Year}", StringComparison.Ordinal);

        var values = new NamingValues
        {
            ArtistName = artist,
            AlbumArtistName = string.IsNullOrWhiteSpace(album?.AlbumArtist) ? artist : album.AlbumArtist,
            AlbumTitle = album?.AlbumTitle,
            ReleaseYear = album is null || album.Kind == AlbumContextKind.PseudoSingles ? null : Year(album.Date),
            TrackTitle = song.Title,
        };

        return NamingTemplate.Render(translated, values, new NamingOptions(Extension: "mkv"));
    }

    private static int? Year(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
}
