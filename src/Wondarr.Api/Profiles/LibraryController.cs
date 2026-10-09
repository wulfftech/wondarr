using Wondarr.Core.Compaction;
using Wondarr.Core.Profiles;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Profiles;

/// <summary>
/// The libraries songs are filed in. Libraries are listed, read, created, edited and deleted; the
/// rules live in <see cref="ILibraryService"/>.
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

    /// <summary>Creates a library.</summary>
    /// <param name="resource">The new library's values; the body's <c>id</c> is ignored.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>201 and the stored library; 400 when the library is not valid.</returns>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(LibraryResource), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LibraryResource>> CreateLibrary(
        [FromBody] LibraryResource resource,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await _libraries
                .CreateAsync(resource.ToLibrary(0), cancellationToken)
                .ConfigureAwait(false);

            return CreatedAtAction(nameof(GetLibrary), new { id = created.Id }, created.ToResource());
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

    /// <summary>Deletes a library that holds nothing and nothing files into.</summary>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>200; 400 naming the field that refuses, or 404 when the id is unknown.</returns>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteLibrary(long id, CancellationToken cancellationToken)
    {
        if (await _libraries.GetAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotFound();
        }

        try
        {
            await _libraries.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

            return Ok();
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

        var existing = await _libraries.GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            return NotFound();
        }

        try
        {
            var replacement = resource.ToLibrary(id);

            // An omitted replayGain keeps the stored switch: clients that predate it must not turn it off.
            if (resource.ReplayGain is null)
            {
                replacement.ReplayGain = existing.ReplayGain;
            }

            var updated = await _libraries.UpdateAsync(replacement, cancellationToken).ConfigureAwait(false);

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
