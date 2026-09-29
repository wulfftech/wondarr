using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tagging;

namespace Wondarr.Core.Importing;

/// <summary>Which step of <see cref="ILibraryOrganizer.OrganizeAsync"/> stopped the file.</summary>
public enum OrganizeFailure
{
    /// <summary>The file is in the library.</summary>
    None,

    /// <summary>The tag set could not be written; nothing was placed.</summary>
    Tagging,

    /// <summary>The file was tagged but could not be placed.</summary>
    Placement,
}

/// <summary>One file to file under a library: the song it is, where it is, and what it may replace.</summary>
/// <param name="Song">The song the file is, with its credits loaded.</param>
/// <param name="Album">The album context the song is filed under; the tags and the folder follow it.</param>
/// <param name="Credits">Every credited artist, in credit order.</param>
/// <param name="Library">The library the file goes into.</param>
/// <param name="SourcePath">The file as it is now.</param>
/// <param name="Extension">The extension the file is placed with, without the dot.</param>
/// <param name="Media">What the probe measured.</param>
/// <param name="Quality">The quality the file was matched to, for the naming tokens.</param>
/// <param name="SourceType">Where the file came from, one of <see cref="Sources.SourceTypes"/> or <c>reference</c>.</param>
/// <param name="AcoustId">The AcoustID the file was identified by, or <see langword="null"/>.</param>
/// <param name="KeepSource">
/// When set, the source is never modified: a copy of it is staged inside the library root, tagged and
/// placed, and the source stays exactly as it was (adoption from a reference library). When clear,
/// the source itself is tagged and moved (a finished download, which is Wondarr's own file).
/// </param>
/// <param name="ReplacesPath">The file this one replaces, recycled by the placer; <see langword="null"/> for none.</param>
public sealed record OrganizeRequest(
    Song Song,
    AlbumContext Album,
    IReadOnlyList<(Artist Artist, ArtistRole Role)> Credits,
    Library Library,
    string SourcePath,
    string Extension,
    MediaInfo Media,
    Quality Quality,
    string SourceType,
    string? AcoustId,
    bool KeepSource,
    string? ReplacesPath);

/// <summary>How organising one file ended.</summary>
/// <param name="Failure">The step that stopped the file, or <see cref="OrganizeFailure.None"/>.</param>
/// <param name="Error">The failing step's own message (the tag writer's or the placer's), or <see langword="null"/>.</param>
/// <param name="FinalPath">Where the file is now in the library, or where the placer left it.</param>
/// <param name="RecycledPath">Where the replaced file went, or <see langword="null"/>.</param>
/// <param name="TagsWritten">The fields the tag writer verified, for the file row's snapshot.</param>
/// <param name="CoverJpgPath">
/// The album folder's <c>cover.jpg</c>, when this call wrote it; <see langword="null"/> when the
/// layout has no album folder, the library does not want one, there was no JPEG cover, or one was
/// already there.
/// </param>
public sealed record OrganizeResult(
    OrganizeFailure Failure,
    string? Error,
    string? FinalPath,
    string? RecycledPath,
    IReadOnlyDictionary<string, string> TagsWritten,
    string? CoverJpgPath = null)
{
    /// <summary>Gets a value indicating whether the file is in the library.</summary>
    public bool Success => Failure == OrganizeFailure.None && FinalPath is not null;
}

/// <summary>
/// Files one audio file under a library: the full tag set with the cover, the path from the library's
/// naming template, and the placement over whatever it replaces (ARCHITECTURE §5.2 steps 9–10). The
/// import pipeline, adoption and the Compact library task all file songs through it, so every file
/// in a library is tagged and named by the same rules.
/// </summary>
public interface ILibraryOrganizer
{
    /// <summary>Tags, names and places one file.</summary>
    /// <param name="request">The file, the song it is and the library it goes into.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Where the file ended up, or which step stopped it.</returns>
    Task<OrganizeResult> OrganizeAsync(OrganizeRequest request, CancellationToken cancellationToken);
}

