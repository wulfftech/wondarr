using System.Net;
using FluentAssertions;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>R-059: HTML responses say who may frame them; API JSON does not need to.</summary>
public sealed class FrameProtectionTests : IDisposable
{
    private const string LocalAddress = "192.168.1.20";

    private readonly List<WondarrAppFactory> _factories = [];
    private readonly List<string> _webRoots = [];

    [Fact]
    public async Task The_spa_forbids_framing_by_other_sites_when_no_ancestors_are_listed()
    {
        using var factory = Create();
        using var client = factory.CreateClient(LocalAddress);

        using var response = await client.GetAsync(new Uri("/system/status", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Content-Security-Policy").Should().Equal("frame-ancestors 'self'");
        response.Headers.GetValues("X-Frame-Options").Should().Equal("SAMEORIGIN");
    }

    [Fact]
    public async Task The_spa_allows_the_listed_ancestors_and_drops_x_frame_options()
    {
        using var factory = Create(
            ("Server:FrameAncestors:0", "https://dash.example.lan"),
            ("Server:FrameAncestors:1", "http://homer.lan:8080"));
        using var client = factory.CreateClient(LocalAddress);

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Content-Security-Policy")
            .Should().Equal("frame-ancestors 'self' https://dash.example.lan http://homer.lan:8080");
        response.Headers.Contains("X-Frame-Options").Should().BeFalse();
    }

    [Fact]
    public async Task The_header_carries_the_normalised_origin()
    {
        using var factory = Create(("Server:FrameAncestors:0", "HTTPS://Dash.Example.LAN:443"));
        using var client = factory.CreateClient(LocalAddress);

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        response.Headers.GetValues("Content-Security-Policy").Should().Equal("frame-ancestors 'self' https://dash.example.lan");
    }

    [Fact]
    public async Task The_login_page_is_protected_too()
    {
        using var factory = Create();
        using var client = factory.CreateClient("8.8.8.8");

        using var response = await client.GetAsync(new Uri("/login", UriKind.Relative));

        response.Headers.GetValues("Content-Security-Policy").Should().Equal("frame-ancestors 'self'");
        response.Headers.GetValues("X-Frame-Options").Should().Equal("SAMEORIGIN");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Api_json_carries_neither_header(bool withAncestors)
    {
        using var factory = withAncestors
            ? Create(("Server:FrameAncestors:0", "https://dash.example.lan"))
            : Create();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.GetAsync(new Uri("/api/v1/system/status", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Content-Security-Policy").Should().BeFalse();
        response.Headers.Contains("X-Frame-Options").Should().BeFalse();
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        foreach (var root in _webRoots.Where(Directory.Exists))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private WondarrAppFactory Create(params (string Key, string Value)[] settings)
    {
        var root = Path.Combine(Path.GetTempPath(), "wondarr-api-tests", "webroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "index.html"),
            """<!doctype html><html><head><base href="__URL_BASE__/"></head><body></body></html>""");
        _webRoots.Add(root);

        var factory = new WondarrAppFactory(TestConfiguration.Of([.. settings, ("webroot", root)]));
        _factories.Add(factory);

        return factory;
    }
}
