using System.Globalization;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
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
public sealed record OrganizeResult(
    OrganizeFailure Failure,
    string? Error,
    string? FinalPath,
    string? RecycledPath,
    IReadOnlyDictionary<string, string> TagsWritten)
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

    private static readonly IReadOnlyDictionary<string, string> NothingWritten = new Dictionary<string, string>();

    private readonly ITagWriter _tagWriter;
    private readonly IFilePlacer _placer;
    private readonly ICoverFetcher _coverFetcher;
    private readonly IDiskOperations _disk;
    private readonly ILogger<LibraryOrganizer> _logger;

    /// <summary>Initialises a new instance of the <see cref="LibraryOrganizer"/> class.</summary>
    /// <param name="tagWriter">Writes the tag set into the file.</param>
    /// <param name="placer">Puts the file at its library path, recycling what it replaces.</param>
    /// <param name="coverFetcher">Downloads the cover to embed.</param>
    /// <param name="disk">Stages the copy of a kept source.</param>
    /// <param name="logger">The logger.</param>
    public LibraryOrganizer(
        ITagWriter tagWriter,
        IFilePlacer placer,
        ICoverFetcher coverFetcher,
        IDiskOperations disk,
        ILogger<LibraryOrganizer> logger)
    {
        ArgumentNullException.ThrowIfNull(tagWriter);
        ArgumentNullException.ThrowIfNull(placer);
        ArgumentNullException.ThrowIfNull(coverFetcher);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(logger);

        _tagWriter = tagWriter;
        _placer = placer;
        _coverFetcher = coverFetcher;
        _disk = disk;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OrganizeResult> OrganizeAsync(OrganizeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

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

            return new OrganizeResult(
                OrganizeFailure.None,
                null,
                placement.FinalPath,
                placement.RecycledPath,
                tagResult.Written);
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
}
