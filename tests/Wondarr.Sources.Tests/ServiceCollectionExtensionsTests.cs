using Wondarr.Sources.Slskd;
using Wondarr.Sources.Torznab;
using Wondarr.Sources.YouTube;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Wondarr.Sources.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddWondarrSlskd_returns_the_same_service_collection()
    {
        // A real collection: AddHttpClient inspects what is already registered.
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder().Build();

        services.AddWondarrSlskd(configuration).Should().BeSameAs(services);
    }

    [Fact]
    public void AddWondarrYouTube_returns_the_same_service_collection()
    {
        // A real collection: AddOptions and AddHttpClient inspect what is already registered.
        var services = new ServiceCollection();

        services.AddWondarrYouTube().Should().BeSameAs(services);
    }

    [Fact]
    public void AddWondarrTorznab_returns_the_same_service_collection()
    {
        var services = Substitute.For<IServiceCollection>();

        services.AddWondarrTorznab().Should().BeSameAs(services);
    }
}
