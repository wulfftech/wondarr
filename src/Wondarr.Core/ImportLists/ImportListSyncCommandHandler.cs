using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ImportListSyncCommandHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="ImportListSyncCommandHandler"/> class.</summary>
    /// <param name="scopes">
    /// Builds the scope the import-list service runs in (resolved when the command runs, not when the
    /// handler is built: it reaches Plex, the metadata providers and the database).
    /// </param>
    /// <param name="logger">The logger.</param>
    public ImportListSyncCommandHandler(IServiceScopeFactory scopes, ILogger<ImportListSyncCommandHandler> logger)
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

        using var scope = _scopes.CreateScope();
        var lists = scope.ServiceProvider.GetRequiredService<IImportListService>();

        if (ReadImportListId(context.Body) is { } id)
        {
            // One list, asked for by hand: a source that cannot be read fails the command, so the
            // user sees why.
            return await lists.SyncAsync(id, context.ReportProgressAsync, cancellationToken).ConfigureAwait(false);
        }

        var due = await lists.GetDueAsync(cancellationToken).ConfigureAwait(false);
        var synced = 0;
        var failed = 0;

        foreach (var listId in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await lists.SyncAsync(listId, context.ReportProgressAsync, cancellationToken).ConfigureAwait(false);
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

        // Songs that got their files since the last sync belong in the playlists now, whether or not
        // their list is due for a read of its source.
        var playlists = await lists.RefreshPlaylistsAsync(cancellationToken).ConfigureAwait(false);

        var summary = due.Count == 0
            ? "No import list is due"
            : string.Create(CultureInfo.InvariantCulture, $"Synced {synced} import lists, {failed} failed");

        return playlists == 0
            ? summary
            : string.Create(CultureInfo.InvariantCulture, $"{summary}; playlists of {playlists} lists written");
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
