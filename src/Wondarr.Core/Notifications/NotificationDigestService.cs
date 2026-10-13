using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.Notifications;

/// <summary>
/// Holds back the events of digest-mode notifications and sends them as one message when the window
/// ends. Entries live in the database, so a restart loses nothing.
/// </summary>
/// <remarks>
/// <para>
/// One window per notification: it starts at the notification's first pending entry and ends
/// <see cref="IDigestNotificationProvider.DigestWindow"/> later. A notification that is no longer in
/// digest mode is flushed on the next tick. A disabled notification keeps what it holds, and sends it
/// when it is enabled again. An empty window sends nothing.
/// </para>
/// <para>
/// A failed send keeps its entries and is tried again after a growing wait. The third failure in a row is
/// an error in the log; the entries are still kept and tried again. A notification holds at most
/// <see cref="MaxPendingEntries"/> entries; the oldest are dropped with a warning.
/// </para>
/// </remarks>
public sealed partial class NotificationDigestService : BackgroundService
{
    /// <summary>The most entries one notification holds back; adding past it drops the oldest.</summary>
    public const int MaxPendingEntries = 500;

    /// <summary>How many failed sends in a row make an error.</summary>
    public const int ErrorAfterFailures = 3;

    /// <summary>How often the loop looks for a window that has ended.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopes;
    private readonly IReadOnlyList<INotificationProvider> _providers;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationDigestService> _logger;

    // Per notification id: failures in a row and when the next try may happen. Lost on a restart, which
    // only means an immediate retry.
    private readonly Dictionary<long, (int Failures, DateTimeOffset NextTry)> _retries = [];

    /// <summary>Initialises a new instance of the <see cref="NotificationDigestService"/> class.</summary>
    /// <param name="scopes">Creates the scope each tick reads the database in.</param>
    /// <param name="providers">Every registered provider.</param>
    /// <param name="time">The clock windows and waits are measured on.</param>
    /// <param name="logger">The log sink.</param>
    public NotificationDigestService(
        IServiceScopeFactory scopes,
        IEnumerable<INotificationProvider> providers,
        TimeProvider time,
        ILogger<NotificationDigestService> logger)
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

    /// <summary>
    /// Stores one event for a notification's next digest and drops the oldest entries past
    /// <see cref="MaxPendingEntries"/>.
    /// </summary>
    /// <param name="database">The database to write to.</param>
    /// <param name="notification">The notification holding the event back.</param>
    /// <param name="message">The message to hold.</param>
    /// <param name="logger">The log sink.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    internal static async Task AddAsync(
        WondarrDbContext database,
        Notification notification,
        NotificationMessage message,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(message);

        var entry = new NotificationDigestEntry
        {
            NotificationId = notification.Id,
            Event = message.Event,
            SongId = message.Song?.Id,
            Snapshot = JsonSerializer.Serialize(message, SnapshotJson),
        };

        database.NotificationDigestEntries.Add(entry);

        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The dispatcher reuses this context for the next notification: a failed row left tracked
            // would be retried, and fail again, with that one's save.
            database.Entry(entry).State = EntityState.Detached;

