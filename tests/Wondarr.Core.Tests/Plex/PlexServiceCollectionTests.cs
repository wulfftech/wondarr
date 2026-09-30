using System.Net.Http;
using Wondarr.Core.Plex;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wondarr.Core.Tests.Plex;

public class PlexServiceCollectionTests
{
    [Fact]
    public void The_Plex_options_and_clients_are_wired_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Plex:RequestTimeoutSeconds"] = "7" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWondarrPlex(configuration);

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        factory.CreateClient(PlexServerClient.ClientName).Timeout.Should().Be(TimeSpan.FromSeconds(7));
        factory.CreateClient(TypedClientName).Timeout.Should().Be(TimeSpan.FromSeconds(7));

        provider.GetRequiredService<IPlexTvClient>().Should().BeOfType<PlexTvClient>();
    }

    [Fact]
    public void Neither_Plex_client_follows_a_redirect()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWondarrPlex(configuration);

        using var provider = services.BuildServiceProvider();

        AssertNoRedirects(provider, PlexServerClient.ClientName);
        AssertNoRedirects(provider, TypedClientName);
    }

    /// <summary>
    /// The name a typed client is registered under: <c>AddHttpClient&lt;TClient, TImplementation&gt;</c>
    /// names the client after <c>TClient</c>, as the type-display name without its namespace.
    /// </summary>
    private static string TypedClientName => typeof(IPlexTvClient).Name;

    /// <summary>
    /// Walks from the handler the factory hands out, through any delegating handler in front of it,
    /// down to the primary one.
    /// </summary>
    private static void AssertNoRedirects(IServiceProvider provider, string clientName)
    {
        var handlers = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        HttpMessageHandler? handler = handlers.CreateHandler(clientName);

        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler;
        }

        handler.Should().BeOfType<SocketsHttpHandler>().Which.AllowAutoRedirect.Should().BeFalse();
    }
}
