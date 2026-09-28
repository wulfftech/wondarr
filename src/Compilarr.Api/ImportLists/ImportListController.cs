using Compilarr.Core.ImportLists;
using Microsoft.AspNetCore.Mvc;

namespace Compilarr.Api.ImportLists;

/// <summary>
/// One import list: its header and the per-state counts the review screen opens with. The lines
/// themselves live on <see cref="ImportListItemController"/>.
/// </summary>
[ApiController]
[Route("api/v1/importlist")]
public sealed class ImportListController : ControllerBase
{
    private readonly IPasteListService _pastes;

    /// <summary>Initialises a new instance of the <see cref="ImportListController"/> class.</summary>
    /// <param name="pastes">The pasted-list pipeline.</param>
    public ImportListController(IPasteListService pastes)
    {
        ArgumentNullException.ThrowIfNull(pastes);

        _pastes = pastes;
    }

    /// <summary>Reads one list and how many of its lines are in each state.</summary>
    /// <param name="id">The list id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("{id:long}")]
    [Produces("application/json")]
    public async Task<ActionResult<ImportListResource>> GetImportList(long id, CancellationToken cancellationToken)
    {
        var summary = await _pastes.GetSummaryAsync(id, cancellationToken).ConfigureAwait(false);

        return summary is null ? NotFound() : Ok(summary.ToResource());
    }
}
