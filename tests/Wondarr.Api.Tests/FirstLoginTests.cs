using System.Net;
using System.Net.Http.Json;
using Wondarr.Api.Authentication;
using FluentAssertions;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>The first user creates their login on the login page (<c>GET /login</c>, <c>POST /login/setup</c>).</summary>
public sealed class FirstLoginTests : IDisposable
{
    private const string LocalAddress = "192.168.1.20";
    private const string RemoteAddress = "8.8.8.8";
    private const string Username = "admin";
    private const string Password = "correct horse battery";

    private readonly List<WondarrAppFactory> _factories = [];

    [Fact]
    public async Task The_login_page_offers_the_create_form_to_a_local_address_when_no_login_exists()
    {
        using var factory = Create();

        using var browser = factory.CreateClient(LocalAddress);
        var page = await browser.GetStringAsync(new Uri("/login", UriKind.Relative));

        page.Should().Contain("<h1>Create your login</h1>");
        page.Should().Contain("No login exists yet. Choose the username and password you will sign in with.");
        page.Should().Contain("action=\"/login/setup\"");
        page.Should().Contain("name=\"passwordAgain\"");
    }

    [Fact]
    public async Task The_login_page_explains_where_to_create_the_login_to_a_public_address_and_offers_no_form()
    {
        using var factory = Create();

        using var browser = factory.CreateClient(RemoteAddress);
        var page = await browser.GetStringAsync(new Uri("/login", UriKind.Relative));

        page.Should().Contain("No login exists yet. Create it from a device on the same network as Wondarr, or under Settings → General.");
        page.Should().NotContain("<form");
    }

    [Fact]
    public async Task The_login_page_does_not_offer_the_create_form_behind_a_proxy()
    {
        using var factory = Create();

        using var browser = factory.CreateClient(LocalAddress);
        browser.DefaultRequestHeaders.Add("X-Forwarded-For", RemoteAddress);
        var page = await browser.GetStringAsync(new Uri("/login", UriKind.Relative));

        page.Should().NotContain("<form");
    }

    [Fact]
    public async Task The_login_page_is_the_normal_sign_in_form_once_a_login_exists()
    {
        using var factory = Create();
        await ConfigureCredentialsAsync(factory);

        using var browser = factory.CreateClient(LocalAddress);
        var page = await browser.GetStringAsync(new Uri("/login", UriKind.Relative));

        page.Should().Contain("action=\"/login\"");
        page.Should().Contain("Log in");
        page.Should().NotContain("Create your login");
        page.Should().NotContain("passwordAgain");
    }

    [Fact]
    public async Task Setup_from_a_local_address_stores_the_login_signs_in_and_goes_to_a_local_return_url()
    {
        using var factory = Create();

        using var browser = factory.CreateClient(LocalAddress);
        using var response = await browser.PostAsync(
            new Uri("/login/setup?returnUrl=%2Fsettings", UriKind.Relative),
            SetupForm(Username, Password, Password));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Be("/settings");
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies!.Should().Contain(cookie => cookie.StartsWith($"{AuthenticationBuilderExtensions.CookieName}=", StringComparison.Ordinal));

        (await StoredUsernameAsync(factory)).Should().Be(Username);

        // The new login works on the sign-in form.
        using var other = factory.CreateClient(RemoteAddress);
        using var login = await other.PostAsync(new Uri("/login", UriKind.Relative), SetupForm(Username, Password, null));
        login.Headers.Location!.ToString().Should().Be("/");
        login.Headers.Contains("Set-Cookie").Should().BeTrue();
    }

    [Fact]
    public async Task Setup_sends_a_non_local_return_url_to_the_root()
    {
        using var factory = Create();

        using var browser = factory.CreateClient(LocalAddress);
        using var response = await browser.PostAsync(
            new Uri("/login/setup?returnUrl=https%3A%2F%2Fevil.example%2F", UriKind.Relative),
            SetupForm(Username, Password, Password));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Be("/");
        response.Headers.Contains("Set-Cookie").Should().BeTrue();
    }

