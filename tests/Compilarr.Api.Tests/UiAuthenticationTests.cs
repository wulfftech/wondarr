using System.Net;
using System.Net.Http.Json;
using Compilarr.Api.Authentication;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Compilarr.Api.Tests;

/// <summary>
/// The UI side of authentication: <c>/initialize.json</c>, the login form post, and the
/// "disabled for local addresses" bypass.
/// </summary>
public sealed class UiAuthenticationTests : IDisposable
{
    private const string LocalAddress = "192.168.1.20";
    private const string RemoteAddress = "8.8.8.8";
    private const string Username = "admin";
    private const string Password = "correct horse battery";

    private readonly List<CompilarrAppFactory> _factories = [];

    [Fact]
    public async Task Initialize_json_serves_the_key_to_a_local_address()
    {
        using var factory = Create();

        using var client = factory.CreateClient(LocalAddress);
        using var response = await client.GetAsync(new Uri("/initialize.json", UriKind.Relative));
        var payload = await response.Content.ReadFromJsonAsync<InitializeJson>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        payload.Should().NotBeNull();
        payload!.ApiKey.Should().Be(factory.ApiKey);
        payload.ApiRoot.Should().Be("/api/v1");
        payload.UrlBase.Should().BeEmpty();
        payload.InstanceName.Should().Be("Compilarr");
        payload.Version.Should().NotBeNullOrWhiteSpace();
    }


    [Fact]
    public async Task Initialize_json_rejects_a_remote_address()
    {
        using var factory = Create();

        using var client = factory.CreateClient(RemoteAddress);
        using var response = await client.GetAsync(new Uri("/initialize.json", UriKind.Relative));

        // .NET 10 cookie auth answers 401 instead of redirecting for API endpoints; the SPA's
        // bootstrap turns that into a navigation to the login page.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Initialize_json_does_not_trust_a_forwarded_for_header()
    {
        using var factory = Create();

        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", RemoteAddress);
        using var response = await client.GetAsync(new Uri("/initialize.json", UriKind.Relative));

        // .NET 10 cookie auth answers 401 instead of redirecting for API endpoints; the SPA's
        // bootstrap turns that into a navigation to the login page.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Initialize_json_rejects_a_local_address_when_authentication_is_required()
    {
        using var factory = Create(("Server:AuthRequired", "Enabled"));

        using var client = factory.CreateClient(LocalAddress);
        using var response = await client.GetAsync(new Uri("/initialize.json", UriKind.Relative));

        // .NET 10 cookie auth answers 401 instead of redirecting for API endpoints; the SPA's
        // bootstrap turns that into a navigation to the login page.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Initialize_json_serves_the_key_when_authentication_is_none()
    {
        using var factory = Create(("Server:Auth", "None"));

        using var client = factory.CreateClient(RemoteAddress);
        using var response = await client.GetAsync(new Uri("/initialize.json", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logging_in_with_the_right_password_grants_access_to_initialize_json()
    {
        using var factory = Create();
        await ConfigureCredentialsAsync(factory);

        using var browser = factory.CreateClient(RemoteAddress);
        using var login = await browser.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, Password));

        login.StatusCode.Should().Be(HttpStatusCode.Found);
        login.Headers.Location!.ToString().Should().Be("/");
        login.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies!.Should().Contain(cookie => cookie.StartsWith($"{AuthenticationBuilderExtensions.CookieName}=", StringComparison.Ordinal));

        using var initialize = await browser.GetAsync(new Uri("/initialize.json", UriKind.Relative));
        initialize.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logging_in_with_a_wrong_password_sends_no_cookie()
    {
        using var factory = Create();
        await ConfigureCredentialsAsync(factory);

        using var browser = factory.CreateClient(RemoteAddress);
        using var login = await browser.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, "wrong password"));

        login.StatusCode.Should().Be(HttpStatusCode.Found);
        login.Headers.Location!.ToString().Should().Be("/login?returnUrl=&loginFailed=true");
        login.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task Logging_in_under_a_url_base_redirects_to_the_url_base_root()
    {
        using var factory = Create(("Server:UrlBase", "/compilarr"));
        await ConfigureCredentialsAsync(factory, "/compilarr");

        using var browser = factory.CreateClient(RemoteAddress);
        using var login = await browser.PostAsync(new Uri("/compilarr/login", UriKind.Relative), LoginForm(Username, Password));

        login.StatusCode.Should().Be(HttpStatusCode.Found);
        login.Headers.Location!.ToString().Should().Be("/compilarr/");
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    private static FormUrlEncodedContent LoginForm(string username, string password) =>
        new(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
        });

    private CompilarrAppFactory Create(params (string Key, string Value)[] settings)
    {
        var factory = new CompilarrAppFactory(TestConfiguration.Of(settings));
        _factories.Add(factory);

        return factory;
    }

    private static async Task ConfigureCredentialsAsync(CompilarrAppFactory factory, string urlBase = "")
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.PutAsJsonAsync(
            new Uri($"{urlBase}/api/v1/auth/user", UriKind.Relative),
            new { username = Username, password = Password });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private sealed record InitializeJson(string ApiRoot, string ApiKey, string UrlBase, string InstanceName, string Version);
}
