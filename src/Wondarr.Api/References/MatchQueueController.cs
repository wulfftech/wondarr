using System.Globalization;
using Wondarr.Api.Paging;
using Wondarr.Core.References;
using Wondarr.Core.Songs;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.References;

/// <summary>
/// The Match queue: the files identification could not settle, and the choices that settle them
/// (LIBRARY_OUTPUT §7.6). A choice is never applied to the file on disk — Wondarr only records which
/// song the file is. Text search inside the queue is not here: the UI searches with the existing
/// <c>POST /api/v1/song/lookup</c> and hands the id it picks back to <c>resolve</c>.
/// </summary>
[ApiController]
[Route("api/v1/matchqueue")]
public sealed class MatchQueueController : ControllerBase
{
    /// <summary>What the response reports when the caller did not name a sort key.</summary>
    private const string DefaultSortKey = "relativePath";

    private readonly IReferenceMatchService _matches;

    /// <summary>Initialises a new instance of the <see cref="MatchQueueController"/> class.</summary>
    /// <param name="matches">The Match queue service.</param>
    public MatchQueueController(IReferenceMatchService matches)
    {
        ArgumentNullException.ThrowIfNull(matches);

        _matches = matches;
    }

    /// <summary>Lists the files identification could not settle, best path first by default.</summary>
    /// <param name="referenceLibraryId">Only files of this reference library, when given.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<PagingResource<MatchQueueItemResource>>> GetQueue(
        [FromQuery] long? referenceLibraryId,
        CancellationToken cancellationToken)
    {
        // A work queue reads top to bottom: ascending unless the caller asks otherwise.
        var paging = Request.ToPagingSpec();

        if (!Request.Query.ContainsKey("sortDirection"))
        {
            paging = paging with { Descending = false };
        }

        var page = await _matches
            .GetQueueAsync(paging, referenceLibraryId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(page.ToPagingResource(paging, DefaultSortKey, row => row.ToResource()));
    }

    /// <summary>Settles one file by the user's choice.</summary>
    /// <param name="id">The reference file id.</param>
    /// <param name="request">
    /// Exactly one of <c>candidateRank</c>, <c>mbRecordingId</c>, <c>deezerId</c> or <c>skip</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the lookup and the write.</param>
    [HttpPost("{id:long}/resolve")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<MatchResolveResource>> Resolve(
        long id,
        [FromBody] MatchResolveRequestResource request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Problem(
                title: "Invalid choice",
                detail: "Exactly one of candidateRank, mbRecordingId, deezerId or skip must be given.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.MbRecordingId is { } mbRecordingId && !Guid.TryParse(mbRecordingId, out _))
        {
            ModelState.AddModelError("mbRecordingId", "mbRecordingId must be a MusicBrainz recording id.");

            return ValidationProblem(ModelState);
        }

        try
        {
            var result = await _matches
                .ResolveAsync(
                    id,
                    new ReferenceResolveChoice(
                        request.CandidateRank,
                        request.MbRecordingId,
                        request.DeezerId,
                        request.Skip),
                    cancellationToken)
                .ConfigureAwait(false);

            return Ok(new MatchResolveResource(
                MatchQueueResourceMapper.WireName(result.State),
                result.SongId,
                result.Message));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (SongNotFoundException)
        {
            return Problem(
                title: "No recording with that id",
                detail: "Neither MusicBrainz nor Deezer knows the id the request named.",
                statusCode: StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException exception)
        {
            return Problem(
                title: "The file cannot be resolved",
                detail: exception.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException exception)
        {
            return Problem(
                title: "Invalid choice",
                detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Accepts the best candidate of every listed ambiguous file.</summary>
    /// <param name="request">The reference file ids to accept.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    [HttpPost("bulk")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<MatchBulkResource>> AcceptBest(
        [FromBody] MatchBulkRequestResource request,
        CancellationToken cancellationToken)
    {
        if (request?.Ids is not { Count: > 0 } ids)
        {
            ModelState.AddModelError("ids", "ids must name at least one reference file.");

            return ValidationProblem(ModelState);
        }

        if (ids.Count > ReferenceMatchService.MaxBulkSize)
        {
            ModelState.AddModelError(
                "ids",
                string.Concat(
                    "ids must name at most ",
                    ReferenceMatchService.MaxBulkSize.ToString(CultureInfo.InvariantCulture),
                    " reference files."));

            return ValidationProblem(ModelState);
        }

        var result = await _matches.AcceptTopCandidatesAsync(ids, cancellationToken).ConfigureAwait(false);

        return Ok(new MatchBulkResource(result.Resolved, result.Failed, [.. result.Errors]));
    }
}
