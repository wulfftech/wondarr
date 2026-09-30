using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Notifications;

/// <summary>The values a create or an update carries.</summary>
/// <param name="Name">The display name.</param>
/// <param name="Implementation">The provider's implementation name.</param>
/// <param name="Enabled">Whether the dispatcher sends it.</param>
/// <param name="Events">The subscribed event names.</param>
/// <param name="Settings">The provider's settings.</param>
/// <param name="Id">The row a test is about, so a masked secret can be kept from it, or <see langword="null"/>.</param>
public sealed record NotificationDraft(
    string Name,
    string Implementation,
    bool Enabled,
    IReadOnlyList<string> Events,
    JsonElement Settings,
    long? Id = null);

/// <summary>The outcome of a test send.</summary>
/// <param name="Success">Whether the endpoint accepted the message.</param>
/// <param name="Error">Why it did not, or <see langword="null"/>. Never names the endpoint.</param>
public sealed record NotificationTestResult(bool Success, string? Error);

/// <summary>The stored notifications the API reads and writes.</summary>
public interface INotificationService
{
    /// <summary>Lists every notification, ordered by id.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<Notification>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Reads one notification, or <see langword="null"/>.</summary>
    /// <param name="id">The notification id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<Notification?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Stores a new notification.</summary>
    /// <param name="draft">The values to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<Notification> CreateAsync(NotificationDraft draft, CancellationToken cancellationToken);

    /// <summary>Replaces a notification, or returns <see langword="null"/> when there is no such row.</summary>
    /// <param name="id">The notification id.</param>
    /// <param name="draft">The values to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<Notification?> UpdateAsync(long id, NotificationDraft draft, CancellationToken cancellationToken);

    /// <summary>Deletes a notification, returning whether there was one.</summary>
    /// <param name="id">The notification id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);

    /// <summary>Sends a test message through the draft's provider.</summary>
    /// <param name="draft">The notification to test.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    Task<NotificationTestResult> TestAsync(NotificationDraft draft, CancellationToken cancellationToken);
}

/// <summary>
/// The rules a notification is stored under: the provider exists, the name is unique, every subscribed
/// event is one of <see cref="NotificationEventNames.All"/>, and the provider accepts the settings.
/// </summary>
public sealed partial class NotificationService : INotificationService
{
    private readonly WondarrDbContext _database;
    private readonly IReadOnlyList<INotificationProvider> _providers;
    private readonly ILogger<NotificationService> _logger;

