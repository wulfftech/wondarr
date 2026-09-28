using System.Text.Json;
using Wondarr.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.ImportLists;

/// <summary>
/// The background half of a bulk add: <c>BulkAddSongs</c> resolves a stored import list and adds
/// whatever came out of it. It runs as a command so the user can watch progress and the MusicBrainz
/// rate limit is respected by the shared resolvers.
/// </summary>
public sealed partial class BulkAddSongsCommandHandler : ICommandHandler
{
    /// <summary>The name <c>POST /api/v1/song/bulk</c> queues.</summary>
    public const string CommandName = "BulkAddSongs";

    /// <summary>The command bodies are camelCase, like every other JSON this app exchanges.</summary>
    private static readonly JsonSerializerOptions BodyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IPasteListService _pastes;
    private readonly ILogger<BulkAddSongsCommandHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="BulkAddSongsCommandHandler"/> class.</summary>
    /// <param name="pastes">The pasted-list pipeline.</param>
    /// <param name="logger">The logger.</param>
    public BulkAddSongsCommandHandler(IPasteListService pastes, ILogger<BulkAddSongsCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(pastes);
        ArgumentNullException.ThrowIfNull(logger);

        _pastes = pastes;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var importListId = ReadImportListId(context.Body);

        LogProcessing(_logger, importListId);

        // Everything below runs on the scoped services the command's own DI scope built, so this
        // handler never holds a DbContext of its own.
        return await _pastes
            .ProcessAsync(importListId, context.ReportProgressAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The list a <c>BulkAddSongs</c> body names.</summary>
    private static long ReadImportListId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("The BulkAddSongs command needs a body naming an importListId.");
        }

        BulkAddSongsBody? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<BulkAddSongsBody>(body, BodyJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The BulkAddSongs command body is not valid JSON.", exception);
        }

        return parsed is { ImportListId: > 0 }
            ? parsed.ImportListId
            : throw new ArgumentException("The BulkAddSongs command body must name an importListId.");
    }

    /// <summary>The body of a bulk add: which list to process.</summary>
    /// <param name="Name">The command name, echoed by the API for readability.</param>
    /// <param name="ImportListId">The import list to process.</param>
    private sealed record BulkAddSongsBody(string? Name, long ImportListId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bulk add is processing import list {ImportListId}")]
    private static partial void LogProcessing(ILogger logger, long importListId);
}
