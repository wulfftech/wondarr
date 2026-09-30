using System.Text.Json;
using System.Threading.Channels;
using Wondarr.Core.Domain;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Importing;
using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Notifications;

/// <summary>
/// Turns the events Wondarr publishes into notification sends, without ever making the publisher wait.
/// </summary>
/// <remarks>
/// A handler only puts a small work item on a bounded channel and returns; the database work and the
/// HTTP requests happen on this service's own loop, one item at a time and in order. A full channel
/// drops the newest item rather than blocking a grab or an import, and says so at most once a minute.
/// A send that fails — an exception, the fifteen-second timeout, a non-success status — is a warning
/// naming the notification and never its settings, and the next notification still gets its send.
/// </remarks>
public sealed partial class NotificationDispatcher : BackgroundService,
    IHandle<SongGrabbedEvent>,
    IHandle<SongImportedEvent>,
    IHandle<QueueItemChangedEvent>,
    IHandle<HealthCheckCompletedEvent>
{
    /// <summary>How many events may wait for a send before the newest is dropped.</summary>
    public const int QueueCapacity = 500;

    /// <summary>The longest a channel drop is reported, so a flood of events cannot flood the log.</summary>
    public static readonly TimeSpan DropLogInterval = TimeSpan.FromMinutes(1);

    private readonly Channel<WorkItem> _queue = Channel.CreateBounded<WorkItem>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    private readonly IServiceScopeFactory _scopes;
    private readonly IReadOnlyList<INotificationProvider> _providers;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationDispatcher> _logger;

    // The health results seen last time, as "source\0message" keys. Null until the first result set,
    // which is the baseline: everything in it is already known, so nothing is sent for it.
    private HashSet<string>? _previousHealth;

    private long _lastDropLog;

    /// <summary>Initialises a new instance of the <see cref="NotificationDispatcher"/> class.</summary>
    /// <param name="scopes">Creates the scope each send reads the database in.</param>
    /// <param name="providers">Every registered provider, by implementation name.</param>
    /// <param name="time">The clock the drop-log throttle and rate-limit waits are measured on.</param>
    /// <param name="logger">The log sink.</param>
    public NotificationDispatcher(
        IServiceScopeFactory scopes,
        IEnumerable<INotificationProvider> providers,
        TimeProvider time,
        ILogger<NotificationDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _providers = [.. providers];
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task HandleAsync(SongGrabbedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        return Enqueue(new WorkItem(NotificationEventNames.Grab, message.SongId, QueueItemId: message.QueueItemId));
    }

    /// <inheritdoc />
    public Task HandleAsync(SongImportedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        return Enqueue(new WorkItem(
            message.Upgraded ? NotificationEventNames.Upgrade : NotificationEventNames.Import,
            message.SongId,
            SongFileId: message.SongFileId,
            IsUpgrade: message.Upgraded));
    }

    /// <inheritdoc />
    public Task HandleAsync(QueueItemChangedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.State == QueueItemState.Failed
            ? Enqueue(new WorkItem(NotificationEventNames.Failure, message.SongId, QueueItemId: message.QueueItemId))
            : Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task HandleAsync(HealthCheckCompletedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        return Enqueue(new WorkItem(NotificationEventNames.Health, Results: message.Results));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await DispatchAsync(item, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // One bad item must not stop the loop: the next event still gets its send.
                    LogDispatchFailed(_logger, item.Event, exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; anything still queued is dropped, which is what a best-effort
            // notification is for.
        }
    }

    /// <summary>Hands one work item to the loop. Never throws and never waits.</summary>
    private Task Enqueue(WorkItem item)
    {
        if (_queue.Writer.TryWrite(item))
        {
            return Task.CompletedTask;
        }

        var now = _time.GetTimestamp();

        if (_lastDropLog == 0 || now - _lastDropLog >= _time.TimestampFrequency * DropLogInterval.TotalSeconds)
        {
            _lastDropLog = now;

            LogQueueFull(_logger);
        }

        return Task.CompletedTask;
    }

    /// <summary>Builds the message for one item and sends it to every notification that wants it.</summary>
    private async Task DispatchAsync(WorkItem item, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        IReadOnlyList<NotificationMessage> messages;

        if (item.Event == NotificationEventNames.Health)
        {
            messages = NewIssues(item.Results ?? []);
        }
        else
        {
            var message = await BuildAsync(database, item, cancellationToken).ConfigureAwait(false);

            if (message is null)
            {
                LogDropped(_logger, item.Event);

                return;
            }

            messages = [message];
        }

        if (messages.Count == 0)
        {
            return;
        }

        var notifications = await database.Notifications
            .Where(notification => notification.Enabled)
            .OrderBy(notification => notification.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var message in messages)
        {
            foreach (var notification in notifications)
            {
                if (Subscribed(notification, message.Event))
                {
                    await SendAsync(notification, message, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Sends one message to one notification. A failure is a warning and nothing else: no retry, and
    /// the next notification is unaffected.
    /// </summary>
    private async Task SendAsync(
        Notification notification,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Implementation, notification.Type, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            LogNoProvider(_logger, notification.Name, notification.Type);

            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // The named client bounds each request; this bounds the whole delivery, including a waited-out
        // Retry-After and its retry, and a provider that does its own work before it sends.
        timeout.CancelAfter(NotificationHttp.DeliveryBudget);

        try
        {
            await provider
                .SendAsync(message, NotificationSecrets.Read(notification.Settings), timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogSendTimedOut(_logger, notification.Name, (int)NotificationHttp.DeliveryBudget.TotalSeconds);
        }
        catch (NotificationSendException exception)
        {
            LogSendFailed(_logger, notification.Name, exception.Message);
        }
        catch (Exception exception)
        {
            // Only the type: a provider's own message may name the endpoint.
            LogSendFailed(_logger, notification.Name, exception.GetType().Name);
        }
    }

    /// <summary>Builds the message for a grab, an import, an upgrade or a failure.</summary>
    private static async Task<NotificationMessage?> BuildAsync(
        WondarrDbContext database,
        WorkItem item,
        CancellationToken cancellationToken)
    {
        var song = await LoadSongAsync(database, item.SongId, cancellationToken).ConfigureAwait(false);

        if (song is null)
        {
            return null;
        }

        var name = string.Concat(song.ArtistCredit, " – ", song.Title);

        switch (item.Event)
        {
            case NotificationEventNames.Grab:
                return await BuildGrabAsync(database, song, name, item, cancellationToken).ConfigureAwait(false);

            case NotificationEventNames.Import:
            case NotificationEventNames.Upgrade:
                return await BuildImportAsync(database, song, name, item, cancellationToken).ConfigureAwait(false);

            case NotificationEventNames.Failure:
                return await BuildFailureAsync(database, song, name, item, cancellationToken).ConfigureAwait(false);

            default:
                return null;
        }
    }

    private static async Task<NotificationMessage?> BuildGrabAsync(
        WondarrDbContext database,
        Song song,
        string name,
        WorkItem item,
        CancellationToken cancellationToken)
    {
        var queueItem = await database.QueueItems
            .FirstOrDefaultAsync(row => row.Id == item.QueueItemId, cancellationToken)
            .ConfigureAwait(false);

        if (queueItem is null)
        {
            return null;
        }

        var candidate = await database.Candidates
            .FirstOrDefaultAsync(row => row.Id == queueItem.CandidateId, cancellationToken)
            .ConfigureAwait(false);

        var quality = candidate is null
            ? null
            : await database.Qualities
                .Where(row => row.Id == candidate.QualityId)
                .Select(row => row.Name)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

        return new NotificationMessage(
            NotificationEventNames.Grab,
            string.Concat("Grabbed: ", name),
            string.Concat(
                candidate?.DisplayName ?? "a file",
                " from ",
                queueItem.SourceType,
                quality is null ? string.Empty : string.Concat(" (", quality, ")")))
        {
            Song = Song(song),
            Release = new NotificationRelease(
                candidate?.DisplayName ?? name,
                queueItem.SourceType,
                quality ?? "Unknown",
                queueItem.SizeBytes ?? candidate?.SizeBytes),
        };
    }

    private static async Task<NotificationMessage?> BuildImportAsync(
        WondarrDbContext database,
        Song song,
        string name,
        WorkItem item,
        CancellationToken cancellationToken)
    {
        var file = await database.SongFiles
            .Where(row => row.Id == item.SongFileId)
            .Select(row => new { row.Path, Quality = row.Quality.Name })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (file is null)
        {
            return null;
        }

        var upgraded = item.Event == NotificationEventNames.Upgrade;

        return new NotificationMessage(
            item.Event,
            string.Concat(upgraded ? "Upgraded: " : "Imported: ", name),
            string.Concat(file.Quality, " → ", file.Path))
        {
            Song = Song(song),
            File = new NotificationFile(file.Path, file.Quality),
            IsUpgrade = upgraded,
        };
    }

    private static async Task<NotificationMessage?> BuildFailureAsync(
        WondarrDbContext database,
        Song song,
        string name,
        WorkItem item,
        CancellationToken cancellationToken)
    {
        var queueItem = await database.QueueItems
            .FirstOrDefaultAsync(row => row.Id == item.QueueItemId, cancellationToken)
            .ConfigureAwait(false);

        if (queueItem is null)
        {
            return null;
        }

        var reason = string.IsNullOrWhiteSpace(queueItem.Message) ? "the download failed" : queueItem.Message;

        return new NotificationMessage(
            NotificationEventNames.Failure,
            string.Concat("Failed: ", name),
            reason)
        {
            Song = Song(song),
        };
    }

    /// <summary>
    /// The health results that were not failing last time. The first result set is the baseline and
    /// produces nothing; a result that is <see cref="HealthCheckResult.Ok"/> or
    /// <see cref="HealthCheckResult.Notice"/> is never an issue.
    /// </summary>
    private IReadOnlyList<NotificationMessage> NewIssues(IReadOnlyList<HealthCheck.HealthCheck> results)
    {
        var current = new HashSet<string>(StringComparer.Ordinal);
        var issues = new List<HealthCheck.HealthCheck>();

        foreach (var result in results)
        {
            if (result.Type is not (HealthCheckResult.Warning or HealthCheckResult.Error))
            {
                continue;
            }

            var key = string.Concat(result.Source, "\0", result.Message);

            current.Add(key);

            if (_previousHealth is { } previous && !previous.Contains(key))
            {
                issues.Add(result);
            }
        }

        var baseline = _previousHealth is null;
        _previousHealth = current;

        if (baseline)
        {
            return [];
        }

        return
        [
            .. issues.Select(issue => new NotificationMessage(
                NotificationEventNames.Health,
                string.Concat("Health: ", issue.Source),
                issue.Message)
            {
                Health = new NotificationHealth(
                    issue.Source,
                    issue.Type == HealthCheckResult.Error ? "error" : "warning",
                    issue.Message,
                    issue.WikiUrl?.ToString()),
            }),
        ];
    }

    private static Task<Song?> LoadSongAsync(
        WondarrDbContext database,
        long songId,
        CancellationToken cancellationToken) =>
        database.Songs
            .Include(song => song.AlbumContext)
            .FirstOrDefaultAsync(song => song.Id == songId, cancellationToken);

    private static bool Subscribed(Notification notification, string @event)
    {
        var events = NotificationSecrets.Read(notification.Events);

        if (events.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var entry in events.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String &&
                string.Equals(entry.GetString(), @event, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static NotificationSong Song(Domain.Song song) => new(
        song.Id,
        song.Title,
        song.ArtistCredit,
        song.AlbumContext?.AlbumTitle,
        song.MbRecordingId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped a {Event} notification: the row it is about is gone")]
    private static partial void LogDropped(ILogger logger, string @event);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The notification queue is full; dropped an event (reported at most once a minute)")]
    private static partial void LogQueueFull(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {Name} did not send: {Reason}")]
    private static partial void LogSendFailed(ILogger logger, string name, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {Name} did not send within {Seconds} seconds")]
    private static partial void LogSendTimedOut(ILogger logger, string name, int seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {Name} names the unknown provider {Provider}")]
    private static partial void LogNoProvider(ILogger logger, string name, string provider);

    [LoggerMessage(Level = LogLevel.Error, Message = "A {Event} notification could not be prepared")]
    private static partial void LogDispatchFailed(ILogger logger, string @event, Exception exception);

    /// <summary>
    /// One event waiting to be turned into a message. Deliberately small: everything the loop needs is
    /// an id it reads back inside its own scope, never a snapshot taken on the publisher's thread.
    /// </summary>
    /// <param name="Event">The <see cref="NotificationEventNames"/> value to send.</param>
    /// <param name="SongId">The song, when the event has one.</param>
    /// <param name="QueueItemId">The queue item, for a grab or a failure.</param>
    /// <param name="SongFileId">The library file, for an import.</param>
    /// <param name="IsUpgrade">Whether the import replaced a file.</param>
    /// <param name="Results">The health results, for a health event.</param>
    private sealed record WorkItem(
        string Event,
        long SongId = 0,
        long QueueItemId = 0,
        long SongFileId = 0,
        bool IsUpgrade = false,
        IReadOnlyList<HealthCheck.HealthCheck>? Results = null);
}
