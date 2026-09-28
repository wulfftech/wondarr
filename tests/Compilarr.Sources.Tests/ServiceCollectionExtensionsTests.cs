using Compilarr.Sources.Slskd;
using Compilarr.Sources.Torznab;
using Compilarr.Sources.YouTube;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Compilarr.Sources.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCompilarrSlskd_returns_the_same_service_collection()
    {
        // A real collection: AddHttpClient inspects what is already registered.
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder().Build();

        services.AddCompilarrSlskd(configuration).Should().BeSameAs(services);
    }

    [Fact]
    public void AddCompilarrYouTube_returns_the_same_service_collection()
    {
        var services = Substitute.For<IServiceCollection>();

        services.AddCompilarrYouTube().Should().BeSameAs(services);
    }

    [Fact]
    public void AddCompilarrTorznab_returns_the_same_service_collection()
    {
        var services = Substitute.For<IServiceCollection>();

        services.AddCompilarrTorznab().Should().BeSameAs(services);
    }
}
