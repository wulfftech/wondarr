using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wondarr.Core.Logging;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.Notifications;

/// <summary>
/// At startup, hands the secrets of every stored notification to the log redactor, so a password or a
/// webhook URL is redacted from the first log line after a restart. Registered after the migration
/// service, so the table is there.
/// </summary>
public sealed class NotificationSecretRegistrar : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IReadOnlyList<INotificationProvider> _providers;
    private readonly ISecretRegistry _secrets;

    /// <summary>Initialises a new instance of the <see cref="NotificationSecretRegistrar"/> class.</summary>
    /// <param name="scopes">Creates the scope the database is read in.</param>
    /// <param name="providers">Every registered provider, which say which settings are secret.</param>
    /// <param name="secrets">The redactor's secret registry.</param>
    public NotificationSecretRegistrar(
        IServiceScopeFactory scopes,
        IEnumerable<INotificationProvider> providers,
        ISecretRegistry secrets)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(secrets);

        _scopes = scopes;
        _providers = [.. providers];
        _secrets = secrets;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        List<Domain.Notification> notifications;

        try
        {
            notifications = await database.Notifications.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            // No table yet (a host that did not migrate): the secrets are registered as they are saved.
            return;
        }

        foreach (var notification in notifications)
        {
            var provider = _providers.FirstOrDefault(candidate =>
                string.Equals(candidate.Implementation, notification.Type, StringComparison.OrdinalIgnoreCase));

            if (provider is not null)
            {
                NotificationSecrets.Register(_secrets, NotificationSecrets.Read(notification.Settings), provider.Fields);
            }
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
