using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Compilarr.Sources.Slskd;

/// <summary>
/// Registers the services owned by Compilarr.Sources.Slskd.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Soulseek settings, the <c>slskd.yml</c> renderer, the runtime-secret store and the
    /// typed slskd client. The bundled process itself is supervised by P0-09.
    /// </summary>
    public static IServiceCollection AddCompilarrSlskd(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SoulseekOptions>()
            .Bind(configuration.GetSection("Soulseek"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<SoulseekOptions>, SoulseekOptionsValidator>();
        services.AddSingleton<SlskdConfigRenderer>();
        services.AddScoped<SlskdSecretsStore>();

        // No retry policy: the supervisor decides whether an unreachable slskd is restarted, and a
        // retry loop here would hide that from it.
        services.AddHttpClient<ISlskdClient, SlskdClient>(client => client.Timeout = TimeSpan.FromSeconds(5));

        return services;
    }
}
