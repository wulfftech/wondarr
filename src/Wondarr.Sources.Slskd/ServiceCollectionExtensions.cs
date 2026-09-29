using Wondarr.Core.HealthCheck;
using Wondarr.Core.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.AddSingleton<SlskdConfigRenderer>();
        services.AddScoped<SlskdSecretsStore>();

        // No retry policy: the supervisor decides whether an unreachable slskd is restarted, and a
        // retry loop here would hide that from it.
        services.AddHttpClient<ISlskdClient, SlskdClient>(client => client.Timeout = TimeSpan.FromSeconds(5));

        // Each call is small (slskd is on loopback); the runner's wall-clock token bounds a whole search,
        // this bounds one hung request.
        services.AddHttpClient<ISlskdSearchApi, SlskdSearchApi>(client => client.Timeout = TimeSpan.FromSeconds(15));

        // Enqueues and status reads are small, and slskd is on loopback: a slow one is a hung one.
        services.AddHttpClient<ISlskdTransferApi, SlskdTransferApi>(client => client.Timeout = TimeSpan.FromSeconds(10));

        // Downloads outlive any one request, so the service that owns them is a singleton and
        // resolves its typed client per call from a scope of its own.
        services.AddSingleton<ISlskdDownloads, SlskdDownloads>();

        // The budget is process-wide — one slskd account, one 30-per-4-minutes allowance — and the
        // runner is a singleton that resolves the typed client per run from a scope of its own.
        services.AddSingleton<ISoulseekSearchBudget, SoulseekSearchBudget>();
        services.AddSingleton<ISlskdSearchRunner, SlskdSearchRunner>();

        services.AddSingleton<IProcessLauncher, ProcessLauncher>();
        services.AddSingleton<SlskdStatus>();
        services.AddSingleton<IHealthCheck, SlskdHealthCheck>();
        services.AddHostedService<SlskdHost>();

        return services;
    }
}
