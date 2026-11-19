using Microsoft.AspNetCore.Mvc;
using Wondarr.Core.FileSystem;

namespace Wondarr.Api.FileSystem;

/// <summary>A folder, as the folder picker shows it.</summary>
/// <param name="Name">The folder's name.</param>
/// <param name="Path">The folder's full path.</param>
public sealed record FolderResource(string Name, string Path)
{
    /// <summary>Maps an entry.</summary>
    /// <param name="entry">The entry to map.</param>
    public static FolderResource From(FolderEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new FolderResource(entry.Name, entry.Path);
    }
}

/// <summary>A folder's sub-folders and the roots the picker may enter.</summary>
/// <param name="Path">The folder listed, or <see langword="null"/> when only the roots are returned.</param>
/// <param name="Parent">The folder above, or <see langword="null"/> at a root.</param>
/// <param name="Folders">The sub-folders: never files, never hidden ones.</param>
/// <param name="Roots">Mounted volumes in a container, else the file system roots.</param>
public sealed record FolderListingResource(
    string? Path,
    string? Parent,
    IReadOnlyList<FolderResource> Folders,
    IReadOnlyList<string> Roots);

/// <summary>The folder to create.</summary>
/// <param name="Parent">An existing absolute folder inside a root.</param>
/// <param name="Name">One plain folder name.</param>
public sealed record CreateFolderResource(string Parent, string Name);

/// <summary>
/// The folder picker's backend: browse only the places a folder may be chosen (the mounted volumes in a container),
/// and make a new folder there. All the rules live in <see cref="IFileSystemBrowser"/>.
/// </summary>
[ApiController]
[Route("api/v1/filesystem")]
public sealed class FileSystemController : ControllerBase
{
    private readonly IFileSystemBrowser _browser;

    /// <summary>Initialises a new instance of the <see cref="FileSystemController"/> class.</summary>
    /// <param name="browser">Lists and creates the folders.</param>
    public FileSystemController(IFileSystemBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);

        _browser = browser;
    }

    /// <summary>Lists the roots, or the sub-folders of a folder inside one.</summary>
    /// <param name="path">An absolute folder inside a root; omit it to get the roots alone.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>200 with the listing; 400 for a path that is relative, has <c>..</c> or is outside every root; 404 when the folder does not exist; 504 when it does not answer in time.</returns>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType(typeof(FolderListingResource), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<FolderListingResource>> Browse([FromQuery] string? path, CancellationToken cancellationToken)
    {
        var result = await _browser.BrowseAsync(path, cancellationToken).ConfigureAwait(false);

        if (result.Outcome != FileSystemOutcome.Ok || result.Value is not { } listing)
        {
            return Refused(result.Outcome, result.Message, "path");
        }

        return Ok(new FolderListingResource(listing.Path, listing.Parent, [.. listing.Folders.Select(FolderResource.From)], listing.Roots));
    }

    /// <summary>Creates a folder inside a folder that sits in a root.</summary>
    /// <param name="resource">The parent and the new folder's name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>200 with the new folder; 400 for a bad name or parent; 404 when the parent does not exist; 409 when the folder is already there; 504 when the disk does not answer in time.</returns>
    [HttpPost("folder")]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(FolderResource), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<FolderResource>> CreateFolder([FromBody] CreateFolderResource resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var result = await _browser
            .CreateFolderAsync(resource.Parent ?? string.Empty, resource.Name ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (result.Outcome != FileSystemOutcome.Ok || result.Value is not { } folder)
        {
            return Refused(result.Outcome, result.Message, "name");
        }

        return Ok(FolderResource.From(folder));
    }

    private ActionResult Refused(FileSystemOutcome outcome, string? message, string field)
    {
        var text = message ?? "The request was refused.";

        switch (outcome)
        {
            case FileSystemOutcome.Invalid:
                ModelState.AddModelError(field, text);

                return ValidationProblem(ModelState);
            case FileSystemOutcome.NotFound:
                return Problem(title: text, detail: text, statusCode: StatusCodes.Status404NotFound);
            case FileSystemOutcome.Conflict:
                return Problem(title: text, detail: text, statusCode: StatusCodes.Status409Conflict);
            default:
                return Problem(title: text, detail: text, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }
}
