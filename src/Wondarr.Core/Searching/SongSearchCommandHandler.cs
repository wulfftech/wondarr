using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;

namespace Wondarr.Core.Searching;

/// <summary>
/// A "search now" button: one song, automatic rules, grab the best accepted candidate. It runs as a
/// command so the API returns immediately and the user can watch the run in the task list.
/// </summary>
public sealed partial class SongSearchCommandHandler : ICommandHandler
{
    /// <summary>The name the search-now command queues.</summary>
    public const string CommandName = "SongSearch";

    /// <summary>The command bodies are camelCase, like every other JSON this app exchanges.</summary>
    private static readonly JsonSerializerOptions BodyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ISongSearchService _search;
    private readonly ILogger<SongSearchCommandHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="SongSearchCommandHandler"/> class.</summary>
    /// <param name="search">The search-and-grab service.</param>
    /// <param name="logger">The logger.</param>
    public SongSearchCommandHandler(ISongSearchService search, ILogger<SongSearchCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(logger);

        _search = search;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var songId = ReadSongId(context.Body);

        LogSearching(_logger, songId);

        // A user asking for this song must not be held back by the backoff, but the run still obeys
        // the automatic rules: the trigger stays Automatic, so the interactive exemptions do not apply.
        var result = await _search
            .SearchAsync(songId, SearchTrigger.Automatic, grab: true, cancellationToken)
            .ConfigureAwait(false);

        return string.Concat(
            "Song ",
            songId.ToString(CultureInfo.InvariantCulture),
            ": ",
            result.Outcome.ToString(),
            result.Message is { Length: > 0 } message ? string.Concat(" — ", message) : string.Empty);
    }

    /// <summary>The song a search body names.</summary>
    private static long ReadSongId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("The SongSearch command needs a body naming a songId.");
        }

        SongSearchBody? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<SongSearchBody>(body, BodyJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The SongSearch command body is not valid JSON.", exception);
        }

        return parsed is { SongId: > 0 }
            ? parsed.SongId
            : throw new ArgumentException("The SongSearch command body must name a songId.");
    }

    /// <summary>The body of a search now: which song to search for.</summary>
    /// <param name="Name">The command name, echoed by the API for readability.</param>
    /// <param name="SongId">The song to search for.</param>
    private sealed record SongSearchBody(string? Name, long SongId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Searching for song {SongId} on request")]
    private static partial void LogSearching(ILogger logger, long songId);
}
