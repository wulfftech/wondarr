using System.Net;
using System.Net.Http.Json;
using Wondarr.Api.Authentication;
using Wondarr.Api.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The login cookie and the login form: sessions that end when the credentials change (R-036), the
/// throttle (R-037) and the Secure flag (R-058).
/// </summary>
public sealed class LoginSessionTests : IDisposable
{
    private const string RemoteAddress = "8.8.8.8";
    private const string Username = "admin";
    private const string Password = "correct horse battery";

    private readonly List<WondarrAppFactory> _factories = [];

    [Fact]
    public async Task An_old_cookie_is_refused_after_the_password_changes()
    {
        using var factory = Create();
        await SetCredentialsAsync(factory, Username, Password);
        var cookie = await LoginAndGetCookieAsync(factory);
        (await InitializeStatusAsync(factory, cookie)).Should().Be(HttpStatusCode.OK);

        await SetCredentialsAsync(factory, Username, "a different password");

        (await InitializeStatusAsync(factory, cookie)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_old_cookie_is_refused_after_the_username_changes()
    {
        using var factory = Create();
        await SetCredentialsAsync(factory, Username, Password);
        var cookie = await LoginAndGetCookieAsync(factory);

        await SetCredentialsAsync(factory, "someone-else", Password);

        (await InitializeStatusAsync(factory, cookie)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_session_that_makes_the_change_stays_signed_in_and_others_do_not()
    {
        using var factory = Create();
        await SetCredentialsAsync(factory, Username, Password);
        var otherCookie = await LoginAndGetCookieAsync(factory);

        // The browser that changes the password holds the cookie and sends the key, as the SPA does.
        using var browser = factory.CreateClient(RemoteAddress);
        using (var login = await browser.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, Password)))
        {
            login.StatusCode.Should().Be(HttpStatusCode.Found);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri("/api/v1/auth/user", UriKind.Relative))
        {
            Content = JsonContent.Create(new { username = Username, password = "a different password" }),
        };
        request.Headers.Add("X-Api-Key", factory.ApiKey);

        using (var change = await browser.SendAsync(request))
        {
            change.StatusCode.Should().Be(HttpStatusCode.NoContent);
            change.Headers.Contains("Set-Cookie").Should().BeTrue();
        }

        using (var stillIn = await browser.GetAsync(new Uri("/initialize.json", UriKind.Relative)))
        {
            stillIn.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await InitializeStatusAsync(factory, otherCookie)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_eleventh_login_attempt_in_a_minute_is_refused_with_retry_after()
    {
        using var factory = Create();
        await SetCredentialsAsync(factory, Username, Password);
        using var client = factory.CreateClient(RemoteAddress);

        for (var attempt = 1; attempt <= LoginRateLimiting.AttemptsPerMinute; attempt++)
        {
            using var accepted = await client.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, "wrong password"));
            accepted.StatusCode.Should().Be(HttpStatusCode.Found, $"attempt {attempt} is within the limit");
        }

        using var refused = await client.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, Password));

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.Contains("Retry-After").Should().BeTrue();
        (await refused.Content.ReadAsStringAsync()).Should().Contain("Too many sign-in attempts. Wait a minute and try again.");

        // Another address has its own window.
        using var other = factory.CreateClient("8.8.4.4");
        using var fine = await other.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, Password));
        fine.StatusCode.Should().Be(HttpStatusCode.Found);
        fine.Headers.Contains("Set-Cookie").Should().BeTrue();
    }

    [Fact]
    public async Task Setup_attempts_are_throttled_too()
    {
        using var factory = Create();
        using var client = factory.CreateClient("192.168.1.20");

        for (var attempt = 0; attempt < LoginRateLimiting.AttemptsPerMinute; attempt++)
        {
            using var accepted = await client.PostAsync(
                new Uri("/login/setup", UriKind.Relative),
                new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "x", ["password"] = "short" }));
            accepted.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        using var refused = await client.PostAsync(
            new Uri("/login/setup", UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "x", ["password"] = "short" }));

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Too_many_logins_at_once_from_different_addresses_are_refused_rather_than_queued()
    {
        using var factory = Create();
        await SetCredentialsAsync(factory, Username, Password);

        var attempts = Enumerable.Range(1, 40).Select(async index =>
        {
            using var client = factory.CreateClient($"8.8.{index}.1");
            using var response = await client.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, "wrong password"));

            return response.StatusCode;
        });

        var results = await Task.WhenAll(attempts);

        results.Should().Contain(HttpStatusCode.TooManyRequests, "no more than two logins hash at once");
        results.Should().Contain(HttpStatusCode.Found);
    }

    [Fact]
    public async Task The_cookie_is_secure_over_https()
    {
        using var factory = Create();
        await SetCredentialsAsync(factory, Username, Password);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false },
            RemoteAddress);

        using var response = await client.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, Password));

        SetCookieOf(response).Should().Contain("secure");
    }

    [Fact]
    public async Task The_cookie_is_secure_when_a_proxy_says_the_visitor_used_https()
    {
        using var factory = Create();
        await SetCredentialsAsync(factory, Username, Password);
        using var client = factory.CreateClient(RemoteAddress);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https, http");

        using var response = await client.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, Password));

        SetCookieOf(response).Should().Contain("secure");
    }

    [Fact]
    public async Task The_cookie_is_not_secure_over_plain_http()
    {
        using var factory = Create();
        await SetCredentialsAsync(factory, Username, Password);
        using var client = factory.CreateClient(RemoteAddress);

        using var response = await client.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, Password));

        SetCookieOf(response).Should().NotContain("secure");
    }

    [Fact]
    public async Task A_proxy_that_says_http_does_not_make_the_cookie_secure()
    {
        using var factory = Create();
        await SetCredentialsAsync(factory, Username, Password);
        using var client = factory.CreateClient(RemoteAddress);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http, https");

        using var response = await client.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, Password));

        SetCookieOf(response).Should().NotContain("secure");
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    private static string SetCookieOf(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();

        return cookies!.Single(cookie => cookie.StartsWith($"{AuthenticationBuilderExtensions.CookieName}=", StringComparison.Ordinal))
            .ToLowerInvariant();
    }

    private static FormUrlEncodedContent LoginForm(string username, string password) =>
        new(new Dictionary<string, string> { ["username"] = username, ["password"] = password });

    private WondarrAppFactory Create()
    {
        var factory = new WondarrAppFactory();
        _factories.Add(factory);

        return factory;
    }

    private static async Task SetCredentialsAsync(WondarrAppFactory factory, string username, string password)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.PutAsJsonAsync(new Uri("/api/v1/auth/user", UriKind.Relative), new { username, password });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>Signs in on its own client and returns the cookie, so it can be replayed later.</summary>
    private static async Task<string> LoginAndGetCookieAsync(WondarrAppFactory factory)
    {
        using var client = factory.CreateClient(RemoteAddress);
        using var login = await client.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, Password));

        login.StatusCode.Should().Be(HttpStatusCode.Found);

        return login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    private static async Task<HttpStatusCode> InitializeStatusAsync(WondarrAppFactory factory, string cookie)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false }, RemoteAddress);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/initialize.json", UriKind.Relative));
        request.Headers.Add("Cookie", cookie);

        using var response = await client.SendAsync(request);

        return response.StatusCode;
    }
}
