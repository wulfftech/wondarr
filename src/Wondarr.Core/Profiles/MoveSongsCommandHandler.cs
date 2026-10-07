using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Jobs;

namespace Wondarr.Core.Profiles;

/// <summary>
/// The background half of a library move: <c>MoveSongs</c> moves each named song to the named
/// library, one by one, so the user can watch progress and one refusal or failure does not stop
/// the rest.
/// </summary>
public sealed partial class MoveSongsCommandHandler : ICommandHandler
{
    /// <summary>The name <c>POST /api/v1/song/move</c> queues.</summary>
    public const string CommandName = "MoveSongs";

    /// <summary>How many songs one command may move.</summary>
    public const int MaxSongIds = 1_000;

    /// <summary>The command bodies are camelCase, like every other JSON this app exchanges.</summary>
    private static readonly JsonSerializerOptions BodyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MoveSongsCommandHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="MoveSongsCommandHandler"/> class.</summary>
    /// <param name="scopes">The scope factory, so each song is moved through its own scope.</param>
    /// <param name="logger">The logger.</param>
    public MoveSongsCommandHandler(IServiceScopeFactory scopes, ILogger<MoveSongsCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (songIds, libraryId) = ReadBody(context.Body);

        var moved = 0;
        var alreadyThere = 0;
        var refused = 0;
        var failed = 0;
        string? firstReason = null;

        for (var index = 0; index < songIds.Count; index++)
        {
            // One scope per song, so each move reads the rows it is about through its own tracker —
            // the same shape the Compact library command handler uses.
            using var scope = _scopes.CreateScope();
            var mover = scope.ServiceProvider.GetRequiredService<ISongLibraryMover>();

            var result = await mover
                .MoveAsync(songIds[index], libraryId, cancellationToken)
                .ConfigureAwait(false);

            switch (result.Outcome)
            {
                case SongMoveOutcome.Moved:
                    moved++;
                    break;
                case SongMoveOutcome.AlreadyThere:
                    alreadyThere++;
                    break;
                case SongMoveOutcome.Refused:
                    refused++;
                    firstReason ??= result.Reason;
                    break;
                case SongMoveOutcome.Failed:
                    failed++;
                    firstReason ??= result.Reason;
                    break;
                default:
                    break;
            }

            await context
                .ReportProgressAsync($"Moved {index + 1} of {songIds.Count}")
                .ConfigureAwait(false);
        }

        var summary =
            $"Moved {moved} of {songIds.Count} songs to library {libraryId}: {moved} moved, {alreadyThere} already there, {refused} refused, {failed} failed";

        if (firstReason is not null)
        {
            summary = string.Concat(summary, $" — {firstReason}");
        }

        LogDone(_logger, libraryId, moved, alreadyThere, refused, failed);

        return summary;
    }

    /// <summary>The songs and the library a <c>MoveSongs</c> body names.</summary>
    private static (IReadOnlyList<long> SongIds, long LibraryId) ReadBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("The MoveSongs command needs a body naming songIds and a libraryId.");
        }

        MoveSongsBody? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<MoveSongsBody>(body, BodyJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The MoveSongs command body is not valid JSON.", exception);
        }

        if (parsed is not { LibraryId: > 0 })
        {
            throw new ArgumentException("The MoveSongs command body must name a libraryId.");
        }

        if (parsed.SongIds is not { Count: >= 1 })
        {
            throw new ArgumentException("The MoveSongs command body must name at least one songId.");
        }

        if (parsed.SongIds.Count > MaxSongIds)
        {
            throw new ArgumentException($"The MoveSongs command body names more than {MaxSongIds} songIds.");
        }

        return (parsed.SongIds, parsed.LibraryId);
    }

    /// <summary>The body of a library move: which songs, and which library they go to.</summary>
    /// <param name="Name">The command name, echoed by the API for readability.</param>
    /// <param name="SongIds">The songs to move.</param>
    /// <param name="LibraryId">The library they go to.</param>
    private sealed record MoveSongsBody(string? Name, IReadOnlyList<long>? SongIds, long LibraryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved {Moved} songs to library {LibraryId} ({AlreadyThere} already there, {Refused} refused, {Failed} failed)")]
    private static partial void LogDone(
        ILogger logger,
        long libraryId,
        int moved,
        int alreadyThere,
        int refused,
        int failed);
}