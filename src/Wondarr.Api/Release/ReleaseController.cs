using Wondarr.Core.Domain;
using Wondarr.Core.Profiles;
using Wondarr.Core.Searching;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Release;

/// <summary>
/// The interactive search (ARCHITECTURE §5.6): <c>GET /api/v1/release?songId=</c> runs a real search
/// and answers with every candidate, its score breakdown and its rejection reasons, and
/// <c>POST /api/v1/release</c> grabs the one the user picked. The search itself lives in
/// <see cref="ISongSearchService"/>.
/// </summary>
[ApiController]
[Route("api/v1/release")]
public sealed class ReleaseController : ControllerBase
{
    private readonly ISongSearchService _search;
    private readonly ISearchRunService _runs;
    private readonly IQualityDefinitionService _qualities;

    /// <summary>Initialises a new instance of the <see cref="ReleaseController"/> class.</summary>
    /// <param name="search">The search-and-grab service.</param>
    /// <param name="runs">The search-run store, which holds the candidates the run saw.</param>
    /// <param name="qualities">The quality ladder, for the name of each candidate's quality.</param>
    public ReleaseController(
        ISongSearchService search,
        ISearchRunService runs,
        IQualityDefinitionService qualities)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(qualities);

        _search = search;
        _runs = runs;
        _qualities = qualities;
    }

    /// <summary>
    /// Searches every source for a song and answers with everything the run saw. A search can take up
    /// to about a minute when the Soulseek budget is busy, so no timeout of our own is imposed: the
    /// caller's connection decides, and <see cref="HttpContext.RequestAborted"/> stops the work when
    /// it goes away.
    /// </summary>
    /// <param name="songId">The song to search for.</param>
    /// <param name="cancellationToken">Cancels the search when the caller goes away.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<InteractiveSearchResource>> Search(
        long songId,
        CancellationToken cancellationToken)
    {
        SongSearchResult result;

        try
        {
            result = await _search
                .SearchAsync(songId, SearchTrigger.Manual, grab: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SongNotFoundException)
        {
            return NotFound();
        }

        // Run 0 means no run was opened (the song already has a download in flight): there is nothing
        // stored to show, and the message says why.
        var releases = result.SearchRunId == 0
            ? []
            : await ReleasesAsync(result.SearchRunId, cancellationToken).ConfigureAwait(false);

        return Ok(new InteractiveSearchResource(result.SearchRunId, result.Outcome, result.Message, releases));
    }

    /// <summary>Grabs one candidate the interactive search returned.</summary>
    /// <param name="resource">The candidate to grab.</param>
    /// <param name="cancellationToken">Cancels the grab.</param>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<IActionResult> Grab([FromBody] GrabRequestResource resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            var queueItemId = await _search
                .GrabCandidateAsync(resource.CandidateId, 1, cancellationToken)
                .ConfigureAwait(false);

            return StatusCode(StatusCodes.Status201Created, new GrabResource(queueItemId));
        }
        catch (InvalidOperationException)
        {
            // GrabCandidateAsync's contract: the candidate id does not exist.
            return NotFound();
        }
        catch (AlreadyDownloadingException)
        {
            return Problem(
                title: "Already downloading",
                detail: "Song is already downloading",
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (GrabFailedException exception)
        {
            return Problem(
                title: "The grab failed",
                detail: exception.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>The releases of one run, in the order the engine judged them.</summary>
    private async Task<IReadOnlyList<ReleaseResource>> ReleasesAsync(long searchRunId, CancellationToken cancellationToken)
    {
        var candidates = await _runs.GetCandidatesAsync(searchRunId, cancellationToken).ConfigureAwait(false);
        var qualities = await _qualities.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var qualityNames = qualities.ToDictionary(quality => quality.Id, quality => quality.Name);

        return [.. candidates.Select(candidate => candidate.ToResource(qualityNames))];
    }
}