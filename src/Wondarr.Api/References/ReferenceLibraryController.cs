using System.Text.Json;
using Wondarr.Api.Commands;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.References;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.References;

/// <summary>
/// The folders Wondarr scans for songs it does not have to download (LIBRARY_OUTPUT §7.6). Unlike the
/// managed libraries, these are created and deleted here: they are folders the user already has, not
/// places Wondarr files songs into. The rules live in <see cref="IReferenceLibraryService"/>.
/// </summary>
[ApiController]
[Route("api/v1/referencelibrary")]
public sealed class ReferenceLibraryController : ControllerBase
{
    private readonly IReferenceLibraryService _libraries;
    private readonly ICommandQueue _commands;

    /// <summary>Initialises a new instance of the <see cref="ReferenceLibraryController"/> class.</summary>
    /// <param name="libraries">The reference library service.</param>
    /// <param name="commands">The command queue a scan now goes through.</param>
    public ReferenceLibraryController(IReferenceLibraryService libraries, ICommandQueue commands)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(commands);

        _libraries = libraries;
        _commands = commands;
    }

    /// <summary>Lists every reference library, ordered by id.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<List<ReferenceLibraryResource>>> GetLibraries(CancellationToken cancellationToken)
    {
        var libraries = await _libraries.ListAsync(cancellationToken).ConfigureAwait(false);

        return Ok(libraries.Select(library => library.ToResource()).ToList());
    }

    /// <summary>Reads one reference library.</summary>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("{id:long}")]
    [Produces("application/json")]
    public async Task<ActionResult<ReferenceLibraryResource>> GetLibrary(long id, CancellationToken cancellationToken)
    {
        var library = await _libraries.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return library is null ? NotFound() : Ok(library.ToResource());
    }

    /// <summary>Adds a reference library.</summary>
    /// <param name="resource">The values to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<ReferenceLibraryResource>> AddLibrary(
        [FromBody] ReferenceLibraryInputResource resource,
        CancellationToken cancellationToken)
    {
        if (Mode(resource) is not { } mode)
        {
            return ValidationProblem(ModelState);
        }

        try
        {
            var library = await _libraries
                .AddAsync(resource.ToInput(mode), cancellationToken)
                .ConfigureAwait(false);

            return CreatedAtAction(nameof(GetLibrary), new { id = library.Library.Id }, library.ToResource());
        }
        catch (ReferenceLibraryValidationException exception)
        {
            return ValidationProblem(exception);
        }
    }

    /// <summary>Replaces a reference library's settings.</summary>
    /// <param name="id">The library id.</param>
    /// <param name="resource">The new values.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPut("{id:long}")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<ReferenceLibraryResource>> UpdateLibrary(
        long id,
        [FromBody] ReferenceLibraryInputResource resource,
        CancellationToken cancellationToken)
    {
        if (Mode(resource) is not { } mode)
        {
            return ValidationProblem(ModelState);
        }

        try
        {
            var library = await _libraries
                .UpdateAsync(id, resource.ToInput(mode), cancellationToken)
                .ConfigureAwait(false);

            return library is null ? NotFound() : Ok(library.ToResource());
        }
        catch (ReferenceLibraryValidationException exception)
        {
            return ValidationProblem(exception);
        }
    }

    /// <summary>
    /// Deletes a reference library. Its files and candidates go with it, and every song that was owned
    /// through one of its files becomes wanted again. The files on disk are never touched; delete is
    /// followed by <c>200</c> with an empty body, as Lidarr and Sonarr do.
    /// </summary>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> DeleteLibrary(long id, CancellationToken cancellationToken) =>
        await _libraries.DeleteAsync(id, cancellationToken).ConfigureAwait(false) ? Ok() : NotFound();

    /// <summary>
    /// Queues a scan of one reference library: a walk of its folder, then identification of what the
    /// walk found.
    /// </summary>
    /// <remarks>
    /// The queue deduplicates by command name and body, so a scan of this library requested while a
    /// scan of the same library is queued or running returns that command instead of queueing a second
    /// one; a scan of another library is queued next to it.
    /// </remarks>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPost("{id:long}/scan")]
    [Produces("application/json")]
    public async Task<ActionResult<CommandResource>> ScanLibrary(long id, CancellationToken cancellationToken)
    {
        if (await _libraries.GetAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotFound();
        }

        // camelCase, as every command body on the wire is (and as the handler's own tests send it).
        var body = JsonSerializer.Serialize(new ReferenceLibraryScanBody(id), JsonSerializerOptions.Web);

        var record = await _commands
            .EnqueueAsync(ReferenceLibraryScanCommandHandler.CommandName, body, CommandTrigger.Manual, cancellationToken)
            .ConfigureAwait(false);

        return CreatedAtAction(
            nameof(CommandController.GetCommand),
            nameof(CommandController).Replace("Controller", string.Empty, StringComparison.Ordinal),
            new { id = record.Id },
            record.ToResource());
    }

    /// <summary>Reads the mode off a request body, or records a validation error when it names none.</summary>
    private ReferenceLibraryMode? Mode(ReferenceLibraryInputResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (ReferenceLibraryResourceMapper.ToMode(resource.Mode) is { } mode)
        {
            return mode;
        }

        ModelState.AddModelError(
            "mode",
            string.Concat(
                "mode must be '",
                ReferenceLibraryResourceMapper.ReferenceMode,
                "' or '",
                ReferenceLibraryResourceMapper.AdoptMode,
                "'."));

        return null;
    }

    /// <summary>Turns the service's validation problem into the RFC 7807 body the API returns.</summary>
    private ActionResult ValidationProblem(ReferenceLibraryValidationException exception)
    {
        ModelState.AddModelError(exception.Field, exception.Detail);

        return ValidationProblem(ModelState);
    }

    /// <summary>The body of a scan now: which library to scan.</summary>
    /// <param name="ReferenceLibraryId">The library to scan.</param>
    private sealed record ReferenceLibraryScanBody(long ReferenceLibraryId);
}
