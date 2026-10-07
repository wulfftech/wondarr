using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Wondarr.Core.Jobs;
using Wondarr.Core.Media;

namespace Wondarr.Api.Songs;

/// <summary>What the caller converts: songs or a library, by the library's policy or a one-off rule.</summary>
/// <param name="SongIds">The songs to convert; or leave empty and name a library.</param>
/// <param name="LibraryId">The library whose songs are converted; or leave empty and name songs.</param>
/// <param name="Rule">A one-off rule (one output rule: <c>codec</c>, <c>mode</c>, <c>bitrateKbps</c>, …), or <see langword="null"/> for each library's policy.</param>
public sealed record ConvertRequestResource(IReadOnlyList<long>? SongIds, long? LibraryId, JsonNode? Rule);

/// <summary>One song in a conversion plan.</summary>
/// <param name="SongId">The song.</param>
/// <param name="Outcome"><c>converted</c>, <c>skipped</c>, <c>refused</c> or <c>failed</c>.</param>
/// <param name="Reason">Why, for anything but a conversion.</param>
/// <param name="FromCodec">The current codec.</param>
/// <param name="ToCodec">The target codec.</param>
/// <param name="CurrentSize">The current size in bytes.</param>
/// <param name="EstimatedSize">The estimated size after the conversion.</param>
public sealed record ConvertSongResource(
    long SongId,
    string Outcome,
    string? Reason,
    string? FromCodec,
    string? ToCodec,
    long CurrentSize,
    long EstimatedSize);

/// <summary>A dry run: what a conversion would do.</summary>
/// <param name="Convert">Songs that would be converted.</param>
/// <param name="Skip">Songs left as they are.</param>
/// <param name="Refuse">Songs the rule cannot apply to.</param>
/// <param name="CurrentSize">The total size of the files that would be converted.</param>
/// <param name="EstimatedSize">Their estimated total size afterwards.</param>
/// <param name="Songs">The first 200 songs.</param>
public sealed record ConvertPlanResource(
    int Convert,
    int Skip,
    int Refuse,
    long CurrentSize,
    long EstimatedSize,
    IReadOnlyList<ConvertSongResource> Songs);

/// <summary>A queued conversion.</summary>
/// <param name="CommandId">The <c>ConvertFiles</c> command; poll <c>GET /api/v1/command/{commandId}</c>.</param>
public sealed record ConvertAcceptedResource(long CommandId);

/// <summary>
/// Conversion on demand (DECISIONS build session 7 #6): a dry run, then a <c>ConvertFiles</c>
/// command that converts the files one at a time, the originals going to the recycle bin.
/// </summary>
[ApiController]
[Route("api/v1/song")]
public sealed class SongConvertController : ControllerBase
{
    private static readonly JsonSerializerOptions CommandJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IFileConverter _converter;
    private readonly ICommandQueue _commands;

    /// <summary>Initialises a new instance of the <see cref="SongConvertController"/> class.</summary>
    /// <param name="converter">The converter.</param>
    /// <param name="commands">The command queue the conversion runs on.</param>
    public SongConvertController(IFileConverter converter, ICommandQueue commands)
    {
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(commands);

        _converter = converter;
        _commands = commands;
    }

    /// <summary>What a conversion would do, from the stored file rows; nothing is touched.</summary>
    /// <param name="resource">The songs or library, and the rule.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpPost("convert/preview")]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType<ConvertPlanResource>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ConvertPlanResource>> PreviewConversion(
        [FromBody] ConvertRequestResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            var plan = await _converter.PlanAsync(ToRequest(resource), cancellationToken).ConfigureAwait(false);

            return Ok(new ConvertPlanResource(
                plan.Convert,
                plan.Skip,
                plan.Refuse,
                plan.CurrentSize,
                plan.EstimatedSize,
                [.. plan.Songs.Select(song => new ConvertSongResource(
                    song.SongId,
                    JsonNamingPolicy.CamelCase.ConvertName(song.Outcome.ToString()),
                    song.Reason,
                    song.FromCodec,
                    song.ToCodec,
                    song.CurrentSize,
                    song.EstimatedSize))]));
        }
        catch (ConvertRequestException exception)
        {
            return Invalid(exception);
        }
    }

    /// <summary>Queues the conversion.</summary>
    /// <param name="resource">The songs or library, and the rule.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPost("convert")]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType<ConvertAcceptedResource>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Convert(
        [FromBody] ConvertRequestResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var request = ToRequest(resource);

        try
        {
            // Checked now, so a request that cannot run is a 400 rather than a failed command.
            await _converter.ResolveSongsAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ConvertRequestException exception)
        {
            return Invalid(exception);
        }

        var command = await _commands
            .EnqueueAsync(
                ConvertFilesCommandHandler.CommandName,
                JsonSerializer.Serialize(
                    new
                    {
                        name = ConvertFilesCommandHandler.CommandName,
                        songIds = resource.SongIds,
                        libraryId = resource.LibraryId,
                        rule = resource.Rule,
                    },
                    CommandJson),
                CommandTrigger.Manual,
                cancellationToken)
            .ConfigureAwait(false);

        return Accepted(new ConvertAcceptedResource(command.Id));
    }

    private static ConvertRequest ToRequest(ConvertRequestResource resource) =>
        new(resource.SongIds, resource.LibraryId, resource.Rule?.ToJsonString());

    private ActionResult Invalid(ConvertRequestException exception)
    {
        ModelState.AddModelError(exception.Field, exception.Detail);

        return ValidationProblem(ModelState);
    }
}
