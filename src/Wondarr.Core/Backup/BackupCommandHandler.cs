using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.Jobs;

namespace Wondarr.Core.Backup;

/// <summary>
/// The scheduler's <c>Backup</c> task (ARCHITECTURE §5.5): one scheduled backup, then the retention
/// pass that deletes the scheduled backups older than <c>backup.retention_days</c>.
/// </summary>
public sealed class BackupCommandHandler : ICommandHandler
{
    /// <summary>The name the <c>Backup</c> scheduled task queues.</summary>
    public const string CommandName = "Backup";

    private readonly IServiceScopeFactory _scopes;

    /// <summary>Initialises a new instance of the <see cref="BackupCommandHandler"/> class.</summary>
    /// <param name="scopes">
    /// Builds the scope the backup service runs in: the handler is resolved whenever the command
    /// queue asks which handlers exist, so the service itself is only resolved once a backup runs.
    /// </param>
    public BackupCommandHandler(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        _scopes = scopes;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var scope = _scopes.CreateScope();
        var backups = scope.ServiceProvider.GetRequiredService<IBackupService>();

        var backup = await backups.CreateAsync(BackupType.Scheduled, cancellationToken).ConfigureAwait(false);
        var removed = await backups.CleanUpAsync(cancellationToken).ConfigureAwait(false);

        return $"{backup.Name} created; {removed} expired scheduled backup(s) removed";
    }
}