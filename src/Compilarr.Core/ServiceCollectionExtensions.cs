using Compilarr.Core.Authentication;
using Compilarr.Core.Configuration;
using Compilarr.Core.HealthCheck;
using Compilarr.Core.Logging;
using Compilarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        services.TryAddSingleton(TimeProvider.System);

        // DatabaseHealthCheck needs the scoped DbContext; the folder checks only need the paths.
        services.AddScoped<IHealthCheck, DatabaseHealthCheck>();
        services.AddSingleton<IHealthCheck, ConfigFolderHealthCheck>();
        services.AddSingleton<IHealthCheck, LogFolderHealthCheck>();
        services.AddSingleton<HealthCheckService>();

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