    [Fact]
    public async Task Setup_from_a_public_address_is_forbidden_and_stores_nothing()
    {
        using var factory = Create();

        using var browser = factory.CreateClient(RemoteAddress);
        using var response = await browser.PostAsync(new Uri("/login/setup", UriKind.Relative), SetupForm(Username, Password, Password));
        var page = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        page.Should().Contain("Create it from a device on the same network as Wondarr");
        (await StoredUsernameAsync(factory)).Should().BeNull();
    }

    [Fact]
    public async Task Setup_with_a_forwarded_for_header_is_forbidden_and_stores_nothing()
    {
        using var factory = Create();

        using var browser = factory.CreateClient(LocalAddress);
        browser.DefaultRequestHeaders.Add("X-Forwarded-For", RemoteAddress);
        using var response = await browser.PostAsync(new Uri("/login/setup", UriKind.Relative), SetupForm(Username, Password, Password));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredUsernameAsync(factory)).Should().BeNull();
    }

    [Theory]
    [InlineData("X-Real-IP", "8.8.8.8")]
    [InlineData("Forwarded", "for=8.8.8.8")]
    [InlineData("X-Forwarded-Host", "wondarr.example.com")]
    [InlineData("X-Forwarded-Proto", "https")]
    [InlineData("Via", "1.1 proxy")]
    [InlineData("X-Original-For", "8.8.8.8")]
    public async Task Setup_with_any_proxy_header_is_forbidden_and_stores_nothing(string header, string value)
    {
        using var factory = Create();

        using var browser = factory.CreateClient(LocalAddress);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/login/setup", UriKind.Relative))
        {
            Content = SetupForm(Username, Password, Password),
        };
        request.Headers.TryAddWithoutValidation(header, value);
        using var response = await browser.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredUsernameAsync(factory)).Should().BeNull();
    }

    [Fact]
    public async Task The_login_page_does_not_offer_the_create_form_with_an_x_real_ip_header()
    {
        using var factory = Create();

        using var browser = factory.CreateClient(LocalAddress);
        browser.DefaultRequestHeaders.Add("X-Real-IP", RemoteAddress);
        var page = await browser.GetStringAsync(new Uri("/login", UriKind.Relative));

        page.Should().NotContain("<form");
    }

    [Fact]
    public async Task Setup_from_another_site_is_forbidden_and_stores_nothing()
    {
        using var factory = Create();

        using var browser = factory.CreateClient(LocalAddress);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/login/setup", UriKind.Relative))
        {
            Content = SetupForm(Username, Password, Password),
        };
        request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        using var response = await browser.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        (await StoredUsernameAsync(factory)).Should().BeNull();
    }

    [Fact]
    public async Task Setup_with_the_same_origin_succeeds()
    {
        using var factory = Create();

        using var browser = factory.CreateClient(LocalAddress);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/login/setup", UriKind.Relative))
        {
            Content = SetupForm(Username, Password, Password),
        };
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost");
        using var response = await browser.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        (await StoredUsernameAsync(factory)).Should().Be(Username);
    }

    [Fact]
    public async Task Setup_from_a_public_address_on_a_configured_instance_redirects_to_the_login_page()
    {
        using var factory = Create();
        await ConfigureCredentialsAsync(factory);

        using var browser = factory.CreateClient(RemoteAddress);
        using var response = await browser.PostAsync(
            new Uri("/login/setup", UriKind.Relative),
            SetupForm("intruder", "another password", "another password"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().StartWith("/login");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        (await StoredUsernameAsync(factory)).Should().Be(Username);
    }

    [Fact]
    public async Task Setup_html_encodes_the_username_when_it_shows_the_form_again()
    {
        using var factory = Create();
        const string username = "\"><script>alert(1)</script>";

        using var browser = factory.CreateClient(LocalAddress);
        using var response = await browser.PostAsync(new Uri("/login/setup", UriKind.Relative), SetupForm(username, "tiny", "tiny"));
        var page = await response.Content.ReadAsStringAsync();

        page.Should().NotContain("<script>alert(1)");
        page.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
    }

    [Theory]
    [InlineData("admin", "correct horse battery", "something else entirely", "The two passwords do not match.")]
    [InlineData("admin", "tiny", "tiny", "The password must be at least 8 characters.")]
    [InlineData("", "correct horse battery", "correct horse battery", "The username must be between 1 and 64 characters.")]
    public async Task Setup_with_invalid_input_shows_the_form_again_with_the_message_and_stores_nothing(
        string username, string password, string passwordAgain, string message)
    {
        using var factory = Create();

        using var browser = factory.CreateClient(LocalAddress);
        using var response = await browser.PostAsync(new Uri("/login/setup", UriKind.Relative), SetupForm(username, password, passwordAgain));
        var page = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        page.Should().Contain(message);
        page.Should().Contain("<h1>Create your login</h1>");
        page.Should().NotContain(password);
        (await StoredUsernameAsync(factory)).Should().BeNull();
    }

    [Fact]
    public async Task Setup_with_a_too_long_username_shows_the_form_again_and_keeps_the_username_filled_in()
    {
        using var factory = Create();
        var username = new string('u', 65);

        using var browser = factory.CreateClient(LocalAddress);
        using var response = await browser.PostAsync(new Uri("/login/setup", UriKind.Relative), SetupForm(username, Password, Password));
        var page = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        page.Should().Contain("The username must be between 1 and 64 characters.");
        page.Should().Contain($"value=\"{username}\"");
        page.Should().NotContain(Password);
        (await StoredUsernameAsync(factory)).Should().BeNull();
    }

    [Fact]
    public async Task Setup_when_a_login_already_exists_redirects_to_the_login_page_and_leaves_it_unchanged()
    {
        using var factory = Create();
        await ConfigureCredentialsAsync(factory);

        using var browser = factory.CreateClient(LocalAddress);
        using var response = await browser.PostAsync(
            new Uri("/login/setup", UriKind.Relative),
            SetupForm("intruder", "another password", "another password"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().StartWith("/login");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        (await StoredUsernameAsync(factory)).Should().Be(Username);

        using var other = factory.CreateClient(RemoteAddress);
        using var original = await other.PostAsync(new Uri("/login", UriKind.Relative), SetupForm(Username, Password, null));
        original.Headers.Contains("Set-Cookie").Should().BeTrue();
    }

    [Fact]
    public async Task Setup_is_not_found_when_authentication_is_none()
    {
        using var factory = Create(("Server:Auth", "None"));

        using var browser = factory.CreateClient(LocalAddress);
        using var response = await browser.PostAsync(new Uri("/login/setup", UriKind.Relative), SetupForm(Username, Password, Password));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StoredUsernameAsync(factory)).Should().BeNull();
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    private static FormUrlEncodedContent SetupForm(string username, string password, string? passwordAgain)
    {
        var fields = new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
        };

        if (passwordAgain is not null)
        {
            fields["passwordAgain"] = passwordAgain;
        }

        return new FormUrlEncodedContent(fields);
    }

    private WondarrAppFactory Create(params (string Key, string Value)[] settings)
    {
        var factory = new WondarrAppFactory(TestConfiguration.Of(settings));
        _factories.Add(factory);

        return factory;
    }

    private static async Task ConfigureCredentialsAsync(WondarrAppFactory factory)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.PutAsJsonAsync(
            new Uri("/api/v1/auth/user", UriKind.Relative),
            new { username = Username, password = Password });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static async Task<string?> StoredUsernameAsync(WondarrAppFactory factory)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        var user = await client.GetFromJsonAsync<AuthUserResource>(new Uri("/api/v1/auth/user", UriKind.Relative));

        return user!.Username;
    }
}
