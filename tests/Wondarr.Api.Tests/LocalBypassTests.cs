using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The <c>disabledForLocalAddresses</c> bypass and first-login setup against a real host (R-008,
/// R-009): a private address is not enough, the request must also carry no proxy header and a
/// LAN-style <c>Host</c>.
/// </summary>
public sealed class LocalBypassTests : IDisposable
{
    private const string LocalAddress = "192.168.1.20";
    private const string Initialize = "/initialize.json";

    private readonly List<WondarrAppFactory> _factories = [];

    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("Forwarded")]
    [InlineData("X-Real-IP")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-Forwarded-Proto")]
    [InlineData("Via")]
    [InlineData("X-Original-For")]
    public async Task Initialize_json_is_refused_when_a_private_address_request_carries_a_proxy_header(string header)
    {
        using var factory = Create();
        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.TryAddWithoutValidation(header, "8.8.8.8");

        using var response = await client.GetAsync(new Uri(Initialize, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Initialize_json_is_refused_to_a_rebinding_host_from_a_private_address()
    {
        using var factory = Create();
        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.Host = "evil.example:1077";

        using var response = await client.GetAsync(new Uri(Initialize, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Initialize_json_is_refused_to_a_public_ip_literal_host_from_a_private_address()
    {
        using var factory = Create();
        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.Host = "8.8.8.8:1077";

        using var response = await client.GetAsync(new Uri(Initialize, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("localhost:1077")]
    [InlineData("wondarr.localhost")]
    [InlineData("127.0.0.1:1077")]
    [InlineData("192.168.1.10:1077")]
    [InlineData("[::1]:1077")]
    [InlineData("mediaserver:1077")]
    [InlineData("nas.local")]
    [InlineData("nas.lan")]
    [InlineData("nas.home.arpa")]
    [InlineData("box.internal")]
    public async Task Initialize_json_is_served_to_a_local_caller_with_each_lan_style_host(string host)
    {
        using var factory = Create();
        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.Host = host;

        using var response = await client.GetAsync(new Uri(Initialize, UriKind.Relative));
        var payload = await response.Content.ReadFromJsonAsync<InitializeJson>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        payload!.ApiKey.Should().Be(factory.ApiKey);
    }

    [Theory]
    [InlineData("nas.example.com")]
    [InlineData("media.home.example.org")]
    public async Task Initialize_json_is_served_for_a_configured_allowed_host(string host)
    {
        using var factory = Create(
            ("Server:AllowedHosts:0", "nas.example.com"),
            ("Server:AllowedHosts:1", "*.home.example.org"));
        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.Host = host;

        using var response = await client.GetAsync(new Uri(Initialize, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_host_that_is_not_in_the_allowed_list_still_needs_a_login()
    {
        using var factory = Create(("Server:AllowedHosts:0", "nas.example.com"));
        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.Host = "other.example.com";

        using var response = await client.GetAsync(new Uri(Initialize, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_foreign_host_reaches_the_login_page_instead_of_being_refused()
    {
        using var factory = Create();
        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.Host = "evil.example";

        using var response = await client.GetAsync(new Uri("/login", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Setup_refuses_a_rebinding_host_from_a_private_address()
    {
        using var factory = Create();
        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.Host = "evil.example";

        using var response = await client.PostAsync(
            new Uri("/login/setup", UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "admin",
                ["password"] = "correct horse battery",
                ["passwordAgain"] = "correct horse battery",
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var api = factory.CreateClient();
        api.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);
        var user = await api.GetFromJsonAsync<AuthUserJson>(new Uri("/api/v1/auth/user", UriKind.Relative));
        user!.Configured.Should().BeFalse();
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    private WondarrAppFactory Create(params (string Key, string Value)[] settings)
    {
        var factory = new WondarrAppFactory(TestConfiguration.Of(settings));
        _factories.Add(factory);

        return factory;
    }

    private sealed record InitializeJson(string ApiRoot, string ApiKey, string UrlBase, string InstanceName, string Version);

    private sealed record AuthUserJson(bool Configured, string? Username);
}