/// <summary>The one implementation of <see cref="ILibraryOrganizer"/>.</summary>
public sealed partial class LibraryOrganizer : ILibraryOrganizer
{
    /// <summary>
    /// The hidden folder under a library root where a kept source's copy is tagged before it is placed.
    /// Inside the root, so the final move is a rename on the same file system; hidden, so neither the
    /// reference scan nor Plex's scanner picks up a half-finished file.
    /// </summary>
    internal const string StagingFolderName = ".wondarr-staging";

    /// <summary>The name of the album folder's art sidecar.</summary>
    internal const string CoverJpgName = "cover.jpg";

    private static readonly IReadOnlyDictionary<string, string> NothingWritten = new Dictionary<string, string>();

    private readonly ITagWriter _tagWriter;
    private readonly IFilePlacer _placer;
    private readonly ICoverFetcher _coverFetcher;
    private readonly IDiskOperations _disk;
    private readonly WondarrDbContext _db;
    private readonly ICoverImageProcessor _coverProcessor;
    private readonly IOptionsMonitor<ImportOptions> _options;
    private readonly ILogger<LibraryOrganizer> _logger;

    /// <summary>Initialises a new instance of the <see cref="LibraryOrganizer"/> class.</summary>
    /// <param name="tagWriter">Writes the tag set into the file.</param>
    /// <param name="placer">Puts the file at its library path, recycling what it replaces.</param>
    /// <param name="coverFetcher">Downloads the cover to embed.</param>
    /// <param name="disk">Stages the copy of a kept source and writes the folder's cover.</param>
    /// <param name="db">Reads the album contexts of the folder the file is landing in.</param>
    /// <param name="coverProcessor">Bounds the cover before it is embedded.</param>
    /// <param name="options">The permissions applied to the cover Wondarr writes.</param>
    /// <param name="logger">The logger.</param>
    public LibraryOrganizer(
        ITagWriter tagWriter,
        IFilePlacer placer,
        ICoverFetcher coverFetcher,
        IDiskOperations disk,
        WondarrDbContext db,
        ICoverImageProcessor coverProcessor,
        IOptionsMonitor<ImportOptions> options,
        ILogger<LibraryOrganizer> logger)
    {
        ArgumentNullException.ThrowIfNull(tagWriter);
        ArgumentNullException.ThrowIfNull(placer);
        ArgumentNullException.ThrowIfNull(coverFetcher);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(coverProcessor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _tagWriter = tagWriter;
        _placer = placer;
        _coverFetcher = coverFetcher;
        _disk = disk;
        _db = db;
        _coverProcessor = coverProcessor;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OrganizeResult> OrganizeAsync(OrganizeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sidecar = LibrarySidecarOptions.Parse(request.Library.SidecarOptions);

        await AlignWithFolderAsync(request, cancellationToken).ConfigureAwait(false);

        var workingPath = request.SourcePath;
        string? staged = null;

        if (request.KeepSource)
        {
            // The tag writer replaces the file it is given, so a source that must stay untouched is
            // never handed to it: a copy is, and it is that copy which moves into the library. A hard
            // link would not do — writing the tags through it would rewrite the source as well.
            staged = Path.Combine(
                request.Library.RootPath,
                StagingFolderName,
                string.Concat(Guid.NewGuid().ToString("N"), ".", request.Extension));
            _disk.CreateDirectory(Path.GetDirectoryName(staged)!);
            _disk.CopyFile(request.SourcePath, staged);
            workingPath = staged;
        }

        try
        {
            // --- Tag ----------------------------------------------------------------------------
            var cover = await _coverFetcher
                .FetchAsync(request.Album.CoverUrl, cancellationToken)
                .ConfigureAwait(false);

            if (cover is not null)
            {
                // Bounded before it is embedded: the same bytes are what every file of the album
                // carries and what the folder's cover.jpg holds.
                cover = await _coverProcessor
                    .PrepareAsync(cover, sidecar.CoverMaxEdge, cancellationToken)
                    .ConfigureAwait(false);
            }

            var tags = TagSetBuilder.Build(request.Song, request.Album, request.Credits, request.AcoustId, cover);
            var tagResult = await _tagWriter.WriteAsync(workingPath, tags, cancellationToken).ConfigureAwait(false);

            if (!tagResult.Success)
            {
                return new OrganizeResult(OrganizeFailure.Tagging, tagResult.Error, null, null, NothingWritten);
            }

            // --- Name ---------------------------------------------------------------------------
            var template = string.IsNullOrWhiteSpace(request.Library.NamingTemplate)
                ? NamingTemplate.PresetTemplates[request.Library.Layout]
                : request.Library.NamingTemplate;

            var relative = NamingTemplate.Render(
                template,
                NamingValuesBuilder.Build(
                    request.Song,
                    request.Album,
                    PrimaryArtist(request.Song, request.Credits),
                    request.Media,
                    request.Quality,
                    request.SourceType),
                new NamingOptions(Extension: request.Extension));

            // --- Place --------------------------------------------------------------------------
            var placement = await _placer
                .PlaceAsync(
                    new PlacementRequest(
                        workingPath,
                        request.Library.RootPath,
                        relative,
                        request.Extension,
                        TransferMode.Move,
                        request.ReplacesPath),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!placement.Success || placement.FinalPath is null)
            {
                return new OrganizeResult(
                    OrganizeFailure.Placement,
                    placement.Error,
                    placement.FinalPath,
                    placement.RecycledPath,
                    tagResult.Written);
            }

            if (string.Equals(placement.FinalPath, staged, StringComparison.Ordinal))
            {
                staged = null;
            }

            // The file is in the library now, so the folder can take its art. A failure here is a
            // missing sidecar, never a failed import: the audio file stays exactly where it is.
            var coverJpg = WriteCoverJpg(request, placement.FinalPath, cover, sidecar);

            return new OrganizeResult(
                OrganizeFailure.None,
                null,
                placement.FinalPath,
                placement.RecycledPath,
                tagResult.Written,
                coverJpg);
        }
        finally
        {
            if (staged is not null)
            {
                DeleteStaged(staged);
            }
        }
    }

    /// <summary>The song's primary artist: the one it points at, or the first credit there is.</summary>
    internal static Artist PrimaryArtist(Song song, IReadOnlyList<(Artist Artist, ArtistRole Role)> credits)
    {
        foreach (var credit in credits)
        {
            if (credit.Artist.Id == song.PrimaryArtistId)
            {
                return credit.Artist;
            }
        }

        foreach (var credit in credits)
        {
            if (credit.Role == ArtistRole.Main)
            {
                return credit.Artist;
            }
        }

        return credits.Count > 0
            ? credits[0].Artist
            : throw new InvalidOperationException(
                string.Concat(
                    "Song ",
                    song.Id.ToString(CultureInfo.InvariantCulture),
                    " has no credited artist."));
    }

    /// <summary>
    /// Brings the file's album context in line with the folder it is landing in, so the folder's tags
    /// keep saying one thing (LIBRARY_OUTPUT.md §7.3–7.4). The reference is the lowest-id file of the
    /// same album key in the same library — a real file that is already there. The caller's own save
    /// persists the correction: <paramref name="request"/>'s album is the entity it tracks.
    /// </summary>
    private async Task AlignWithFolderAsync(OrganizeRequest request, CancellationToken cancellationToken)
    {
        var folder = _db.AlbumContexts
            .AsNoTracking()
            .Where(context => context.AlbumKey == request.Album.AlbumKey
                && context.SongId != request.Song.Id
                && context.Song.LibraryId == request.Library.Id
                && context.Song.File != null);

        var reference = await folder
            .OrderBy(context => context.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (reference is not null)
        {
            var changed = AlbumFolderConsistency.Align(request.Album, reference);

            if (changed.Count > 0)
            {
                LogFolderAligned(_logger, request.Song.Id, string.Join(", ", changed));
            }
        }

        // Reported, never repaired: the file keeps its number, and the user decides which of the two
        // is the mistake.
        var disc = request.Album.DiscNo ?? 1;
        var taken = await folder
            .AnyAsync(
                context => (context.DiscNo ?? 1) == disc && context.TrackNo == request.Album.TrackNo,
                cancellationToken)
            .ConfigureAwait(false);

        if (taken)
        {
            LogTrackNumberTaken(_logger, request.Song.Id, disc, request.Album.TrackNo);
        }
    }

    /// <summary>
    /// Writes the album folder's <c>cover.jpg</c> if this call is the first to put a file in it. An
    /// existing cover is never replaced: the first file placed into a folder decides its art.
    /// </summary>
    /// <param name="request">The file's request, which carries the library's layout.</param>
    /// <param name="finalPath">Where the file was placed; the cover goes beside it.</param>
    /// <param name="cover">The processed cover, or <see langword="null"/> when there was none.</param>
    /// <param name="sidecar">The library's sidecar options.</param>
    /// <returns>The path written, or <see langword="null"/> when nothing was written.</returns>
    private string? WriteCoverJpg(
        OrganizeRequest request,
        string finalPath,
        byte[]? cover,
        LibrarySidecarOptions sidecar)
    {
        if (cover is null
            || !sidecar.CoverJpg
            || !LibrarySidecarOptions.HasAlbumFolder(request.Library.Layout)
            || !IsJpeg(cover))
        {
            return null;
        }

        var folder = Path.GetDirectoryName(finalPath);

        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        var target = Path.Combine(folder, CoverJpgName);

        if (_disk.FileExists(target))
        {
            return null;
        }

        // A name of its own per attempt, so two files of one album landing at once never share a
        // half-written cover, and only this call's own file is ever removed.
        var partial = string.Concat(target, ".", Guid.NewGuid().ToString("N"), DiskOperations.PartialSuffix);

        try
        {
            _disk.WriteAllBytes(partial, cover);

            try
            {
                _disk.MoveFile(partial, target);
            }
            catch (IOException) when (_disk.FileExists(target))
            {
                // Another file of the same folder wrote the cover between the check and the move.
                DeletePartial(partial);

                return null;
            }
            catch
            {
                DeletePartial(partial);

                throw;
            }

            ApplyCoverPermissions(target);

            return target;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogCoverJpgNotWritten(_logger, target, exception.Message);

            return null;
        }
    }

    /// <summary>Gives the cover the configured file mode, as the placer does for the audio file.</summary>
    private void ApplyCoverPermissions(string target)
    {
        var options = _options.CurrentValue;

        if (options.SetPermissions)
        {
            _disk.SetUnixFileMode(target, ImportOptions.ParseMode(options.FileMode));
        }
    }

    /// <summary>Drops a half-written cover. Its own failure is not worth reporting.</summary>
    private void DeletePartial(string partial)
    {
        try
        {
            if (_disk.FileExists(partial))
            {
                _disk.DeleteFile(partial);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogPartialNotDeleted(_logger, partial, exception.Message);
        }
    }

    private static bool IsJpeg(byte[] image) =>
        image.Length >= 3 && image[0] == 0xFF && image[1] == 0xD8 && image[2] == 0xFF;

    /// <summary>
    /// Removes a staged copy that did not make it into the library. After a successful placement the
    /// copy has moved, so there is nothing left to remove.
    /// </summary>
    private void DeleteStaged(string staged)
    {
        try
        {
            if (_disk.FileExists(staged))
            {
                _disk.DeleteFile(staged);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogStagedNotDeleted(_logger, staged, exception.Message);
        }
    }

    [LoggerMessage(EventId = 2301, Level = LogLevel.Warning, Message = "Could not remove the staged copy {Path}: {Reason}")]
    private static partial void LogStagedNotDeleted(ILogger logger, string path, string reason);

    [LoggerMessage(
        EventId = 2302,
        Level = LogLevel.Warning,
        Message = "Song {SongId} was filed against a folder that disagrees with it; took the folder's: {Fields}")]
    private static partial void LogFolderAligned(ILogger logger, long songId, string fields);

    [LoggerMessage(
        EventId = 2303,
        Level = LogLevel.Warning,
        Message = "Song {SongId} holds (disc {Disc}, track {Track}), which another file of the same folder already holds.")]
    private static partial void LogTrackNumberTaken(ILogger logger, long songId, int disc, int? track);

    [LoggerMessage(EventId = 2304, Level = LogLevel.Warning, Message = "Could not write {Path}: {Reason}")]
    private static partial void LogCoverJpgNotWritten(ILogger logger, string path, string reason);

    [LoggerMessage(EventId = 2305, Level = LogLevel.Warning, Message = "Could not remove the half-written cover {Path}: {Reason}")]
    private static partial void LogPartialNotDeleted(ILogger logger, string path, string reason);
}
