using System.Text.Json;
using Wondarr.Api.Middleware;
using Wondarr.Core.Albums;
using Wondarr.Core.Jobs;
using Wondarr.Core.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Albums;

/// <summary>
/// The album add over HTTP: search albums, list a release group's official releases, read a
/// release's tracklist with what the library already holds, and queue the <c>AddAlbum</c> command
/// that adds the tracks as songs. The add answers 202 as soon as the command is queued, so the
/// caller polls <c>GET /api/v1/command/{commandId}</c> while the MusicBrainz lookups pace themselves.
/// </summary>
[ApiController]
[MetadataUnavailableFilter]
[Route("api/v1/album")]
public sealed class AlbumController : ControllerBase
{
    /// <summary>Command bodies are camelCase, like the API's own resources.</summary>
    private static readonly JsonSerializerOptions CommandJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>The largest page a lookup answers with.</summary>
    private const int MaxLimit = 100;

    private readonly IAlbumService _albums;
    private readonly ICommandQueue _commands;

    /// <summary>Initialises a new instance of the <see cref="AlbumController"/> class.</summary>
    /// <param name="albums">The album add.</param>
    /// <param name="commands">The command queue the add runs on.</param>
    public AlbumController(IAlbumService albums, ICommandQueue commands)
    {
        ArgumentNullException.ThrowIfNull(albums);
        ArgumentNullException.ThrowIfNull(commands);

        _albums = albums;
        _commands = commands;
    }

    /// <summary>Searches MusicBrainz, and Deezer when MusicBrainz has nothing.</summary>
    /// <param name="term">What the user typed, for example <c>Queen - A Night at the Opera</c>.</param>
    /// <param name="limit">How many hits to return at most; 20 by default.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The hits, best first; 400 when the term is blank.</returns>
    [HttpGet("lookup")]
    [Produces("application/json")]
    public async Task<ActionResult<IReadOnlyList<AlbumSearchResultResource>>> Lookup(
        [FromQuery] string? term,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return Problem(
                title: "A search term is required",
                detail: "term must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var capped = limit is < 1 or > MaxLimit ? 20 : limit;

        try
        {
            var found = await _albums.SearchPartialAsync(term, capped, cancellationToken).ConfigureAwait(false);

            if (found.IsPartial)
            {
                // The list is still the body, so a client that never heard of the header keeps working.
                Response.Headers[MetadataUnavailableFilterAttribute.PartialHeader] =
                    MetadataUnavailableFilterAttribute.PartialValue(found.FailedProviders);
            }

            return Ok(found.Items.Select(hit => hit.ToResource()).ToList());
        }
        catch (ProvidersUnavailableException)
        {
            return MetadataUnavailableFilterAttribute.Unavailable(
                HttpContext,
                "Album search is unavailable",
                "MusicBrainz and Deezer did not answer; try again in a minute.");
        }
        catch (ArgumentException exception)
        {
            return InvalidRequest(exception);
        }
    }

    /// <summary>Lists every official release of a release group, with the default one marked.</summary>
    /// <param name="id">The release-group MBID.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The releases, best first; 400 when the id is blank.</returns>
    [HttpGet("releasegroup/{id}/releases")]
    [Produces("application/json")]
    public async Task<ActionResult<IReadOnlyList<AlbumReleaseResource>>> Releases(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            var releases = await _albums.GetReleasesAsync(id, cancellationToken).ConfigureAwait(false);

            return Ok(releases.Select(release => release.ToResource()).ToList());
        }
        catch (ArgumentException exception)
        {
            return InvalidRequest(exception);
        }
    }

    /// <summary>Reads an album's tracklist, with the song the library already holds beside every track.</summary>
    /// <param name="source">The provider the album lives on: <c>musicbrainz</c> or <c>deezer</c>.</param>
    /// <param name="id">The album's id on that provider: a release MBID or a Deezer album id.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The tracks in album order; 400 for an unknown source, 404 when the provider does not know the id.</returns>
    [HttpGet("{source}/{id}/tracks")]
    [Produces("application/json")]
    public async Task<ActionResult<IReadOnlyList<AlbumTrackResource>>> Tracks(
        string source,
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            var tracks = await _albums
                .GetTracklistAsync(new AlbumRef(source, id), cancellationToken)
                .ConfigureAwait(false);

            return tracks is null ? NotFound() : Ok(tracks.Select(track => track.ToResource()).ToList());
        }
        catch (ArgumentException exception)
        {
            return InvalidRequest(exception);
        }
    }

    /// <summary>Queues the command that adds an album's tracks as songs.</summary>
    /// <param name="resource">The album, the tracks to add and where the songs land.</param>
    /// <param name="cancellationToken">Cancels the enqueue.</param>
    /// <returns>202 and the command id; 400 when the album, library or profile is unusable.</returns>
    [HttpPost("add")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<AlbumAddAcceptedResource>> Add(
        [FromBody] AlbumAddResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (string.IsNullOrWhiteSpace(resource.Source) || string.IsNullOrWhiteSpace(resource.Id))
        {
            return Problem(
                title: "An album is required",
                detail: "source and id must name the album to add.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            // The library and profile are checked here, so the caller learns about a bad one at once
            // instead of when the command fails in the background.
            await _albums
                .ValidateAddAsync(resource.LibraryId, resource.QualityProfileId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return InvalidRequest(exception);
        }

        var command = await _commands
            .EnqueueAsync(
                AddAlbumCommandHandler.CommandName,
                JsonSerializer.Serialize(
                    new AddAlbumCommandBody(
                        AddAlbumCommandHandler.CommandName,
                        resource.Source,
                        resource.Id,
                        resource.TrackKeys,
                        resource.LibraryId,
                        resource.QualityProfileId,
                        resource.Monitored),
                    CommandJson),
                CommandTrigger.Manual,
                cancellationToken)
            .ConfigureAwait(false);

        return Accepted(new AlbumAddAcceptedResource(command.Id));
    }

    /// <summary>The album a song is filed under, when it is a real MusicBrainz release.</summary>
    /// <param name="id">The song id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The album reference; 404 when the song is unknown or filed under no release.</returns>
    [HttpGet("/api/v1/song/{id:long}/album")]
    [Produces("application/json")]
    public async Task<ActionResult<AlbumRefResource>> AlbumOfSong(
        long id,
        CancellationToken cancellationToken)
    {
        var album = await _albums.GetAlbumForSongAsync(id, cancellationToken).ConfigureAwait(false);

        return album is null ? NotFound() : Ok(new AlbumRefResource(album.Source, album.Id));
    }

    /// <summary>The body of the queued command, exactly as <c>AddAlbum</c> reads it.</summary>
    /// <param name="Name">The command name.</param>
    /// <param name="Source">The provider the album lives on.</param>
    /// <param name="Id">The album's id on that provider.</param>
    /// <param name="TrackKeys">The tracks to add, or <see langword="null"/> for all of them.</param>
    /// <param name="LibraryId">The library the songs land in.</param>
    /// <param name="QualityProfileId">The profile the songs are monitored against.</param>
    /// <param name="Monitored">Whether the songs are wanted.</param>
    private sealed record AddAlbumCommandBody(
        string Name,
        string Source,
        string Id,
        IReadOnlyList<string>? TrackKeys,
        long? LibraryId,
        long? QualityProfileId,
        bool? Monitored);

    /// <summary>A 400 that carries the argument's message, which names the field that was wrong.</summary>
    private ObjectResult InvalidRequest(ArgumentException exception) =>
        Problem(
            title: "Invalid request",
            detail: exception.Message,
            statusCode: StatusCodes.Status400BadRequest);
}
