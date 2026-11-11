using Wondarr.Core.Compaction;
using Wondarr.Core.Domain;
using Wondarr.Core.Organizer;
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
    private readonly ILibraryRoots _roots;

    /// <summary>Initialises a new instance of the <see cref="LibraryController"/> class.</summary>
    /// <param name="libraries">The library service.</param>
    /// <param name="compaction">The Compact library task's planner, which this endpoint dry-runs.</param>
    /// <param name="roots">Reports whether each root is ready, and marks one the user confirms.</param>
    public LibraryController(ILibraryService libraries, ICompactPlanner compaction, ILibraryRoots roots)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(compaction);
        ArgumentNullException.ThrowIfNull(roots);

        _libraries = libraries;
        _compaction = compaction;
        _roots = roots;
    }

    /// <summary>Lists every library, ordered by id.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<List<LibraryResource>>> GetLibraries(CancellationToken cancellationToken)
    {
        var libraries = await _libraries.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var resources = new List<LibraryResource>(libraries.Count);

        foreach (var library in libraries)
        {
            resources.Add(await ResourceAsync(library, cancellationToken).ConfigureAwait(false));
        }

        return Ok(resources);
    }

    /// <summary>Reads one library.</summary>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("{id:long}")]
    [Produces("application/json")]
    public async Task<ActionResult<LibraryResource>> GetLibrary(long id, CancellationToken cancellationToken)
    {
        var library = await _libraries.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return library is null ? NotFound() : Ok(await ResourceAsync(library, cancellationToken).ConfigureAwait(false));
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

    /// <summary>
    /// Says that the library's root folder is the right one: writes the <c>.wondarr-library</c> marker
    /// into it, so imports, searches and the health check treat it as ready. It is how a user accepts an
    /// empty or moved folder that Wondarr took for an unmounted share.
    /// </summary>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>200 and the library; 400 (<c>rootPath</c>) when the folder does not exist or cannot be written; 404 for an unknown id.</returns>
    [HttpPost("{id:long}/root/confirm")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(LibraryResource), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LibraryResource>> ConfirmLibraryRoot(long id, CancellationToken cancellationToken)
    {
        var library = await _libraries.GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (library is null)
        {
            return NotFound();
        }

        // Only a folder that exists is confirmed: a missing one is an unmounted share or a typo, and
        // creating it here would be exactly the write the marker exists to prevent.
        var status = await _roots.CheckAsync(library.RootPath, cancellationToken).ConfigureAwait(false);

        if (status == LibraryRootStatus.Unresponsive)
        {
            ModelState.AddModelError("rootPath", $"{library.RootPath} did not answer in time, so its share may be hung; try again when it answers.");

            return ValidationProblem(ModelState);
        }

        if (status == LibraryRootStatus.Missing)
        {
            ModelState.AddModelError("rootPath", $"{library.RootPath} does not exist; mount or create it first.");

            return ValidationProblem(ModelState);
        }

        try
        {
            _roots.Mark(library.RootPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ModelState.AddModelError("rootPath", $"the marker file could not be written into {library.RootPath}: {exception.Message}");

            return ValidationProblem(ModelState);
        }

        return Ok(await ResourceAsync(library, cancellationToken).ConfigureAwait(false));
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

            return CreatedAtAction(nameof(GetLibrary), new { id = created.Id }, await ResourceAsync(created, cancellationToken).ConfigureAwait(false));
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

            return Ok(await ResourceAsync(updated, cancellationToken).ConfigureAwait(false));
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

    /// <summary>The resource for <paramref name="library"/>, with its root checked on the disk now.</summary>
    private async Task<LibraryResource> ResourceAsync(Library library, CancellationToken cancellationToken) =>
        library.ToResource(await _roots.CheckAsync(library.RootPath, cancellationToken).ConfigureAwait(false));
}
