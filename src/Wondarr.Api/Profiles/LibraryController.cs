using Wondarr.Core.Compaction;
using Wondarr.Core.Profiles;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Profiles;

/// <summary>
/// The libraries songs are filed in. Libraries are seeded and edited, never created or deleted here
/// (P1-08 is out of scope for that), so the endpoint is list, read and update. The rules live in
/// <see cref="ILibraryService"/>.
/// </summary>
[ApiController]
[Route("api/v1/library")]
public sealed class LibraryController : ControllerBase
{
    private readonly ILibraryService _libraries;
    private readonly ICompactPlanner _compaction;

    /// <summary>Initialises a new instance of the <see cref="LibraryController"/> class.</summary>
    /// <param name="libraries">The library service.</param>
    /// <param name="compaction">The Compact library task's planner, which this endpoint dry-runs.</param>
    public LibraryController(ILibraryService libraries, ICompactPlanner compaction)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(compaction);

        _libraries = libraries;
        _compaction = compaction;
    }

    /// <summary>Lists every library, ordered by id.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<List<LibraryResource>>> GetLibraries(CancellationToken cancellationToken)
    {
        var libraries = await _libraries.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return Ok(libraries.Select(library => library.ToResource()).ToList());
    }

    /// <summary>Reads one library.</summary>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("{id:long}")]
    [Produces("application/json")]
    public async Task<ActionResult<LibraryResource>> GetLibrary(long id, CancellationToken cancellationToken)
    {
        var library = await _libraries.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return library is null ? NotFound() : Ok(library.ToResource());
    }

    /// <summary>
    /// Plans the Compact library task for one library: which songs would change album, and where their
    /// files would go. A dry run — nothing is moved, tagged or written.
    /// </summary>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    [HttpGet("{id:long}/compact")]
    [Produces("application/json")]
    public async Task<ActionResult<CompactPlanResource>> GetCompactPlan(long id, CancellationToken cancellationToken)
    {
        try
        {
            var plan = await _compaction.PlanAsync(id, cancellationToken).ConfigureAwait(false);

            return Ok(plan.ToResource());
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Replaces a library's settings.</summary>
    /// <param name="id">The library id.</param>
    /// <param name="resource">The new values; the body's <c>id</c> must match the route or be 0.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPut("{id:long}")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<LibraryResource>> UpdateLibrary(
        long id,
        [FromBody] LibraryResource resource,
        CancellationToken cancellationToken)
    {
        if (resource.Id != 0 && resource.Id != id)
        {
            ModelState.AddModelError("id", $"The body id {resource.Id} does not match the route id {id}.");

            return ValidationProblem(ModelState);
        }

        if (await _libraries.GetAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotFound();
        }

        try
        {
            var updated = await _libraries.UpdateAsync(resource.ToLibrary(id), cancellationToken).ConfigureAwait(false);

            return Ok(updated.ToResource());
        }
        catch (ProfileValidationException exception)
        {
            foreach (var (property, message) in exception.Errors)
            {
                ModelState.AddModelError(property, message);
            }

            return ValidationProblem(ModelState);
        }
    }
}
