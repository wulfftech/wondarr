using System.Threading.Channels;
using Compilarr.Core.Authentication;
using Compilarr.Core.Configuration;
using Compilarr.Core.HealthCheck;
using Compilarr.Core.Jobs;
using Compilarr.Core.Logging;
using Compilarr.Core.Messaging;
using Compilarr.Core.Persistence;
using Compilarr.Core.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quartz;
using Microsoft.Extensions.Options;

namespace Compilarr.Core;

/// <summary>
/// Registers the services owned by Compilarr.Core.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the domain, decision engine, import pipeline, metadata and persistence services.
    /// </summary>
    public static IServiceCollection AddCompilarrCore(this IServiceCollection services)
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

        // The queue writes ids here and the executor drains it; both resolve the same instance, so
        // this must stay a singleton.
        services.TryAddSingleton(Channel.CreateUnbounded<long>());
        services.AddSingleton<ICommandQueue, CommandQueue>();

        services.AddScoped<ICommandHandler, HeartbeatCommandHandler>();
        services.AddScoped<ICommandHandler, CheckHealthCommandHandler>();

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

        // Registered here rather than in AddCompilarrPersistence so it starts after the migration
        // hosted service (hosted services start in registration order) and the command table exists.
        services.AddHostedService<CommandExecutor>();

        return services;
    }

    /// <summary>
    /// Registers the resolved paths and the validated, bound <see cref="ServerOptions"/>.
    /// </summary>
    public static IServiceCollection AddCompilarrConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        CompilarrPaths paths)
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
    /// <param name="connectionString">The SQLite connection string (for example <c>Data Source=/config/compilarr.db</c>).</param>
    public static IServiceCollection AddCompilarrPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<CompilarrDbContext>(options => options
            .UseSqlite(connectionString)
            .UseSnakeCaseNamingConvention());
        services.AddScoped<ISettingsRepository, SettingsRepository>();
        services.AddScoped<DatabaseMigrator>();
        services.AddHostedService<DatabaseMigrationHostedService>();

        return services;
    }
}
