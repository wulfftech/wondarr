using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Compilarr.Core.Persistence;

/// <summary>
/// Migrates the database during startup, so it is ready before the server accepts requests.
/// </summary>
public sealed partial class DatabaseMigrationHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseMigrationHostedService> _logger;

    /// <summary>Initialises a new instance of the <see cref="DatabaseMigrationHostedService"/> class.</summary>
    public DatabaseMigrationHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseMigrationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        LogApplyingMigrations();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var migrator = scope.ServiceProvider.GetRequiredService<DatabaseMigrator>();
        await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying database migrations")]
    private partial void LogApplyingMigrations();
}
