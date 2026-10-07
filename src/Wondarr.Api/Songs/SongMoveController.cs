using System.Text.Json;
using Wondarr.Core.Jobs;
using Wondarr.Core.Profiles;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Songs;

/// <summary>
/// Moving songs between libraries: <c>POST /api/v1/song/move</c> checks the body, queues a
/// <c>MoveSongs</c> command and answers as soon as the songs are named, so the caller polls
/// <c>GET /api/v1/command/{commandId}</c> while it runs.
/// </summary>
[ApiController]
[Route("api/v1/song")]
public sealed class SongMoveController : ControllerBase
{
    /// <summary>Command bodies are camelCase, like the API's own resources.</summary>
    private static readonly JsonSerializerOptions CommandJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILibraryService _libraries;
    private readonly ICommandQueue _commands;

    /// <summary>Initialises a new instance of the <see cref="SongMoveController"/> class.</summary>
    /// <param name="libraries">The library service, which knows whether the target exists.</param>
    /// <param name="commands">The command queue the move runs on.</param>
    public SongMoveController(ILibraryService libraries, ICommandQueue commands)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(commands);

        _libraries = libraries;
        _commands = commands;
    }

    /// <summary>Queues the move of songs to another library.</summary>
    /// <param name="resource">The songs and the library they go to.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>202 and the command to poll; 400 when the body is unusable.</returns>
    [HttpPost("move")]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(SongMoveAcceptedResource), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SongMoveAcceptedResource>> Move(
        [FromBody] SongMoveResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (resource.SongIds is not { Count: >= 1 } songIds)
        {
            return Problem(
                title: "A list of songs is required",
                detail: "songIds must hold at least one id.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (songIds.Count > MoveSongsCommandHandler.MaxSongIds)
        {
            return Problem(
                title: "Too many songs",
                detail: $"songIds holds more than {MoveSongsCommandHandler.MaxSongIds} ids.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (await _libraries.GetAsync(resource.LibraryId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Problem(
                title: "Unknown library",
                detail: $"libraryId {resource.LibraryId} does not exist.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var command = await _commands
            .EnqueueAsync(
                MoveSongsCommandHandler.CommandName,
                JsonSerializer.Serialize(
                    new MoveSongsCommandBody(MoveSongsCommandHandler.CommandName, songIds, resource.LibraryId),
                    CommandJson),
                CommandTrigger.Manual,
                cancellationToken)
            .ConfigureAwait(false);

        return Accepted(new SongMoveAcceptedResource(command.Id));
    }

    /// <summary>The body of the queued command, exactly as <c>MoveSongs</c> reads it.</summary>
    /// <param name="Name">The command name.</param>
    /// <param name="SongIds">The songs to move.</param>
    /// <param name="LibraryId">The library they go to.</param>
    private sealed record MoveSongsCommandBody(string Name, IReadOnlyList<long> SongIds, long LibraryId);
}

/// <summary>The songs to move, and the library they go to.</summary>
/// <param name="SongIds">The songs to move, at most 1,000 of them.</param>
/// <param name="LibraryId">The library they go to.</param>
public sealed record SongMoveResource(IReadOnlyList<long>? SongIds, long LibraryId);

/// <summary>The accepted move: the command to poll.</summary>
/// <param name="CommandId">The <c>MoveSongs</c> command; poll <c>GET /api/v1/command/{commandId}</c>.</param>
public sealed record SongMoveAcceptedResource(long CommandId);
