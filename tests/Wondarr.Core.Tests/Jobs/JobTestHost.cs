using Wondarr.Core.Metadata;
using System.Threading.Channels;
using Wondarr.Core.Jobs;
using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Wondarr.Core.Tests.Jobs;

/// <summary>
/// A core container wired the way the application wires it (a real SQLite file, the real queue and
/// aggregator) with the executor left out, so a test can start and stop it deliberately.
/// </summary>
internal sealed class JobTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _directory;

    private JobTestHost(ServiceProvider provider, string directory, string databasePath, FakeTimeProvider timeProvider)
    {
        _provider = provider;
        _directory = directory;
        DatabasePath = databasePath;
        TimeProvider = timeProvider;
    }

    /// <summary>The SQLite file behind the container.</summary>
    public string DatabasePath { get; }

    /// <summary>The clock every registration and the executor read.</summary>
    public FakeTimeProvider TimeProvider { get; }

    /// <summary>The command queue under test.</summary>
    public ICommandQueue Queue => _provider.GetRequiredService<ICommandQueue>();

    /// <summary>Builds the container, migrates it, and returns it.</summary>
    /// <param name="configure">Extra registrations, used to add test handlers.</param>
    public static async Task<JobTestHost> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wondarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "wondarr.db");
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

        var services = new ServiceCollection();
        services.AddLogging();

        // Registered before AddWondarrCore, whose TryAddSingleton would otherwise win.
        services.AddSingleton<TimeProvider>(timeProvider);
        services.AddWondarrPersistence($"Data Source={databasePath}");
        services.AddWondarrCore();

        // Command handlers (BulkAddSongs) depend on the metadata services, as they do in the app.
        services.AddWondarrMetadata(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        configure?.Invoke(services);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken.None);
        }

        return new JobTestHost(provider, directory, databasePath, timeProvider);
    }

    /// <summary>The real event aggregator, for a test that wraps it.</summary>
    public IEventAggregator EventAggregator => _provider.GetRequiredService<IEventAggregator>();

    /// <summary>Builds the executor over this container's services.</summary>
    /// <param name="aggregator">Replaces the container's event aggregator, to inject a fault.</param>
    public CommandExecutor CreateExecutor(IEventAggregator? aggregator = null) => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        aggregator ?? _provider.GetRequiredService<IEventAggregator>(),
        _provider.GetRequiredService<Channel<long>>(),
        TimeProvider,
        NullLogger<CommandExecutor>.Instance);

    /// <summary>Writes a command row directly, as a process that died mid-command would leave it.</summary>
    public async Task<long> InsertCommandAsync(string name, CommandStatus status)
    {
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var record = new CommandRecord
        {
            Name = name,
            Status = status,
            Result = CommandResult.Unknown,
            QueuedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };

        context.Commands.Add(record);
        await context.SaveChangesAsync(CancellationToken.None);

        return record.Id;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();

        // Windows keeps the file handle until the pooled connections are gone.
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(
            new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_directory, "wondarr.db")}"));

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
