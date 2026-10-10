using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wondarr.Api.Authentication;
using Wondarr.Api.Frontend;
using Wondarr.Api.SignalR;
using Wondarr.Core.Configuration;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Importing;
using Wondarr.Core.Jobs;
using Wondarr.Core.Messaging;
using Wondarr.Core.Updates;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Wondarr.Api.Extensions;

/// <summary>Registers the HTTP layer: controllers, authentication, authorization and data protection.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the controllers and the authentication pipeline the *arrs use: an API key on
    /// <c>/api/v1</c> by default, and the configured UI method for everything else.
    /// </summary>
    public static IServiceCollection AddWondarrApi(this IServiceCollection services, WondarrPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            });

        // Login cookies are encrypted with the data protection key ring, so it has to outlive the process.
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(paths.ConfigDir, "asp")));

        services.AddSingleton<IAuthorizationPolicyProvider, UiAuthorizationPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, UiAuthorizationHandler>();

        services.AddAuthorization(options =>
        {
            options.AddPolicy("SignalR", policy =>
            {
                policy.AddAuthenticationSchemes("SignalR");
                policy.RequireAuthenticatedUser();
            });

            // Require authentication on everything except the actions marked [AllowAnonymous].
            options.FallbackPolicy = new AuthorizationPolicyBuilder("API")
                .RequireAuthenticatedUser()
                .Build();
        });

        services.AddAppAuthentication();

        AddSignalREvents(services);

        // One document, "v1", snapshotted by tests/Wondarr.Api.Tests/OpenApiSnapshotTests.cs.
        services.AddOpenApi("v1", options =>
            options.AddDocumentTransformer(new WondarrDocumentTransformer().TransformAsync));

        return services;
    }

    /// <summary>
    /// Adds the events hub and the relays that feed it. The protocol is configured to match the REST
    /// API exactly — camelCase names and enums as strings — so a resource looks the same whichever
    /// way the UI received it.
    /// </summary>
    private static void AddSignalREvents(IServiceCollection services)
    {
        services.AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            });

        services.AddSingleton<SignalRBroadcaster>();

        // The relays are stateless and the aggregator resolves handlers from a scope, so registering
        // them as singletons keeps one broadcaster call path for the whole process.
        services.AddSingleton<IHandle<CommandUpdatedEvent>, CommandEventsRelay>();
        services.AddSingleton<IHandle<HealthCheckCompletedEvent>, HealthEventsRelay>();
        services.AddSingleton<IHandle<QueueItemChangedEvent>, QueueEventsRelay>();
        services.AddSingleton<IHandle<SongImportedEvent>, QueueEventsRelay>();
        services.AddSingleton<IHandle<UpdateCheckedEvent>, UpdateEventsRelay>();
    }
}
