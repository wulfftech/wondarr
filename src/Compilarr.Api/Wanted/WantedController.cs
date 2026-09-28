using Compilarr.Api.Paging;
using Compilarr.Api.Songs;
using Compilarr.Core.Wanted;
using Microsoft.AspNetCore.Mvc;

namespace Compilarr.Api.Wanted;

/// <summary>
/// The wanted list, in Lidarr's shape: <c>/api/v1/wanted/missing</c> lists monitored songs without a
/// file and <c>/api/v1/wanted/cutoff</c> the ones whose file is below the profile's cutoff
/// (ARCHITECTURE §5.6).
/// </summary>
[ApiController]
[Route("api/v1/wanted")]
public sealed class WantedController : ControllerBase
{
    /// <summary>What the response reports when the caller did not name a sort key.</summary>
    private const string DefaultSortKey = "added";

    private readonly IWantedService _wanted;

    /// <summary>Initialises a new instance of the <see cref="WantedController"/> class.</summary>
    /// <param name="wanted">The wanted list service.</param>
    public WantedController(IWantedService wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);

        _wanted = wanted;
    }

    /// <summary>Lists monitored songs that have no file yet.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("missing")]
    [Produces("application/json")]
    public async Task<ActionResult<PagingResource<SongResource>>> GetMissing(CancellationToken cancellationToken)
    {
        var paging = Request.ToPagingSpec();
        var page = await _wanted.GetMissingAsync(paging, cancellationToken).ConfigureAwait(false);

        return Ok(page.ToPagingResource(paging, DefaultSortKey, song => song.ToResource()));
    }

    /// <summary>Lists monitored songs whose file is below the song's profile cutoff.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("cutoff")]
    [Produces("application/json")]
    public async Task<ActionResult<PagingResource<SongResource>>> GetCutoffUnmet(CancellationToken cancellationToken)
    {
        var paging = Request.ToPagingSpec();
        var page = await _wanted.GetCutoffUnmetAsync(paging, cancellationToken).ConfigureAwait(false);

        return Ok(page.ToPagingResource(paging, DefaultSortKey, song => song.ToResource()));
    }
}