            throw;
        }

        // The id of the newest entry that is past the cap; it and everything older go in one statement.
        var cutoff = await database.NotificationDigestEntries
            .Where(row => row.NotificationId == notification.Id)
            .OrderByDescending(row => row.Id)
            .Skip(MaxPendingEntries)
            .Select(row => row.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (cutoff != 0)
        {
            var dropped = await database.NotificationDigestEntries
                .Where(row => row.NotificationId == notification.Id && row.Id <= cutoff)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            LogDropped(logger, notification.Name, dropped, MaxPendingEntries);
        }
    }

    /// <summary>Sends every digest whose window has ended. Never throws for one notification's failure.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    public async Task FlushDueAsync(CancellationToken cancellationToken)
    {
        List<long> ids;

        using (var scope = _scopes.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

            ids = await database.NotificationDigestEntries
                .Select(entry => entry.NotificationId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var id in ids)
        {
            try
            {
                await FlushOneAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogFlushFailed(_logger, id, exception);
            }
        }

        _retries.Keys.Where(key => !ids.Contains(key)).ToList().ForEach(key => _retries.Remove(key));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, _time);

        try
        {
            do
            {
                try
                {
                    await FlushDueAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogTickFailed(_logger, exception);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; everything pending is in the database.
        }
    }

    private async Task FlushOneAsync(long notificationId, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var notification = await database.Notifications
            .FirstOrDefaultAsync(row => row.Id == notificationId, cancellationToken)
            .ConfigureAwait(false);

        if (notification is null || !notification.Enabled)
        {
            return;
        }

        var provider = _providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Implementation, notification.Type, StringComparison.OrdinalIgnoreCase));

        var settings = NotificationSecrets.Read(notification.Settings);

        if (provider is not IDigestNotificationProvider digest)
        {
            await database.NotificationDigestEntries
                .Where(entry => entry.NotificationId == notificationId)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        var rows = await database.NotificationDigestEntries
            .Where(entry => entry.NotificationId == notificationId)
            .OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var first = new DateTimeOffset(DateTime.SpecifyKind(rows[0].CreatedAt, DateTimeKind.Utc));

        // No window means the notification left digest mode: send what is pending now.
        var window = digest.DigestWindow(settings);

        if (window is { } length && now - first < length)
        {
            return;
        }

        // The digest reports its own window, not the moment it happened to be flushed.
        var end = window is { } span ? first + span : now;

        if (_retries.TryGetValue(notificationId, out var retry) && now < retry.NextTry)
        {
            return;
        }

        var entries = new List<DigestEntry>(rows.Count);
        var unreadable = new List<NotificationDigestEntry>();

        foreach (var row in rows)
        {
            NotificationMessage? message = null;

            try
            {
                message = JsonSerializer.Deserialize<NotificationMessage>(row.Snapshot, SnapshotJson);
            }
            catch (JsonException)
            {
                // Handled below with the other entries nothing can be made of.
            }

            if (message is null)
            {
                unreadable.Add(row);
            }
            else
            {
                entries.Add(new DigestEntry(message, new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc))));
            }
        }

        if (unreadable.Count > 0)
        {
            database.NotificationDigestEntries.RemoveRange(unreadable);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            LogUnreadable(_logger, notification.Name, unreadable.Count);
        }

        if (entries.Count == 0)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NotificationHttp.DeliveryBudget);

        try
        {
            await digest
                .SendDigestAsync(entries, first, end, _time.LocalTimeZone, settings, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The message of a NotificationSendException names no secret; any other exception is only its type.
            Failed(notification, exception is NotificationSendException ? exception.Message : exception.GetType().Name, now, entries.Count);

            return;
        }

        _retries.Remove(notificationId);

        var sent = rows.Where(row => !unreadable.Contains(row)).Select(row => row.Id).ToList();

        await database.NotificationDigestEntries
            .Where(entry => sent.Contains(entry.Id))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        LogFlushed(_logger, notification.Name, entries.Count);
    }

    private void Failed(Notification notification, string reason, DateTimeOffset now, int held)
    {
        var failures = (_retries.TryGetValue(notification.Id, out var previous) ? previous.Failures : 0) + 1;
        var wait = TimeSpan.FromMinutes(Math.Min(MaxBackoff.TotalMinutes, 1 << Math.Min(failures, 10)));

        _retries[notification.Id] = (failures, now + wait);

        if (failures == ErrorAfterFailures)
        {
            LogDigestError(_logger, notification.Name, failures, held, reason);
        }
        else
        {
            LogDigestRetry(_logger, notification.Name, failures, (int)wait.TotalMinutes, reason);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {Name} holds more than {Max} digest entries; dropped the {Dropped} oldest")]
    private static partial void LogDropped(ILogger logger, string name, int dropped, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {Name} dropped {Count} digest entries it could not read")]
    private static partial void LogUnreadable(ILogger logger, string name, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {Name} did not send its digest ({Failures} in a row); trying again in {Minutes} minutes: {Reason}")]
    private static partial void LogDigestRetry(ILogger logger, string name, int failures, int minutes, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification {Name} did not send its digest {Failures} times in a row; keeping its {Held} entries and trying again: {Reason}")]
    private static partial void LogDigestError(ILogger logger, string name, int failures, int held, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {Name} sent a digest of {Count} events")]
    private static partial void LogFlushed(ILogger logger, string name, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The digest of notification {Id} could not be prepared")]
    private static partial void LogFlushFailed(ILogger logger, long id, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A digest pass failed")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
