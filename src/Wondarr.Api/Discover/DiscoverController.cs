using Microsoft.AspNetCore.Mvc;
using Wondarr.Core.Discover;

namespace Wondarr.Api.Discover;

/// <summary>
/// Song suggestions from ListenBrainz, Last.fm and Deezer, in two modes: a radio seeded by one library
/// song, and a mix seeded by a random sample of the library. No MusicBrainz request is made here;
/// a suggestion is resolved when the user adds it.
/// </summary>
[ApiController]
[Route("api/v1/discover")]
public sealed class DiscoverController : ControllerBase
{
    private readonly IDiscoverService _discover;

    /// <summary>Initialises a new instance of the <see cref="DiscoverController"/> class.</summary>
    /// <param name="discover">The Discover service.</param>
    public DiscoverController(IDiscoverService discover)
    {
        ArgumentNullException.ThrowIfNull(discover);

        _discover = discover;
    }

    /// <summary>
    /// Suggests songs. With <paramref name="songId"/> it is a radio seeded by that song; otherwise it is a mix,
    /// and <paramref name="mixSeed"/> picks the sample (omit it for a new one; the answer says which it used).
    /// </summary>
    /// <param name="songId">The library song to seed a radio with.</param>
    /// <param name="mixSeed">The sample seed of a mix, to repeat an earlier sample.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<DiscoverResource>> GetSuggestions(
        long? songId,
        int? mixSeed,
        CancellationToken cancellationToken)
    {
        if (songId is not null && mixSeed is not null)
        {
            return Problem(
                title: "Give songId or mixSeed, not both",
                detail: "A radio is seeded by a song and a mix by a sample seed.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (songId is { } id)
        {
            var radio = await _discover.GetRadioAsync(id, cancellationToken).ConfigureAwait(false);

            return radio is null ? NotFound() : Ok(radio.ToResource());
        }

        var mix = await _discover.GetMixAsync(mixSeed, cancellationToken).ConfigureAwait(false);

        return Ok(mix.ToResource());
    }

    /// <summary>Lists the suggestions the user turned down, newest first: the undo list.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("dismissals")]
    [Produces("application/json")]
    public async Task<ActionResult<List<DiscoverDismissalResource>>> GetDismissals(CancellationToken cancellationToken)
    {
        var rows = await _discover.ListDismissalsAsync(cancellationToken).ConfigureAwait(false);

        return Ok(rows.Select(row => row.ToResource()).ToList());
    }

    /// <summary>Dismisses a suggestion so it is not suggested again. Dismissing one twice returns the first dismissal.</summary>
    /// <param name="resource">What to dismiss.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPost("dismissals")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<DiscoverDismissalResource>> AddDismissal(
        [FromBody] DiscoverDismissalInputResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            var row = await _discover
                .DismissAsync(
                    new DiscoverDismissalInput(resource.MbRecordingId, resource.Artist, resource.Title),
                    cancellationToken)
                .ConfigureAwait(false);

            return Ok(row.ToResource());
        }
        catch (DiscoverValidationException exception)
        {
            return Problem(
                title: "The dismissal is not valid",
                detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Undoes a dismissal, which brings the suggestion back.</summary>
    /// <param name="id">The dismissal id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpDelete("dismissals/{id:long}")]
    public async Task<IActionResult> DeleteDismissal(long id, CancellationToken cancellationToken)
    {
        var removed = await _discover.UndoDismissalAsync(id, cancellationToken).ConfigureAwait(false);

        return removed ? NoContent() : NotFound();
    }
}
