using System.Net;
using System.Net.Http.Json;
using Wondarr.Api.Authentication;
using Wondarr.Api.Extensions;
using Wondarr.Core.Authentication;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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

    [Theory]
    [InlineData("/login/")]
    [InlineData("/LOGIN")]
    [InlineData("/Login/")]
    public async Task The_throttle_also_covers_a_trailing_slash_and_a_mixed_case_path(string path)
    {
        using var factory = Create();
        await SetCredentialsAsync(factory, Username, Password);
        using var client = factory.CreateClient(RemoteAddress);

        for (var attempt = 1; attempt <= LoginRateLimiting.AttemptsPerMinute; attempt++)
        {
            using var accepted = await client.PostAsync(new Uri(path, UriKind.Relative), LoginForm(Username, "wrong password"));
            accepted.StatusCode.Should().Be(HttpStatusCode.Found, $"attempt {attempt} is within the limit");
        }

        using var refused = await client.PostAsync(new Uri(path, UriKind.Relative), LoginForm(Username, "wrong password"));

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_variants_of_the_path_share_one_window()
    {
        using var factory = Create();
        await SetCredentialsAsync(factory, Username, Password);
        using var client = factory.CreateClient(RemoteAddress);

        for (var attempt = 0; attempt < LoginRateLimiting.AttemptsPerMinute; attempt++)
        {
            using var accepted = await client.PostAsync(
                new Uri(attempt % 2 == 0 ? "/login" : "/login/", UriKind.Relative),
                LoginForm(Username, "wrong password"));
            accepted.StatusCode.Should().Be(HttpStatusCode.Found);
        }

        using var refused = await client.PostAsync(new Uri("/LOGIN", UriKind.Relative), LoginForm(Username, "wrong password"));

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task An_ipv6_client_is_limited_per_slash_64()
    {
        using var factory = Create();
        await SetCredentialsAsync(factory, Username, Password);

        for (var attempt = 0; attempt < LoginRateLimiting.AttemptsPerMinute; attempt++)
        {
            using var client = factory.CreateClient($"2001:db8:1:2::{attempt + 1:x}");
            using var accepted = await client.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, "wrong password"));
            accepted.StatusCode.Should().Be(HttpStatusCode.Found);
        }

        using var sameNetwork = factory.CreateClient("2001:db8:1:2:ffff::1");
        using var refused = await sameNetwork.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, "wrong password"));
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        using var otherNetwork = factory.CreateClient("2001:db8:1:3::1");
        using var fine = await otherNetwork.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, "wrong password"));
        fine.StatusCode.Should().Be(HttpStatusCode.Found);
    }

    [Fact]
    public async Task A_third_login_while_two_are_checking_is_refused_and_the_first_two_finish()
    {
        var gate = new PasswordCheckGate();
        using var factory = new WondarrAppFactory(
            configureServices: services =>
            {
                var original = services.Single(descriptor => descriptor.ServiceType == typeof(ICredentialStore));
                services.Remove(original);
                services.AddScoped<ICredentialStore>(provider =>
                    new GatedCredentialStore(ActivatorUtilities.CreateInstance<CredentialStore>(provider), gate));
            });
        _factories.Add(factory);
        await SetCredentialsAsync(factory, Username, Password);

        using var first = factory.CreateClient("8.8.1.1");
        using var second = factory.CreateClient("8.8.1.2");
        using var third = factory.CreateClient("8.8.1.3");
        var firstLogin = first.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, Password));
        var secondLogin = second.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, Password));

        await gate.TwoEntered.WaitAsync(TimeSpan.FromSeconds(30));

        using var refused = await third.PostAsync(new Uri("/login", UriKind.Relative), LoginForm(Username, Password));

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.Contains("Retry-After").Should().BeTrue();
        (await refused.Content.ReadAsStringAsync()).Should().Contain(LoginRateLimiting.BusyMessage);

        gate.Release();

        using var firstDone = await firstLogin;
        using var secondDone = await secondLogin;
        firstDone.StatusCode.Should().Be(HttpStatusCode.Found);
        secondDone.StatusCode.Should().Be(HttpStatusCode.Found);
        firstDone.Headers.Contains("Set-Cookie").Should().BeTrue();
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

    /// <summary>Holds password checks open until released, and says when two are in progress.</summary>
    private sealed class PasswordCheckGate
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _twoEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;

        public Task TwoEntered => _twoEntered.Task;

        public async Task EnterAsync()
        {
            if (Interlocked.Increment(ref _entered) == LoginRateLimiting.ConcurrentHashes)
            {
                _twoEntered.TrySetResult();
            }

            await _release.Task;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class GatedCredentialStore(ICredentialStore inner, PasswordCheckGate gate) : ICredentialStore
    {
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) => inner.IsConfiguredAsync(cancellationToken);

        public Task<string?> GetUsernameAsync(CancellationToken cancellationToken) => inner.GetUsernameAsync(cancellationToken);

        public Task<string?> GetStampAsync(CancellationToken cancellationToken) => inner.GetStampAsync(cancellationToken);

        public Task SetAsync(string username, string password, CancellationToken cancellationToken) =>
            inner.SetAsync(username, password, cancellationToken);

        public Task<bool> TrySetInitialAsync(string username, string password, CancellationToken cancellationToken) =>
            inner.TrySetInitialAsync(username, password, cancellationToken);

        public async Task<bool> VerifyAsync(string username, string password, CancellationToken cancellationToken)
        {
            await gate.EnterAsync();

            return await inner.VerifyAsync(username, password, cancellationToken);
        }
    }
}
