using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;

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
    private readonly SlotWaitContext _slotWait;
    private readonly ILogger<SongSearchCommandHandler> _logger;
    private readonly WondarrDbContext? _database;
    private readonly ILibraryRoots? _roots;

    /// <summary>Initialises a new instance of the <see cref="SongSearchCommandHandler"/> class.</summary>
    /// <param name="search">The search-and-grab service.</param>
    /// <param name="slotWait">Where this command offers its progress and its worker to a search that has to wait.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="database">Finds the song's library; without it (and <paramref name="roots"/>) no library check is made.</param>
    /// <param name="roots">Keeps search on add away from a library whose folder is missing or unmarked.</param>
    public SongSearchCommandHandler(
        ISongSearchService search,
        SlotWaitContext slotWait,
        ILogger<SongSearchCommandHandler> logger,
        WondarrDbContext? database = null,
        ILibraryRoots? roots = null)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(slotWait);
        ArgumentNullException.ThrowIfNull(logger);

        _search = search;
        _slotWait = slotWait;
        _logger = logger;
        _database = database;
        _roots = roots;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    /// <remarks>A search that waits for a download slot honours its token and releases everything it holds.</remarks>
    public bool CancelWhileRunning => true;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var songId = ReadSongId(context.Body);

        // Search on add (and any other automatic caller) skips a song whose library folder is missing
        // or unmarked: the grab would only wait in the queue. A person pressing "search now" is not
        // held back: their grab waits in the queue like a manual one.
        if (context.Trigger != CommandTrigger.Manual
            && _database is not null
            && _roots is not null
            && await _database.Songs
                .AsNoTracking()
                .Where(song => song.Id == songId)
                .Join(_database.Libraries.AsNoTracking(), song => song.LibraryId, library => library.Id, (song, library) => library)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false) is { } library
            && await _roots.CheckAsync(library.RootPath, cancellationToken).ConfigureAwait(false) is var status
            && status != LibraryRootStatus.Ready)
        {
            var reason = _roots.Describe(library, status);
            LogSkipped(_logger, songId, reason);

            return string.Concat(
                "Song ",
                songId.ToString(CultureInfo.InvariantCulture),
                ": Skipped — ",
                reason);
        }

        LogSearching(_logger, songId);

        // The service shares this scope: if the search has to wait for a download slot it reports it on
        // this command and lends this command's executor worker meanwhile.
        _slotWait.ReportProgressAsync = context.ReportProgressAsync;
        _slotWait.YieldWorker = context.YieldWorker;
        _slotWait.CancelWait = context.UserCancellation;
        _slotWait.WaitForSlot = true;

        // A user asking for this song must not be held back by the backoff, but the run still obeys
        // the automatic rules: the trigger stays Automatic, so the interactive exemptions do not apply.
        var result = await _search
            .SearchAsync(songId, SearchTrigger.Automatic, grab: true, cancellationToken)
            .ConfigureAwait(false);

        // A search that waited for a slot without getting one, or whose song no longer needed the grab by
        // the time one freed, is not a failed search: the command did its job and says "Completed". (The
        // run itself is recorded as NoFreeSlot or Cancelled respectively.)
        return string.Concat(
            "Song ",
            songId.ToString(CultureInfo.InvariantCulture),
            ": ",
            result.SlotWaitExpired || result.SatisfiedDuringWait ? "Completed" : result.Outcome.ToString(),
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Not searching for song {SongId}: {Reason}")]
    private static partial void LogSkipped(ILogger logger, long songId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Searching for song {SongId} on request")]
    private static partial void LogSearching(ILogger logger, long songId);
}
