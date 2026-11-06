using Microsoft.AspNetCore.Mvc;
using Wondarr.Core.Songs;

namespace Wondarr.Api.Songs;

/// <summary>A song's file: taking a wrong one away so the song is searched for again.</summary>
[ApiController]
[Route("api/v1/song")]
public sealed class SongFileController : ControllerBase
{
    private readonly ISongFileRemover _remover;

    /// <summary>Initialises a new instance of the <see cref="SongFileController"/> class.</summary>
    /// <param name="remover">Removes the file.</param>
    public SongFileController(ISongFileRemover remover) => _remover = remover;

    /// <summary>
    /// Removes a song's file: it goes to the recycle bin with its lyrics sidecars, and the song is missing
    /// again, so the next search for it tries again. A reference library's file is the user's own and is
    /// never removed.
    /// </summary>
    /// <param name="id">The song id.</param>
    /// <param name="blocklist">Whether the download the file came from is blocklisted for the song (default true).</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// 200 with what was done; 404 when the song is unknown or has no file; 409 when the file is a reference
    /// library's, or a download, an import or a compaction has the song.
    /// </returns>
    [HttpDelete("{id:long}/file")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(SongFileRemovedResource), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SongFileRemovedResource>> RemoveFile(
        long id,
        [FromQuery] bool blocklist = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var removal = await _remover.RemoveAsync(id, blocklist, cancellationToken).ConfigureAwait(false);

            return removal is null
                ? NotFound()
                : Ok(new SongFileRemovedResource(removal.Path, removal.RecycledPath, removal.Blocklisted));
        }
        catch (SongFileIsReferenceException exception)
        {
            return Problem(title: "Your own copy", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (SongsBusyException exception)
        {
            return Problem(title: "Songs in the queue", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (SongsCompactingException exception)
        {
            return Problem(title: "Compaction in progress", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}

/// <summary>What removing a song's file did.</summary>
/// <param name="Path">Where the file was in the library.</param>
/// <param name="RecycledPath">Where it is now, in the recycle bin; null when it was already gone from disk.</param>
/// <param name="Blocklisted">Whether the download it came from is now blocklisted for the song.</param>
public sealed record SongFileRemovedResource(string Path, string? RecycledPath, bool Blocklisted);
