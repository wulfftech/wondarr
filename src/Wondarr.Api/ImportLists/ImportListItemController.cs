using Wondarr.Api.Paging;
using Wondarr.Core.Domain;
using Wondarr.Core.ImportLists;
using Wondarr.Core.Songs;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.ImportLists;

/// <summary>
/// The lines of the import lists: the review screen's list, and the two things a user can do with an
/// unresolved line — pick one of its candidates, or skip it.
/// </summary>
[ApiController]
[Route("api/v1/importlistitem")]
public sealed class ImportListItemController : ControllerBase
{
    /// <summary>What the response reports when the caller did not name a sort key.</summary>
    private const string DefaultSortKey = "line";

    private readonly IPasteListService _pastes;

    /// <summary>Initialises a new instance of the <see cref="ImportListItemController"/> class.</summary>
    /// <param name="pastes">The pasted-list pipeline.</param>
    public ImportListItemController(IPasteListService pastes)
    {
        ArgumentNullException.ThrowIfNull(pastes);

        _pastes = pastes;
    }

    /// <summary>Lists the lines of the import lists, filtered and paged.</summary>
    /// <param name="importListId">Only lines of this list, or <see langword="null"/> for every list.</param>
    /// <param name="state">Only lines in this state, or <see langword="null"/> for every state.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<PagingResource<ImportListItemResource>>> GetItems(
        long? importListId,
        ImportListItemState? state,
        CancellationToken cancellationToken)
    {
        var paging = Request.ToPagingSpec();
        var page = await _pastes
            .GetItemsAsync(paging, importListId, state, cancellationToken)
            .ConfigureAwait(false);

        return Ok(page.ToPagingResource(paging, DefaultSortKey, item => item.ToResource()));
    }

    /// <summary>Resolves one unresolved line to the candidate the user picked, and adds the song.</summary>
    /// <param name="id">The item id.</param>
    /// <param name="resource">Exactly one of the two candidate ids.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>200 and the item, 404 when the item or the id is unknown, 400 when both or neither id is given.</returns>
    [HttpPost("{id:long}/resolve")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<ImportListItemResource>> ResolveItem(
        long id,
        [FromBody] ImportListItemResolveResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var hasRecording = !string.IsNullOrWhiteSpace(resource.MbRecordingId);
        if (hasRecording == (resource.DeezerId is not null))
        {
            return Problem(
                title: "Exactly one candidate id is required",
                detail: "Give either mbRecordingId or deezerId, not both and not neither.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var item = await _pastes
                .ResolveItemAsync(
                    id,
                    hasRecording ? resource.MbRecordingId : null,
                    resource.DeezerId,
                    cancellationToken)
                .ConfigureAwait(false);

            return item is null ? NotFound() : Ok(item.ToResource());
        }
        catch (SongNotFoundException exception)
        {
            return Problem(
                title: "Song not found",
                detail: exception.Message,
                statusCode: StatusCodes.Status404NotFound);
        }
        catch (ArgumentException exception)
        {
            return Problem(
                title: "Invalid request",
                detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Skips one line, so nothing more is done with it.</summary>
    /// <param name="id">The item id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>200 and the item, or 404 when the id is unknown.</returns>
    [HttpPost("{id:long}/skip")]
    [Produces("application/json")]
    public async Task<ActionResult<ImportListItemResource>> SkipItem(long id, CancellationToken cancellationToken)
    {
        var item = await _pastes.SkipItemAsync(id, cancellationToken).ConfigureAwait(false);

        return item is null ? NotFound() : Ok(item.ToResource());
    }
}
