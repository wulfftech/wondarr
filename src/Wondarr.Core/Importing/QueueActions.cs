using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Importing;

/// <summary>What the user can do to an item of the download queue.</summary>
public interface IQueueActions
{
    /// <summary>
    /// Takes one grab out of the queue: stops it at the source, and — when asked — blocklists the
    /// candidate it was carrying and starts the next one.
    /// </summary>
    /// <param name="queueItemId">The item to remove.</param>
    /// <param name="blocklist">Whether the candidate is blocklisted for the song.</param>
    /// <param name="retry">Whether the search is asked for the next candidate at once.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see langword="true"/> when the item existed, <see langword="false"/> when it did not.</returns>
    Task<bool> RemoveAsync(long queueItemId, bool blocklist, bool retry, CancellationToken cancellationToken);
}

/// <summary>
/// The user's side of the download queue (ARCHITECTURE §5.4). Removing an item that is still in flight
/// cancels it at the source and leaves the history behind; removing one that is already finished only
/// drops its row, because the history is the record.
/// </summary>
public sealed partial class QueueActions : IQueueActions
{
    /// <summary>The reason a candidate blocklisted by hand carries.</summary>
    internal const string RemovedByUser = "Removed by the user";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly char[] Separators = ['/', '\\'];

    private readonly WondarrDbContext _database;
    private readonly IReadOnlyList<ISourceProvider> _sources;
    private readonly ISongSearchService _search;
    private readonly IEventAggregator _events;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QueueActions> _logger;

