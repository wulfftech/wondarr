using Wondarr.Api.Paging;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Paging;
using Wondarr.Core.Profiles;
using Wondarr.Core.Sources;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Queue;

/// <summary>
/// The download queue in Lidarr's shape (ARCHITECTURE §5.6): <c>/api/v1/queue</c> lists the grabs,
/// <c>/api/v1/queue/status</c> summarises them for the UI's badge and <c>DELETE /api/v1/queue/{id}</c>
/// removes one. The rules live in <see cref="IQueueService"/> and <see cref="IQueueActions"/>.
/// </summary>
[ApiController]
[Route("api/v1/queue")]
public sealed class QueueController : ControllerBase
{
    /// <summary>What the response reports when the caller did not name a sort key.</summary>
    private const string DefaultSortKey = "createdAt";

    /// <summary>How far back the status counts a failed item as a current error.</summary>
    private static readonly TimeSpan RecentFailureWindow = TimeSpan.FromHours(1);

    /// <summary>How many of the newest finished items the status looks at for its error count.</summary>
    private const int RecentFailureScanSize = 200;

    private readonly IQueueService _queue;
    private readonly IQueueActions _actions;
    private readonly IQualityDefinitionService _qualities;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new instance of the <see cref="QueueController"/> class.</summary>
    /// <param name="queue">The download queue.</param>
    /// <param name="actions">The user's side of the queue: remove, blocklist, retry.</param>
    /// <param name="qualities">The quality ladder, for the name of each candidate's quality.</param>
    /// <param name="timeProvider">The clock the recent-failure window is measured against.</param>
    public QueueController(
        IQueueService queue,
        IQueueActions actions,
        IQualityDefinitionService qualities,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(qualities);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _queue = queue;
        _actions = actions;
        _qualities = qualities;
        _timeProvider = timeProvider;
    }

    /// <summary>Lists the grabs, newest first.</summary>
    /// <param name="includeFinished">
    /// Whether imported, failed and cancelled items are on the page too; the default shows only what
    /// is still in flight, which is what the UI's Activity page wants.
    /// </param>
    /// <param name="songId">Only the grabs made for this song; omitted means every song.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<PagingResource<QueueResource>>> GetQueue(
        bool includeFinished = false,
        long? songId = null,
        CancellationToken cancellationToken = default)
    {
        var paging = Request.ToPagingSpec();
        var page = await _queue.GetPageAsync(paging, includeFinished, songId, cancellationToken).ConfigureAwait(false);
        var qualityNames = await QualityNamesAsync(cancellationToken).ConfigureAwait(false);

        return Ok(page.ToPagingResource(paging, DefaultSortKey, item => item.ToResource(qualityNames)));
    }

    /// <summary>
    /// The queue's summary in Lidarr's <c>QueueStatusResource</c> shape, for the UI's badge and for
    /// the ecosystem tools that poll it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("status")]
    [Produces("application/json")]
    public async Task<ActionResult<QueueStatusResource>> GetStatus(CancellationToken cancellationToken)
    {
        var active = await _queue.GetActiveAsync(cancellationToken).ConfigureAwait(false);

        // Failed items are not "active", so the error count is read from the newest finished rows:
        // the queue page itself only shows what is in flight.
        var recent = await _queue.GetPageAsync(
            new PagingSpec(1, RecentFailureScanSize, DefaultSortKey, descending: true),
            includeFinished: true,
            cancellationToken).ConfigureAwait(false);

        var since = _timeProvider.GetUtcNow().UtcDateTime - RecentFailureWindow;
        var errors = recent.Records.Any(item =>
            item.State == QueueItemState.Failed
            && !string.IsNullOrWhiteSpace(item.Message)
            && item.FinishedAt >= since);

        // A peer that queued us is the one thing Lidarr calls a warning it can still resolve itself.
        var warnings = active.Any(item => item.State == QueueItemState.RemotelyQueued);

        return Ok(new QueueStatusResource(
            TotalCount: active.Count,
            Count: active.Count,
            UnknownCount: 0,
            Errors: errors,
            Warnings: warnings,
            UnknownErrors: false,
            UnknownWarnings: false));
    }

    /// <summary>Takes one grab out of the queue.</summary>
    /// <param name="id">The queue item id.</param>
    /// <param name="removeFromClient">
    /// Accepted for Lidarr parity. Wondarr always stops the grab at its source: leaving a transfer
    /// running that nothing is tracking anymore is never what the user wants.
    /// </param>
    /// <param name="blocklist">Whether the candidate is blocklisted so the song is not grabbed from it again.</param>
    /// <param name="skipRedownload">Whether a blocklisted candidate should not be replaced straight away.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>200 with an empty object, or 404 when the id is unknown.</returns>
    [HttpDelete("{id:long}")]
    [Produces("application/json")]
    public async Task<IActionResult> DeleteQueueItem(
        long id,
        bool removeFromClient = true,
        bool blocklist = false,
        bool skipRedownload = false,
        CancellationToken cancellationToken = default)
    {
        _ = removeFromClient;

        try
        {
            var removed = await _actions
                .RemoveAsync(id, blocklist, retry: blocklist && !skipRedownload, cancellationToken)
                .ConfigureAwait(false);

            return removed ? Ok(new { }) : NotFound();
        }
        catch (QueueItemBusyException)
        {
            return Problem(
                title: "The download is busy",
                detail: "The download is being imported; try again in a moment",
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>The quality ladder as an id → name lookup, in one query per request.</summary>
    private async Task<IReadOnlyDictionary<long, string>> QualityNamesAsync(CancellationToken cancellationToken)
    {
        var qualities = await _qualities.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return qualities.ToDictionary(quality => quality.Id, quality => quality.Name);
    }
}

/// <summary>
/// The queue summary, with Lidarr's field names so Homepage/Homarr widgets and other *arr clients can
/// read it. <c>unknownCount</c> and the <c>unknown*</c> flags stay 0/false: Wondarr has no
/// unmatched-item concept, but the shape has to be complete for the clients that read it.
/// </summary>
/// <param name="TotalCount">How many grabs are still in flight.</param>
/// <param name="Count">How many of them this answer covers — the same number, the summary is not paged.</param>
/// <param name="UnknownCount">Always 0.</param>
/// <param name="Errors">Whether a grab failed within the last hour with a message to show.</param>
/// <param name="Warnings">Whether a grab is waiting in a peer's own queue.</param>
/// <param name="UnknownErrors">Always false.</param>
/// <param name="UnknownWarnings">Always false.</param>
public sealed record QueueStatusResource(
    int TotalCount,
    int Count,
    int UnknownCount,
    bool Errors,
    bool Warnings,
    bool UnknownErrors,
    bool UnknownWarnings);
