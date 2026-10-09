using Wondarr.Api.Paging;
using Wondarr.Core.Blocklisting;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Blocklist;

/// <summary>
/// The blocklist, in Lidarr's shape: <c>GET /api/v1/blocklist</c> and
/// <c>DELETE /api/v1/blocklist/{id}</c> (ARCHITECTURE §5.6).
/// </summary>
[ApiController]
[Route("api/v1/blocklist")]
public sealed class BlocklistController : ControllerBase
{
    /// <summary>What the response reports when the caller did not name a sort key.</summary>
    private const string DefaultSortKey = "date";

    private readonly IBlocklistService _blocklist;

    /// <summary>Initialises a new instance of the <see cref="BlocklistController"/> class.</summary>
    /// <param name="blocklist">The blocklist service.</param>
    public BlocklistController(IBlocklistService blocklist)
    {
        ArgumentNullException.ThrowIfNull(blocklist);

        _blocklist = blocklist;
    }

    /// <summary>Lists blocked candidates, newest first by default.</summary>
    /// <param name="songId">Only the entries recorded for this song; omitted means every entry.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<PagingResource<BlocklistResource>>> GetBlocklist(
        long? songId,
        CancellationToken cancellationToken)
    {
        var paging = Request.ToPagingSpec();
        var page = await _blocklist.GetPageAsync(paging, songId, cancellationToken).ConfigureAwait(false);

        return Ok(page.ToPagingResource(paging, DefaultSortKey, item => item.ToResource()));
    }

    /// <summary>Removes one entry from the blocklist.</summary>
    /// <param name="id">The blocklist row.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>200 when it was removed, 404 when there was no such row.</returns>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> DeleteBlocklistItem(long id, CancellationToken cancellationToken)
    {
        return await _blocklist.DeleteAsync(id, cancellationToken).ConfigureAwait(false)
            ? Ok()
            : NotFound();
    }
}
