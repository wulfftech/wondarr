using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Wondarr.Api.Tests;

public sealed class UrlBaseTests : IDisposable
{
    private readonly WondarrAppFactory _factory = new(TestConfiguration.Of(("Server:UrlBase", "/wondarr")));

    [Fact]
    public async Task Requests_without_the_url_base_are_redirected_to_it()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);

        using var response = await client.GetAsync(new Uri("/api/v1/auth/user", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        response.Headers.Location!.ToString().Should().Be("/wondarr/api/v1/auth/user");
    }

    [Fact]
    public async Task Requests_with_the_url_base_reach_the_api()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);

        using var response = await client.GetAsync(new Uri("/wondarr/api/v1/auth/user", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public void Dispose() => _factory.Dispose();
}
