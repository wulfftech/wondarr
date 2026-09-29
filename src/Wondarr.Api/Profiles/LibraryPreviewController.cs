using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
using Wondarr.Core.Profiles;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Profiles;

/// <summary>
/// The library editor's naming preview: <c>POST /api/v1/library/{id}/preview</c> renders the
/// library's template — or the one in the body, before it is saved — for a song, and answers with the
/// path the file would land on. The rules live in <see cref="NamingTemplate"/> and
/// <see cref="NamingValuesBuilder"/>.
/// </summary>
[ApiController]
[Route("api/v1/library")]
public sealed class LibraryPreviewController : ControllerBase
{
    /// <summary>
    /// The extension the sample renders with, and the one used for a song with no file yet: the
    /// preview answers "where would a FLAC land", which is what the preset templates describe.
    /// </summary>
    private const string SampleExtension = "flac";

    /// <summary>The quality the sample and a file-less song render as, by display name.</summary>
    private const string SampleQualityName = "FLAC";

    /// <summary>The sample song's duration in milliseconds (Daft Punk — Get Lucky).</summary>
    private const int SampleDurationMs = 369_000;

    /// <summary>The sample's bit rate in kbps — a CD-quality FLAC.</summary>
    private const int SampleBitrateKbps = 1025;

    /// <summary>The sample's sample rate in Hz.</summary>
    private const int SampleSampleRate = 44_100;

    /// <summary>The sample's bit depth.</summary>
    private const int SampleBitDepth = 16;

    /// <summary>The sample's channel count.</summary>
    private const int SampleChannels = 2;

    private readonly ILibraryService _libraries;
    private readonly ISongService _songs;
    private readonly IQualityDefinitionService _qualities;

    /// <summary>Initialises a new instance of the <see cref="LibraryPreviewController"/> class.</summary>
    /// <param name="libraries">The library service.</param>
    /// <param name="songs">The song service.</param>
    /// <param name="qualities">The quality ladder, so the rendered values name a real quality.</param>
    public LibraryPreviewController(
        ILibraryService libraries,
        ISongService songs,
        IQualityDefinitionService qualities)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(qualities);

