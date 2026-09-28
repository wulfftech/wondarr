using System.Text.Json;
using Wondarr.Core.ImportLists;
using Wondarr.Core.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.ImportLists;

/// <summary>
/// The bulk add: a pasted block of lines becomes an import list, and a <c>BulkAddSongs</c> command
/// resolves it in the background. The endpoint answers as soon as the list is stored, so the caller
/// polls <c>GET /api/v1/command/{commandId}</c> and reads the review screen while it runs.
/// </summary>
[ApiController]
[Route("api/v1/song")]
public sealed class BulkAddController : ControllerBase
{
    /// <summary>Command bodies are camelCase, like the API's own resources.</summary>
    private static readonly JsonSerializerOptions CommandJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IPasteListService _pastes;
    private readonly ICommandQueue _commands;

    /// <summary>Initialises a new instance of the <see cref="BulkAddController"/> class.</summary>
    /// <param name="pastes">The pasted-list pipeline.</param>
    /// <param name="commands">The command queue the resolve runs on.</param>
    public BulkAddController(IPasteListService pastes, ICommandQueue commands)
    {
        ArgumentNullException.ThrowIfNull(pastes);
        ArgumentNullException.ThrowIfNull(commands);

        _pastes = pastes;
        _commands = commands;
    }

    /// <summary>Stores a pasted list and queues the command that resolves it.</summary>
    /// <param name="resource">The pasted text, and where its songs land.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>202 and the list, the command and the line count; 400 when the input is unusable.</returns>
    [HttpPost("bulk")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<BulkAddAcceptedResource>> BulkAdd(
        [FromBody] BulkAddResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (string.IsNullOrWhiteSpace(resource.Text))
        {
            return Problem(
                title: "A list of songs is required",
                detail: "text must hold at least one line.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var list = await _pastes
                .CreateAsync(resource.Text, resource.QualityProfileId, resource.LibraryId, cancellationToken)
                .ConfigureAwait(false);

            var command = await _commands
                .EnqueueAsync(
                    BulkAddSongsCommandHandler.CommandName,
                    JsonSerializer.Serialize(
                        new BulkAddCommandBody(BulkAddSongsCommandHandler.CommandName, list.Id),
                        CommandJson),
                    CommandTrigger.Manual,
                    cancellationToken)
                .ConfigureAwait(false);

            return Accepted(new BulkAddAcceptedResource(list.Id, command.Id, list.Items.Count));
        }
        catch (ArgumentException exception)
        {
            return Problem(
                title: "Invalid request",
                detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>The body of the queued command, exactly as <c>BulkAddSongs</c> reads it.</summary>
    /// <param name="Name">The command name.</param>
    /// <param name="ImportListId">The list to process.</param>
    private sealed record BulkAddCommandBody(string Name, long ImportListId);
}
