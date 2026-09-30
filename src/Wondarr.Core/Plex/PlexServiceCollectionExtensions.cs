using Wondarr.Core.HealthCheck;
using Wondarr.Core.Importing;
using Wondarr.Core.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Plex;

/// <summary>Registers the services owned by the Plex namespace.</summary>
public static class PlexServiceCollectionExtensions
{
    /// <summary>
    /// Adds the <c>plex</c> options and its validator, the plex.tv client, the named
    /// <see cref="PlexServerClient.ClientName"/> client, the connection service, the partial-scan
    /// updater and its health check.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configuration">Configuration to bind the <c>plex</c> section from.</param>
    public static IServiceCollection AddWondarrPlex(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<PlexOptions>()
            .Bind(configuration.GetSection("Plex"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<PlexOptions>, PlexOptionsValidator>();

        services.AddHttpClient<IPlexTvClient, PlexTvClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<PlexOptions>>().Value;

            // Trailing slash included, so every relative path appends rather than replaces.
            client.BaseAddress = new Uri(WithTrailingSlash(options.PlexTvBaseUrl), UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        })
            .ConfigurePrimaryHttpMessageHandler(NoRedirects);

        // No retry pipeline on the server client: a retried refresh is at best wasted work, and a
        // retried emptyTrash mid-compaction is not what the caller asked for. The metadata clients
        // get their resilience handlers because a retried lookup really is harmless.
        services.AddHttpClient(PlexServerClient.ClientName, (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<PlexOptions>>().Value;

            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        })
            .ConfigurePrimaryHttpMessageHandler(NoRedirects);

        services.AddSingleton<PlexClientIdentifierState>();
        services.AddScoped<IPlexClientIdentifier, PlexClientIdentifier>();
        services.AddScoped<IPlexServerClient, PlexServerClient>();
        services.AddScoped<IPlexConnectionService, PlexConnectionService>();

        // One instance is the import handler, the hosted service and the interface adoption and the
        // compact task ask for: the pending batch lives in it, so a second copy would scan twice.
        services.AddSingleton<PlexLibraryUpdater>();
        services.AddSingleton<IPlexLibraryUpdater>(provider => provider.GetRequiredService<PlexLibraryUpdater>());
        services.AddSingleton<IHandle<SongImportedEvent>>(provider => provider.GetRequiredService<PlexLibraryUpdater>());
        services.AddHostedService(provider => provider.GetRequiredService<PlexLibraryUpdater>());

        // Scoped like the database check: reading the connection needs the scoped DbContext.
        services.AddScoped<IHealthCheck, PlexHealthCheck>();

        return services;
    }

    /// <summary>
    /// A primary handler that never follows a redirect. Plex answers a moved resource with a 3xx to
    /// another host, and a header such as <c>X-Plex-Token</c> survives that hop, so following one
    /// would hand the user's token to whoever the server named. A 3xx is left to become the ordinary
    /// failure it is.
    /// </summary>
    private static HttpMessageHandler NoRedirects() => new SocketsHttpHandler { AllowAutoRedirect = false };

    private static string WithTrailingSlash(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();

        return trimmed.Length == 0 || trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }
}