        _libraries = libraries;
        _songs = songs;
        _qualities = qualities;
    }

    /// <summary>Renders a naming template for a song and answers with the path it builds.</summary>
    /// <param name="id">The library whose root the path is built under.</param>
    /// <param name="resource">The song and the template to try.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    [HttpPost("{id:long}/preview")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<LibraryPreviewResource>> Preview(
        long id,
        [FromBody] LibraryPreviewRequestResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var library = await _libraries.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (library is null)
        {
            return NotFound();
        }

        var template = string.IsNullOrWhiteSpace(resource.Template) ? library.NamingTemplate : resource.Template;

        // An invalid template is reported, not refused: the preview box in the UI is where the user
        // is still typing, so the answer is the list of what is wrong with it.
        var validation = NamingTemplate.Validate(template);
        if (!validation.IsValid)
        {
            return Ok(new LibraryPreviewResource(null, validation.Errors));
        }

        Song? song = null;
        if (resource.SongId is { } songId)
        {
            song = await _songs.GetAsync(songId, cancellationToken).ConfigureAwait(false);
            if (song is null)
            {
                return NotFound();
            }
        }

        var qualities = await _qualities.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var qualityById = qualities.ToDictionary(quality => quality.Id);

        Quality? sampleQuality = null;
        foreach (var quality in qualities)
        {
            if (quality.Name == SampleQualityName)
            {
                sampleQuality = quality;
                break;
            }
        }

        sampleQuality ??= qualities.Count > 0 ? qualities[0] : null;

        var rendered = song is null
            ? RenderSample(template, sampleQuality)
            : RenderSong(template, song, qualityById, sampleQuality);

        var root = library.RootPath.TrimEnd('/');
        var path = string.Concat(root, "/", rendered.Path, ".", rendered.Extension);

        return Ok(new LibraryPreviewResource(path, []));
    }

    /// <summary>The built-in sample: Daft Punk — Get Lucky, track 8 of 13 on its album.</summary>
    private static RenderedPath RenderSample(string template, Quality? quality)
    {
        var artist = new Artist { Name = "Daft Punk", SortName = "Daft Punk" };
        var song = new Song
        {
            Title = "Get Lucky",
            ArtistCredit = "Daft Punk",
            DurationMs = SampleDurationMs,
        };

        var album = new AlbumContext
        {
            Kind = AlbumContextKind.Album,
            AlbumTitle = "Random Access Memories",
            AlbumArtist = "Daft Punk",
            AlbumKey = "f9f5b1d3-1c0e-4d4a-9a3f-6db1a4b0f1e2",
            TrackNo = 8,
            DiscNo = 1,
            TotalTracks = 13,
            Date = "2013",
            OriginalDate = "2013",
        };

        return Render(template, song, album, artist, quality, SampleExtension, SourceTypes.Soulseek);
    }

    /// <summary>
    /// The values of one stored song. A song with no file yet is rendered as a FLAC: the preview is
    /// about the template, and the sample values are the ones the preset templates describe.
    /// </summary>
    private static RenderedPath RenderSong(
        string template,
        Song song,
        Dictionary<long, Quality> qualityById,
        Quality? sampleQuality)
    {
        var artist = song.PrimaryArtist ?? new Artist { Name = song.ArtistCredit, SortName = song.ArtistCredit };

        // A song is always filed under an album context once the album policy has run; before that,
        // render the pseudo-album it would get rather than refuse to answer.
        var album = song.AlbumContext ?? new AlbumContext
        {
            SongId = song.Id,
            Kind = AlbumContextKind.PseudoSingles,
            AlbumTitle = song.Title,
            AlbumArtist = artist.Name,
            AlbumKey = string.Empty,
            TrackNo = 1,
            DiscNo = 1,
        };

        var file = song.File;
        var quality = file is not null && qualityById.TryGetValue(file.QualityId, out var fileQuality)
            ? fileQuality
            : sampleQuality;

        var media = file is null
            ? new MediaInfo(
                SampleExtension,
                SampleExtension,
                SampleBitrateKbps,
                SampleSampleRate,
                SampleBitDepth,
                SampleChannels,
                song.DurationMs ?? SampleDurationMs,
                IsLossless: true,
                SizeBytes: 0)
            : new MediaInfo(
                file.Codec,
                file.Container,
                file.BitrateKbps,
                file.SampleRate,
                file.BitDepth,
                file.Channels,
                file.DurationMs ?? song.DurationMs ?? SampleDurationMs,
                quality?.Lossless ?? false,
                file.Size);

        var sourceType = file is null || string.IsNullOrWhiteSpace(file.SourceType)
            ? SourceTypes.Soulseek
            : file.SourceType;

        var extension = file is null || string.IsNullOrWhiteSpace(quality?.Codec)
            ? SampleExtension
            : quality!.Codec;

        return Render(template, song, album, artist, quality, extension, sourceType, media);
    }

    /// <summary>Builds the values and renders the template, with the sample's probe when there is none.</summary>
    private static RenderedPath Render(
        string template,
        Song song,
        AlbumContext album,
        Artist artist,
        Quality? quality,
        string extension,
        string sourceType,
        MediaInfo? media = null)
    {
        var effectiveMedia = media ?? new MediaInfo(
            SampleExtension,
            SampleExtension,
            SampleBitrateKbps,
            SampleSampleRate,
            SampleBitDepth,
            SampleChannels,
            SampleDurationMs,
            IsLossless: true,
            SizeBytes: 0);

        var effectiveQuality = quality ?? new Quality { Name = SampleQualityName, Codec = SampleExtension, Lossless = true };
        var values = NamingValuesBuilder.Build(song, album, artist, effectiveMedia, effectiveQuality, sourceType);

        return new RenderedPath(NamingTemplate.Render(template, values), extension);
    }

    /// <summary>A rendered relative path and the extension it was rendered for.</summary>
    /// <param name="Path">The relative path, with <c>/</c> separators.</param>
    /// <param name="Extension">The extension, without its dot.</param>
    private sealed record RenderedPath(string Path, string Extension);
}