    /// <summary>Initialises a new instance of the <see cref="NotificationService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="providers">Every registered provider.</param>
    /// <param name="logger">The log sink.</param>
    public NotificationService(
        WondarrDbContext database,
        IEnumerable<INotificationProvider> providers,
        ILogger<NotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _providers = [.. providers];
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Notification>> ListAsync(CancellationToken cancellationToken) =>
        await _database.Notifications
            .OrderBy(notification => notification.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<Notification?> GetAsync(long id, CancellationToken cancellationToken) =>
        _database.Notifications.FirstOrDefaultAsync(notification => notification.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<Notification> CreateAsync(NotificationDraft draft, CancellationToken cancellationToken)
    {
        var provider = await ValidateAsync(draft, excludingId: null, cancellationToken).ConfigureAwait(false);

        var notification = new Notification
        {
            Name = draft.Name.Trim(),
            Type = provider.Implementation,
            Enabled = draft.Enabled,
            Settings = NotificationSecrets.Merge(draft.Settings, null, provider.Fields),
            Events = JsonSerializer.Serialize(draft.Events),
        };

        _database.Notifications.Add(notification);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogSaved(_logger, notification.Id, notification.Type);

        return notification;
    }

    /// <inheritdoc />
    public async Task<Notification?> UpdateAsync(
        long id,
        NotificationDraft draft,
        CancellationToken cancellationToken)
    {
        var notification = await GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (notification is null)
        {
            return null;
        }

        var provider = await ValidateAsync(draft, excludingId: id, cancellationToken).ConfigureAwait(false);

        notification.Name = draft.Name.Trim();
        notification.Type = provider.Implementation;
        notification.Enabled = draft.Enabled;
        notification.Settings = NotificationSecrets.Merge(draft.Settings, notification.Settings, provider.Fields);
        notification.Events = JsonSerializer.Serialize(draft.Events);

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogSaved(_logger, notification.Id, notification.Type);

        return notification;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var notification = await GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (notification is null)
        {
            return false;
        }

        _database.Notifications.Remove(notification);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogDeleted(_logger, id);

        return true;
    }

    /// <inheritdoc />
    public async Task<NotificationTestResult> TestAsync(
        NotificationDraft draft,
        CancellationToken cancellationToken)
    {
        var provider = await ValidateAsync(draft, draft.Id, cancellationToken).ConfigureAwait(false);
        var settings = await ResolveSettingsAsync(draft, provider, cancellationToken).ConfigureAwait(false);

        var message = new NotificationMessage(
            NotificationEventNames.Test,
            "Test notification from Wondarr",
            "Wondarr reached this endpoint; nothing else was sent.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NotificationHttp.SendTimeout);

        try
        {
            await provider.SendAsync(message, settings, timeout.Token).ConfigureAwait(false);

            return new NotificationTestResult(true, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NotificationTestResult(
                false,
                $"the endpoint did not answer within {(int)NotificationHttp.SendTimeout.TotalSeconds} seconds.");
        }
        catch (NotificationSendException exception)
        {
            return new NotificationTestResult(false, exception.Message);
        }
        catch (Exception exception)
        {
            // Only the type: a provider's own message may name the endpoint.
            return new NotificationTestResult(false, exception.GetType().Name);
        }
    }

    /// <summary>
    /// Checks a draft and returns the provider it names. <paramref name="excludingId"/> is the row the
    /// write is about, which the uniqueness check must not count against itself.
    /// </summary>
    private async Task<INotificationProvider> ValidateAsync(
        NotificationDraft draft,
        long? excludingId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var provider = _providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Implementation, draft.Implementation, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotificationValidationException(
                "implementation",
                string.Concat("'", draft.Implementation, "' is not a known notification type."));

        var name = draft.Name?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            throw new NotificationValidationException("name", "A notification needs a name.");
        }

        var taken = await _database.Notifications
            .AnyAsync(
                notification => notification.Name == name && notification.Id != (excludingId ?? 0),
                cancellationToken)
            .ConfigureAwait(false);

        if (taken)
        {
            throw new NotificationValidationException(
                "name",
                string.Concat("A notification called '", name, "' already exists."));
        }

        foreach (var @event in draft.Events ?? [])
        {
            if (!NotificationEventNames.All.Contains(@event, StringComparer.Ordinal))
            {
                throw new NotificationValidationException(
                    "events",
                    string.Concat("'", @event, "' is not an event Wondarr sends."));
            }
        }

        if (provider.Validate(draft.Settings) is { Count: > 0 } failures)
        {
            throw new NotificationValidationException("settings", string.Join(" ", failures));
        }

        return provider;
    }

    /// <summary>
    /// The settings a send uses: the draft's own, with every masked secret replaced by the value stored
    /// on <paramref name="draft"/>'s row when it has one.
    /// </summary>
    private async Task<JsonElement> ResolveSettingsAsync(
        NotificationDraft draft,
        INotificationProvider provider,
        CancellationToken cancellationToken)
    {
        if (draft.Id is not { } id)
        {
            return draft.Settings;
        }

        var stored = await GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (stored is null)
        {
            return draft.Settings;
        }

        var merged = NotificationSecrets.Merge(draft.Settings, stored.Settings, provider.Fields);

        return NotificationSecrets.Read(merged);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved notification {Id} ({Type})")]
    private static partial void LogSaved(ILogger logger, long id, string type);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted notification {Id}")]
    private static partial void LogDeleted(ILogger logger, long id);
}