    /// <summary>Initialises a new instance of the <see cref="QueueActions"/> class.</summary>
    /// <param name="database">The Wondarr database; the item, the blocklist entry and the history commit through it.</param>
    /// <param name="sources">The source providers, one per source type.</param>
    /// <param name="search">Grabs the next candidate when the user asks for one.</param>
    /// <param name="events">Publishes the queue item change.</param>
    /// <param name="timeProvider">The clock every timestamp comes from.</param>
    /// <param name="logger">The logger.</param>
    public QueueActions(
        WondarrDbContext database,
        IEnumerable<ISourceProvider> sources,
        ISongSearchService search,
        IEventAggregator events,
        TimeProvider timeProvider,
        ILogger<QueueActions> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _sources = [.. sources];
        _search = search;
        _events = events;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        long queueItemId,
        bool blocklist,
        bool retry,
        CancellationToken cancellationToken)
    {
        var item = await _database.QueueItems
            .Include(entry => entry.Candidate)
            .FirstOrDefaultAsync(entry => entry.Id == queueItemId, cancellationToken)
            .ConfigureAwait(false);

        if (item is null)
        {
            LogMissingItem(_logger, queueItemId);

            return false;
        }

        if (item.State is QueueItemState.Imported or QueueItemState.Failed or QueueItemState.Cancelled)
        {
            // Finished: the history is the record, so the row is simply dropped.
            _database.QueueItems.Remove(item);
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            LogDeleted(_logger, item.Id);

            return true;
        }

        await CancelAtSourceAsync(item, cancellationToken).ConfigureAwait(false);

        if (blocklist)
        {
            _database.Blocklist.Add(new BlocklistItem
            {
                SongId = item.SongId,
                SourceType = item.SourceType,
                BlocklistKey = item.Candidate.BlocklistKey,
                Reason = RemovedByUser,
                ExpiresAt = null,
            });

            // A download that finished but was never imported is ours to delete; one still in flight
            // is the source's, and cancelling above is what stops it.
            if (item.State == QueueItemState.Completed)
            {
                DeleteDownload(item);
            }
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        item.State = QueueItemState.Cancelled;
        item.StateChangedAt = now;
        item.FinishedAt = now;
        item.Message = RemovedByUser;

        _database.History.Add(new HistoryItem
        {
            SongId = item.SongId,
            EventType = HistoryEventType.Failed,
            SourceInstanceId = item.SourceInstanceId,
            Data = JsonSerializer.Serialize(new RemovalHistoryData(true, blocklist, retry), Json),
        });

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _events
            .PublishAsync(
                new QueueItemChangedEvent(item.Id, item.SongId, item.State)
                {
                    Progress = item.Progress,
                    Message = item.Message,
                },
                cancellationToken)
            .ConfigureAwait(false);

        LogRemoved(_logger, item.Id, blocklist, retry);

        if (retry)
        {
            // A manual retry ignores the attempt budget: the user asked for the next candidate, and
            // the next candidate is what they get.
            await TryNextAsync(item, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Stops the grab at its source; a failure to stop it is logged, never thrown.</summary>
    /// <param name="item">The item to cancel.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    private async Task CancelAtSourceAsync(QueueItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.Handle))
        {
            return;
        }

        var provider = FindProvider(item.SourceType);

        if (provider is null)
        {
            return;
        }

        try
        {
            await provider
                .CancelAsync(new GrabHandle(item.SourceType, item.Handle), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogCancelFailed(_logger, item.Id, item.SourceType, exception);
        }
    }

    /// <summary>Asks the search for the next accepted candidate of the item's run.</summary>
    /// <param name="item">The removed item.</param>
    /// <param name="cancellationToken">Cancels the grab.</param>
    private async Task TryNextAsync(QueueItem item, CancellationToken cancellationToken)
    {
        try
        {
            await _search
                .GrabBestAsync(item.SearchRunId, item.Attempt + 1, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The removal has already been recorded; a grab that could not start is a message in the
            // log, not an error the caller has to answer for.
            LogNextAttemptFailed(_logger, item.Id, exception);
        }
    }

    /// <summary>
    /// Deletes a finished download, and only while it really is the item's own: the file has to sit
    /// directly in a folder whose trailing segments are the item's <c>Destination</c>, with no <c>..</c>
    /// in the path and no reparse point on the way. Anything else is left alone.
    /// </summary>
    /// <param name="item">The item that owns the download.</param>
    private void DeleteDownload(QueueItem item)
    {
        if (!IsOwnDownload(item, out var path, out var folder))
        {
            LogDeleteRefused(_logger, item.Id, item.DownloadPath);

            return;
        }

        try
        {
            File.Delete(path);

            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (IOException exception)
        {
            LogDeleteFailed(_logger, path, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            LogDeleteFailed(_logger, path, exception.Message);
        }
    }

    /// <summary>Whether a download is Wondarr's to delete, and where it is.</summary>
    /// <param name="item">The item that owns the download.</param>
    /// <param name="path">The full path of the file.</param>
    /// <param name="folder">The folder the file sits in.</param>
    private static bool IsOwnDownload(QueueItem item, out string path, out string folder)
    {
        path = string.Empty;
        folder = string.Empty;

        if (string.IsNullOrWhiteSpace(item.DownloadPath))
        {
            return false;
        }

        // ".." is only visible before normalisation: Path.GetFullPath would resolve it away and hide it.
        var given = SplitSegments(item.DownloadPath);

        if (given.Count == 0 || given.Contains(".."))
        {
            return false;
        }

        var wanted = SplitSegments(item.Destination);

        if (wanted.Count == 0)
        {
            return false;
        }

        var full = Path.GetFullPath(item.DownloadPath);
        var directory = Path.GetDirectoryName(full);

        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var have = SplitSegments(directory);

        if (have.Count < wanted.Count)
        {
            return false;
        }

        for (var index = 0; index < wanted.Count; index++)
        {
            if (!string.Equals(have[have.Count - wanted.Count + index], wanted[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (IsReparsePoint(directory) || (File.Exists(full) && IsReparsePoint(full)))
        {
            return false;
        }

        path = full;
        folder = directory;

        return true;
    }

    /// <summary>Splits a path or a destination into its segments, treating <c>/</c> and <c>\</c> alike.</summary>
    /// <param name="path">The path to split.</param>
    private static List<string> SplitSegments(string path) =>
        [.. path.Split(Separators, StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>Whether a path is a symlink, a junction or another reparse point; unreadable counts as one.</summary>
    /// <param name="path">The path to look at.</param>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The source provider for a source type, or <see langword="null"/> when there is none.</summary>
    /// <param name="sourceType">One of <see cref="SourceTypes"/>.</param>
    private ISourceProvider? FindProvider(string sourceType)
    {
        foreach (var provider in _sources)
        {
            if (string.Equals(provider.SourceType, sourceType, StringComparison.OrdinalIgnoreCase))
            {
                return provider;
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed queue item {QueueItemId} (blocklisted: {Blocklisted}, retried: {Retried})")]
    private static partial void LogRemoved(ILogger logger, long queueItemId, bool blocklisted, bool retried);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted the finished queue item {QueueItemId}")]
    private static partial void LogDeleted(ILogger logger, long queueItemId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No queue item has the id {QueueItemId}")]
    private static partial void LogMissingItem(ILogger logger, long queueItemId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The grab of queue item {QueueItemId} could not be cancelled at {SourceType}")]
    private static partial void LogCancelFailed(ILogger logger, long queueItemId, string sourceType, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The next candidate for queue item {QueueItemId} could not be grabbed")]
    private static partial void LogNextAttemptFailed(ILogger logger, long queueItemId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refusing to delete {Path}: it is not the own download of queue item {QueueItemId}")]
    private static partial void LogDeleteRefused(ILogger logger, long queueItemId, string? path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the download {Path}: {Reason}")]
    private static partial void LogDeleteFailed(ILogger logger, string path, string reason);

    /// <summary>The history payload of a removal by the user.</summary>
    /// <param name="RemovedByUser">Always true; the row only exists when the user removed something.</param>
    /// <param name="Blocklisted">Whether the candidate was blocklisted for the song.</param>
    /// <param name="Retried">Whether the search was asked for the next candidate.</param>
    private sealed record RemovalHistoryData(bool RemovedByUser, bool Blocklisted, bool Retried);
}