using System.Text.Json;
using Wondarr.Core.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Commands;

/// <summary>
/// The command endpoint the UI and *arr-style clients use. Modelled on Lidarr's
/// <c>CommandController</c> (<c>src/Lidarr.Api.V1/Commands/CommandController.cs</c>): the whole
/// request body is kept as the command's body, and the command runs in the background — the
/// response only says that it was queued.
/// </summary>
[ApiController]
[Route("api/v1/command")]
public sealed class CommandController : ControllerBase
{
    private readonly ICommandQueue _commandQueue;

    /// <summary>Initialises a new instance of the <see cref="CommandController"/> class.</summary>
    public CommandController(ICommandQueue commandQueue)
    {
        ArgumentNullException.ThrowIfNull(commandQueue);

        _commandQueue = commandQueue;
    }

    /// <summary>Queues a command.</summary>
    /// <param name="body">
    /// The request body. It must contain a <c>name</c>; everything else is stored verbatim for the
    /// handler to read.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>201 with the queued command, or 400 problem details for a bad or unknown name.</returns>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<IActionResult> StartCommand(
        [FromBody] JsonElement body,
        CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            return Problem(
                title: "Invalid command",
                detail: "The command body must be a JSON object with a non-empty \"name\".",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var name = nameElement.GetString()!;

        try
        {
            var record = await _commandQueue
                .EnqueueAsync(name, body.GetRawText(), CommandTrigger.Manual, cancellationToken)
                .ConfigureAwait(false);

            return CreatedAtAction(nameof(GetCommand), new { id = record.Id }, record.ToResource());
        }
        catch (UnknownCommandException exception)
        {
            return Problem(
                title: "Unknown command",
                detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Lists recently queued commands, newest first.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<IReadOnlyList<CommandResource>>> GetCommands(CancellationToken cancellationToken)
    {
        var records = await _commandQueue.ListAsync(50, cancellationToken).ConfigureAwait(false);

        return Ok(records.Select(record => record.ToResource()).ToList());
    }

    /// <summary>Reads one command.</summary>
    /// <param name="id">The command row.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet("{id:long}")]
    [Produces("application/json")]
    public async Task<ActionResult<CommandResource>> GetCommand(long id, CancellationToken cancellationToken)
    {
        var record = await _commandQueue.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return record is null ? NotFound() : Ok(record.ToResource());
    }

    /// <summary>Cancels a command that has not started.</summary>
    /// <param name="id">The command row.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>204 when it was cancelled, 404 when it does not exist, 409 when it already ran.</returns>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> CancelCommand(long id, CancellationToken cancellationToken)
    {
        var record = await _commandQueue.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound();
        }

        return await _commandQueue.CancelAsync(id, cancellationToken).ConfigureAwait(false)
            ? NoContent()
            : Conflict();
    }
}
