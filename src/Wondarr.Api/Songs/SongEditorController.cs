using Wondarr.Core.Songs;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Songs;

/// <summary>
/// The Library page's mass editor, in Lidarr's shape (<c>/artist/editor</c>): one body names many
/// songs and what to change on all of them, or asks to delete them. The rules live in
/// <see cref="ISongEditorService"/>; this controller maps the bodies and the failures.
/// </summary>
[ApiController]
[Route("api/v1/song/editor")]
public sealed class SongEditorController : ControllerBase
{
    private const int MaxListedIds = 10;

    private readonly ISongEditorService _editor;

    /// <summary>Initialises a new instance of the <see cref="SongEditorController"/> class.</summary>
    /// <param name="editor">The editor service.</param>
    public SongEditorController(ISongEditorService editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        _editor = editor;
    }

    /// <summary>Changes many songs at once.</summary>
    /// <param name="resource">The songs and the changes; a missing field is left alone.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>200 and the updated songs plus any queued <c>MoveSongs</c> commands; 404 when a song is unknown; 400 when the body is unusable.</returns>
    [HttpPut]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(SongEditorResultResource), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SongEditorResultResource>> Edit(
        [FromBody] SongEditorResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            var result = await _editor
                .EditAsync(
                    new SongEditorRequest(
                        resource.SongIds,
                        resource.Monitored,
                        resource.QualityProfileId,
                        resource.LibraryId,
                        resource.Tags,
                        resource.ApplyTags),
                    cancellationToken)
                .ConfigureAwait(false);

            return Ok(new SongEditorResultResource(
                [.. result.Songs.Select(song => song.ToResource())],
                result.MoveCommandIds));
        }
        catch (SongsNotFoundException exception)
        {
            return NotFoundProblem(exception);
        }
        catch (ArgumentException exception)
        {
            return Invalid(exception);
        }
    }

    /// <summary>Deletes many songs at once. Files stay on disk, exactly as a single delete leaves them.</summary>
    /// <param name="resource">The songs to delete.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// 200 and how many were deleted; 404 (nothing deleted) when a song is unknown; 409 (nothing
    /// deleted) when a song is still downloading or importing.
    /// </returns>
    [HttpDelete]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(SongEditorDeletedResource), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SongEditorDeletedResource>> Delete(
        [FromBody] SongEditorDeleteResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            var deleted = await _editor.DeleteAsync(resource.SongIds, cancellationToken).ConfigureAwait(false);

            return Ok(new SongEditorDeletedResource(deleted));
        }
        catch (SongsNotFoundException exception)
        {
            return NotFoundProblem(exception);
        }
        catch (SongsBusyException exception)
        {
            var problem = Problem(
                title: "Songs in the queue",
                detail: exception.Message,
                statusCode: StatusCodes.Status409Conflict);

            if (problem.Value is ProblemDetails details)
            {
                details.Extensions["songIds"] = exception.BusyIds.Take(MaxListedIds).ToList();
            }

            return problem;
        }
        catch (ArgumentException exception)
        {
            return Invalid(exception);
        }
    }

    private ObjectResult NotFoundProblem(SongsNotFoundException exception)
    {
        var problem = Problem(
            title: "Unknown songs",
            detail: exception.Message,
            statusCode: StatusCodes.Status404NotFound);

        if (problem.Value is ProblemDetails details)
        {
            details.Extensions["songIds"] = exception.MissingIds.Take(MaxListedIds).ToList();
        }

        return problem;
    }

    private ObjectResult Invalid(ArgumentException exception) =>
        Problem(
            title: "Invalid request",
            detail: exception.Message,
            statusCode: StatusCodes.Status400BadRequest);
}

/// <summary>The mass editor's body. A missing field is left alone.</summary>
/// <param name="SongIds">The songs to change, 1 to 1,000, distinct.</param>
/// <param name="Monitored">The new monitored flag.</param>
/// <param name="QualityProfileId">The new quality profile.</param>
/// <param name="LibraryId">The library to move the songs to; queues <c>MoveSongs</c> for the songs not already there.</param>
/// <param name="Tags">The tags to apply; needs <paramref name="ApplyTags"/>.</param>
/// <param name="ApplyTags"><c>add</c>, <c>remove</c> or <c>replace</c>.</param>
public sealed record SongEditorResource(
    IReadOnlyList<long>? SongIds,
    bool? Monitored,
    long? QualityProfileId,
    long? LibraryId,
    IReadOnlyList<string>? Tags,
    string? ApplyTags);

/// <summary>The updated songs and the move commands to poll.</summary>
/// <param name="Songs">The updated songs.</param>
/// <param name="MoveCommandIds">The <c>MoveSongs</c> commands queued; poll <c>GET /api/v1/command/{id}</c>.</param>
public sealed record SongEditorResultResource(IReadOnlyList<SongResource> Songs, IReadOnlyList<long> MoveCommandIds);

/// <summary>The mass delete's body.</summary>
/// <param name="SongIds">The songs to delete, 1 to 1,000, distinct.</param>
public sealed record SongEditorDeleteResource(IReadOnlyList<long>? SongIds);

/// <summary>The mass delete's answer.</summary>
/// <param name="Deleted">How many songs were deleted.</param>
public sealed record SongEditorDeletedResource(int Deleted);
