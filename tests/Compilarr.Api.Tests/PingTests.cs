using System.Net;
using FluentAssertions;
using Xunit;

namespace Compilarr.Api.Tests;

public sealed class PingTests : IDisposable
{
    private readonly CompilarrAppFactory _factory = new();

    [Fact]
    public async Task Ping_returns_200_with_an_OK_status()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/ping", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Trim().Should().Be("""{"status":"OK"}""");
    }

    [Fact]
    public async Task Ping_needs_no_credentials()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/ping", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Should().NotContain(header => header.Key == "WWW-Authenticate");
    }

    [Fact]
    public async Task Ping_is_reachable_with_and_without_the_url_base()
    {
        using var factory = new CompilarrAppFactory(TestConfiguration.Of(("Server:UrlBase", "/compilarr")));
        using var client = factory.CreateClient();

        using var withoutUrlBase = await client.GetAsync(new Uri("/ping", UriKind.Relative));
        using var withUrlBase = await client.GetAsync(new Uri("/compilarr/ping", UriKind.Relative));

        withoutUrlBase.StatusCode.Should().Be(HttpStatusCode.OK);
        withUrlBase.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public void Dispose() => _factory.Dispose();
}
