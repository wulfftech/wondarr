using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Jobs;

namespace Wondarr.Core.ImportLists;

/// <summary>
/// The <c>ImportListSync</c> command. With a body naming an <c>importListId</c> it syncs that list
/// (the "Sync now" button); without one — the hourly scheduled run — it syncs every enabled list whose
/// interval has passed, one after another, and a list whose source is down does not stop the others.
/// </summary>
public sealed partial class ImportListSyncCommandHandler : ICommandHandler
{
    /// <summary>The command name the queue, the schedule and the API use.</summary>
    public const string CommandName = "ImportListSync";

    private static readonly JsonSerializerOptions BodyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IImportListService _lists;
    private readonly ILogger<ImportListSyncCommandHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="ImportListSyncCommandHandler"/> class.</summary>
    /// <param name="lists">The import-list service.</param>
    /// <param name="logger">The logger.</param>
    public ImportListSyncCommandHandler(IImportListService lists, ILogger<ImportListSyncCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentNullException.ThrowIfNull(logger);

        _lists = lists;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (ReadImportListId(context.Body) is { } id)
        {
            // One list, asked for by hand: a source that cannot be read fails the command, so the
            // user sees why.
            return await _lists.SyncAsync(id, context.ReportProgressAsync, cancellationToken).ConfigureAwait(false);
        }

        var due = await _lists.GetDueAsync(cancellationToken).ConfigureAwait(false);
        var synced = 0;
        var failed = 0;

        foreach (var listId in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _lists.SyncAsync(listId, context.ReportProgressAsync, cancellationToken).ConfigureAwait(false);
                synced++;
            }
            catch (ImportListSyncException)
            {
                // The list has recorded why; the next list still runs.
                failed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogListFailed(_logger, listId, exception);
                failed++;
            }
        }

        return due.Count == 0
            ? "No import list is due"
            : string.Create(CultureInfo.InvariantCulture, $"Synced {synced} import lists, {failed} failed");
    }

    private static long? ReadImportListId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        ImportListSyncBody? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<ImportListSyncBody>(body, BodyJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The ImportListSync command body is not valid JSON.", exception);
        }

        return parsed?.ImportListId is > 0 ? parsed.ImportListId : null;
    }

    private sealed record ImportListSyncBody(string? Name, long? ImportListId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Syncing import list {ImportListId} failed")]
    private static partial void LogListFailed(ILogger logger, long importListId, Exception exception);
}
