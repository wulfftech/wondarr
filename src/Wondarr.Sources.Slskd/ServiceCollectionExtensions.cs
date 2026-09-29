using Wondarr.Core.Configuration;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// Registers the services owned by Wondarr.Sources.Slskd.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Soulseek settings, the <c>slskd.yml</c> renderer, the runtime-secret store, the
    /// typed slskd client and the supervisor that owns the bundled slskd process.
    /// </summary>
    /// <remarks>
    /// Call this <em>after</em> the persistence registration: the supervisor is a hosted service, and
    /// hosting starts hosted services in registration order, so it must come after the one that
    /// migrates and seeds the database the generated slskd secrets are read from.
    /// </remarks>
    public static IServiceCollection AddWondarrSlskd(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISecretRegistry, SecretRegistry>();

        services.AddOptions<SoulseekOptions>()
            .Bind(configuration.GetSection("Soulseek"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<SoulseekOptions>, SoulseekOptionsValidator>();

        // The shares default depends on whether config.yml mentions the key at all, which is not
        // something the binder can see; the post-configure applies it after binding.
        services.AddSingleton<IPostConfigureOptions<SoulseekOptions>, SoulseekOptionsPostConfigure>();
        services.AddSingleton<SlskdConfigRenderer>();
        services.AddScoped<SlskdSecretsStore>();

        // No retry policy: the supervisor decides whether an unreachable slskd is restarted, and a
        // retry loop here would hide that from it.
        services.AddHttpClient<ISlskdClient, SlskdClient>(client => client.Timeout = TimeSpan.FromSeconds(5));

        // Each call is small (slskd is on loopback); the runner's wall-clock token bounds a whole search,
        // this bounds one hung request.
        services.AddHttpClient<ISlskdSearchApi, SlskdSearchApi>(client => client.Timeout = TimeSpan.FromSeconds(15));

        // The budget is process-wide — one slskd account, one 30-per-4-minutes allowance — and the
        // runner is a singleton that resolves the typed client per run from a scope of its own.
        services.AddSingleton<ISoulseekSearchBudget, SoulseekSearchBudget>();
        services.AddSingleton<ISlskdSearchRunner, SlskdSearchRunner>();

        // Resolved by a factory, because the environment is a plain IDictionary rather than a service;
        // it is read once at start-up, exactly as WondarrPaths.Resolve reads it.
        services.AddSingleton<ISoulseekSettingsService>(provider => new SoulseekSettingsService(
            provider.GetRequiredService<IOptionsMonitor<SoulseekOptions>>(),
            provider.GetRequiredService<IConfigFileWriter>(),
            provider.GetRequiredService<IValidateOptions<SoulseekOptions>>(),
            Environment.GetEnvironmentVariables(),
            provider.GetRequiredService<ILogger<SoulseekSettingsService>>()));

        services.AddSingleton<IProcessLauncher, ProcessLauncher>();
        services.AddSingleton<SlskdStatus>();
        services.AddSingleton<IHealthCheck, SlskdHealthCheck>();
        services.AddSingleton<IHealthCheck, SlskdDownloadFolderHealthCheck>();
        services.AddSingleton<IHealthCheck, SoulseekSharingHealthCheck>();
        services.AddHostedService<SlskdHost>();

        return services;
    }
}
