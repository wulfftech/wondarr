using Wondarr.Api.Paging;
using Wondarr.Core.Domain;
using Wondarr.Core.History;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.History;

/// <summary>
/// The song lifecycle log, in Lidarr's shape: <c>/api/v1/history?songId=&amp;eventType=</c>
/// (ARCHITECTURE §5.6).
/// </summary>
[ApiController]
[Route("api/v1/history")]
public sealed class HistoryController : ControllerBase
{
    /// <summary>What the response reports when the caller did not name a sort key.</summary>
    private const string DefaultSortKey = "date";

    private readonly IHistoryService _history;

    /// <summary>Initialises a new instance of the <see cref="HistoryController"/> class.</summary>
    /// <param name="history">The history service.</param>
    public HistoryController(IHistoryService history)
    {
        ArgumentNullException.ThrowIfNull(history);

        _history = history;
    }

    /// <summary>Lists lifecycle events, newest first by default.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <remarks>
    /// <c>songId</c> and <c>eventType</c> are deliberately lenient: a value that cannot be read is
    /// treated as "no filter" rather than failing the whole request.
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<PagingResource<HistoryResource>>> GetHistory(CancellationToken cancellationToken)
    {
        var paging = Request.ToPagingSpec();
        var songId = ReadSongId(Request.Query["songId"]);
        var eventType = ReadEventType(Request.Query["eventType"]);

        var page = await _history.GetPageAsync(paging, songId, eventType, cancellationToken).ConfigureAwait(false);

        return Ok(page.ToPagingResource(paging, DefaultSortKey, item => item.ToResource()));
    }

    private static long? ReadSongId(string? text) =>
        long.TryParse(text, out var value) ? value : null;

    private static HistoryEventType? ReadEventType(string? text) =>
        Enum.TryParse<HistoryEventType>(text, ignoreCase: true, out var value) ? value : null;
}
