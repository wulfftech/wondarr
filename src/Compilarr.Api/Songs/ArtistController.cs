using Compilarr.Core.Songs;
using Microsoft.AspNetCore.Mvc;

namespace Compilarr.Api.Songs;

/// <summary>
/// The artist list the Library UI browses. Every rule lives in <see cref="IArtistService"/>; this
/// controller only maps artists onto <see cref="ArtistResource"/>.
/// </summary>
[ApiController]
[Route("api/v1/artist")]
public sealed class ArtistController : ControllerBase
{
    private readonly IArtistService _artists;

    /// <summary>Initialises a new instance of the <see cref="ArtistController"/> class.</summary>
    /// <param name="artists">The artist service.</param>
    public ArtistController(IArtistService artists)
    {
        ArgumentNullException.ThrowIfNull(artists);

        _artists = artists;
    }

    /// <summary>Lists every artist, sort name first, with how many songs credit them.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<List<ArtistResource>>> GetArtists(CancellationToken cancellationToken)
    {
        var artists = await _artists.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return Ok(artists.Select(artist => artist.ToResource()).ToList());
    }

    /// <summary>Reads one artist.</summary>
    /// <param name="id">The artist id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("{id:long}")]
    [Produces("application/json")]
    public async Task<ActionResult<ArtistResource>> GetArtist(long id, CancellationToken cancellationToken)
    {
        // The song count only exists on the summary list, so one artist is read from it rather than
        // from a second, count-less query.
        var artists = await _artists.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var artist = artists.FirstOrDefault(summary => summary.Artist.Id == id);

        return artist is null ? NotFound() : Ok(artist.ToResource());
    }
}