using System.Threading.Channels;
using Wondarr.Core.Authentication;
using Wondarr.Core.Blocklisting;
using Wondarr.Core.Configuration;
using Wondarr.Core.Decisions;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.History;
using Wondarr.Core.ImportLists;
using Wondarr.Core.Jobs;
using Wondarr.Core.Logging;
using Wondarr.Core.Messaging;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Profiles;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Wondarr.Core.Wanted;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quartz;
using Microsoft.Extensions.Options;

namespace Wondarr.Core;

/// <summary>
/// Registers the services owned by Wondarr.Core.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the domain, decision engine, import pipeline, metadata and persistence services.
    /// </summary>
    public static IServiceCollection AddWondarrCore(this IServiceCollection services)
    {
        services.AddScoped<ICredentialStore, CredentialStore>();

        // Profiles and libraries are edited through the API and read by the decision engine; both use
        // the scoped DbContext, so they are scoped too.
        services.AddScoped<IQualityDefinitionService, QualityDefinitionService>();
        services.AddScoped<IQualityProfileService, QualityProfileService>();
        services.AddScoped<ILibraryService, LibraryService>();

        services.TryAddSingleton(TimeProvider.System);

        // DatabaseHealthCheck needs the scoped DbContext; the folder checks only need the paths.
        services.AddScoped<IHealthCheck, DatabaseHealthCheck>();
        services.AddSingleton<IHealthCheck, ConfigFolderHealthCheck>();
        services.AddSingleton<IHealthCheck, LogFolderHealthCheck>();
        services.AddSingleton<HealthCheckService>();

        services.AddSingleton<IEventAggregator, EventAggregator>();

        // The album policy engine is pure; its only dependency is the source of synthetic album ids.
        services.AddSingleton<IAlbumPolicyEngine>(new AlbumPolicyEngine(Guid.NewGuid));

        // The decision engine is pure and stateless: every candidate is judged from the context alone.
        services.AddSingleton<DecisionEngine>();

        // The queue writes ids here and the executor drains it; both resolve the same instance, so
        // this must stay a singleton.
        services.TryAddSingleton(Channel.CreateUnbounded<long>());
        services.AddSingleton<ICommandQueue, CommandQueue>();

        services.AddScoped<ICommandHandler, HeartbeatCommandHandler>();
        services.AddScoped<ICommandHandler, CheckHealthCommandHandler>();
        services.AddScoped<ICommandHandler, BulkAddSongsCommandHandler>();

        // The pasted-list pipeline: stored by the API, processed by the BulkAddSongs command.
        services.AddScoped<IPasteListService, PasteListService>();

        // The write side of the song lifecycle: resolved identities become songs and album contexts.
        services.AddScoped<ISongService, SongService>();
        services.AddScoped<IArtistService, ArtistService>();

        // The read side of the song lifecycle: wanted lists, history and the blocklist.
        services.AddScoped<IWantedService, WantedService>();
        services.AddScoped<IHistoryService, HistoryService>();
        services.AddScoped<IBlocklistService, BlocklistService>();

        // What the search-and-grab loop records: the runs and their candidates, the download queue, and
        // what we know about the Soulseek peers. All three read and write the scoped DbContext.
        services.AddScoped<ISearchRunService, SearchRunService>();
        services.AddScoped<IQueueService, QueueService>();
        services.AddScoped<ISoulseekUserService, SoulseekUserService>();

        services.AddSingleton<IScheduledTaskCatalog, ScheduledTaskCatalog>();
        services.AddScoped<IHandle<CommandUpdatedEvent>, JobTableUpdater>();

        // The scheduler is a RAM store: the durable state is the job table, which ScheduledTaskService
        // reads back at every start (ARCHITECTURE §5.5).
        services.AddQuartz();

        // Same ordering reason as the executor below: the schedule is built after the migration
        // hosted service has created the job table, and the Quartz hosted service that starts the
        // scheduler runs after this one so every trigger is registered before it does.
        services.AddHostedService<ScheduledTaskService>();
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

        // Registered here rather than in AddWondarrPersistence so it starts after the migration
        // hosted service (hosted services start in registration order) and the command table exists.
        services.AddHostedService<CommandExecutor>();

        return services;
    }

    /// <summary>
    /// Registers the resolved paths and the validated, bound <see cref="ServerOptions"/>.
    /// </summary>
    public static IServiceCollection AddWondarrConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        WondarrPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton(paths);

        services.AddOptions<ServerOptions>()
            .Bind(configuration.GetSection("Server"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<ServerOptions>, ServerOptionsValidator>();
        services.AddSingleton<IPostConfigureOptions<ServerOptions>, ServerOptionsPostConfigure>();

        services.AddOptions<LogOptions>()
            .Bind(configuration.GetSection("Log"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<LogOptions>, LogOptionsValidator>();
        services.AddSingleton<ISecretRegistry, SecretRegistry>();

        return services;
    }

    /// <summary>
    /// Adds the SQLite database, the settings repository and the startup migrator.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="connectionString">The SQLite connection string (for example <c>Data Source=/config/wondarr.db</c>).</param>
    public static IServiceCollection AddWondarrPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<WondarrDbContext>(options => options
            .UseSqlite(connectionString)
            .UseSnakeCaseNamingConvention());
        services.AddScoped<ISettingsRepository, SettingsRepository>();
        services.AddScoped<DatabaseMigrator>();
        services.AddHostedService<DatabaseMigrationHostedService>();

        return services;
    }
}
