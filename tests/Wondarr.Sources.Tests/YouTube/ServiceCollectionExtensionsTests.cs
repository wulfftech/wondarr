using Wondarr.Sources.YouTube;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// What <see cref="ServiceCollectionExtensions.AddWondarrYouTube"/> registers: the client behind its
/// interface, the named <see cref="HttpClient"/> it posts through, and the options validator.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void Registers_the_client_the_named_http_client_and_the_validator()
    {
        var services = new ServiceCollection();

        services.AddWondarrYouTube();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IInnertubeClient>().Should().BeOfType<InnertubeClient>();
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(InnertubeClient.HttpClientName)
            .Should().NotBeNull();
        provider.GetRequiredService<IValidateOptions<YouTubeOptions>>().Should().BeOfType<YouTubeOptionsValidator>();
        provider.GetRequiredService<IOptions<YouTubeOptions>>().Value.SearchLimit.Should().Be(20);
    }

    [Fact]
    public void Calling_it_twice_is_harmless()
    {
        var services = new ServiceCollection();

        services.AddWondarrYouTube();
        services.AddWondarrYouTube();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IInnertubeClient>().Should().BeOfType<InnertubeClient>();
        provider.GetServices<IValidateOptions<YouTubeOptions>>().Should().ContainSingle();
    }
}
