using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// With <c>auth: none</c> the web UI refuses a request addressed to a host name that is not local
/// (DNS rebinding, api-02), unless <c>server.require_local_host</c> is off or the name is allowed.
/// API-key callers are not affected, and neither are <c>forms</c> and <c>external</c>.
/// </summary>
public sealed class NoAuthHostCheckTests : IDisposable
{
    private const string LocalAddress = "192.168.1.20";
    private const string RemoteAddress = "8.8.8.8";
    private const string Initialize = "/initialize.json";
    private const string Endpoint = "/api/v1/config/security";

    private readonly List<WondarrAppFactory> _factories = [];

    [Fact]
    public async Task Initialize_json_is_refused_to_a_rebinding_host_from_a_private_address_when_authentication_is_none()
    {
        using var factory = Create(("Server:Auth", "None"));
        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.Host = "evil.example:1077";

        using var response = await client.GetAsync(new Uri(Initialize, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        body.Should().NotContain(factory.ApiKey);
        body.Should().Contain("evil.example").And.Contain("server.allowed_hosts");
    }

    [Fact]
    public async Task Initialize_json_is_refused_to_a_public_host_name_from_a_public_address_when_authentication_is_none()
    {
        using var factory = Create(("Server:Auth", "None"));
        using var client = factory.CreateClient(RemoteAddress);
        client.DefaultRequestHeaders.Host = "wondarr.example.com";

        using var response = await client.GetAsync(new Uri(Initialize, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(factory.ApiKey);
    }

    [Fact]
    public async Task Initialize_json_is_served_to_a_rebinding_host_when_require_local_host_is_off()
    {
        using var factory = Create(("Server:Auth", "None"), ("Server:RequireLocalHost", "false"));
        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.Host = "evil.example:1077";

        using var response = await client.GetAsync(new Uri(Initialize, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Initialize_json_is_served_to_a_host_in_the_allowed_list_when_authentication_is_none()
    {
        using var factory = Create(("Server:Auth", "None"), ("Server:AllowedHosts:0", "evil.example"));
        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.Host = "evil.example:1077";

        using var response = await client.GetAsync(new Uri(Initialize, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("localhost:1077")]
    [InlineData("192.168.1.10:1077")]
    [InlineData("nas.lan")]
    [InlineData("mediaserver")]
    public async Task Initialize_json_is_served_with_each_local_host_when_authentication_is_none(string host)
    {
        using var factory = Create(("Server:Auth", "None"));
        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.Host = host;

        using var response = await client.GetAsync(new Uri(Initialize, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_browser_navigation_to_a_non_local_host_gets_a_page_that_names_the_way_out()
    {
        using var factory = Create(("Server:Auth", "None"));
        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.Host = "evil.example";
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));
        var page = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        page.Should().Contain("<strong>evil.example</strong>");
        page.Should().Contain("server.allowed_hosts");
        page.Should().Contain("Settings");
        page.Should().NotContain(factory.ApiKey);
    }

    [Fact]
    public async Task An_api_style_request_to_a_non_local_host_gets_a_problem_details_body()
    {
        using var factory = Create(("Server:Auth", "None"));
        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.Host = "evil.example";

        using var response = await client.GetAsync(new Uri("/some/ui/route", UriKind.Relative));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        body["detail"]!.GetValue<string>().Should().Contain("evil.example").And.Contain("only answers local names");
    }

    [Fact]
    public async Task An_api_key_request_works_from_any_host_when_authentication_is_none()
    {
        using var factory = Create(("Server:Auth", "None"));
        using var client = factory.CreateClient(RemoteAddress);
        client.DefaultRequestHeaders.Host = "wondarr.example.com";
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.GetAsync(new Uri("/api/v1/system/status", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_api_still_needs_the_key_when_authentication_is_none()
    {
        using var factory = Create(("Server:Auth", "None"));
        using var client = factory.CreateClient(LocalAddress);

        using var response = await client.GetAsync(new Uri("/api/v1/system/status", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Initialize_json_is_served_to_a_public_host_when_authentication_is_external()
    {
        using var factory = Create(("Server:Auth", "External"));
        using var client = factory.CreateClient(RemoteAddress);
        client.DefaultRequestHeaders.Host = "wondarr.example.com";

        using var response = await client.GetAsync(new Uri(Initialize, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_setting_is_on_by_default_and_not_locked()
    {
        using var factory = Create();
        using var api = new Session(factory);

        var (status, body) = await api.SendAsync(HttpMethod.Get, Endpoint);

        status.Should().Be(HttpStatusCode.OK);
        ((JsonObject)body!)["requireLocalHost"]!.GetValue<bool>().Should().BeTrue();
        ((JsonObject)body)["requireLocalHostLocked"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task The_setting_needs_the_api_key()
    {
        using var factory = Create();
        using var client = factory.CreateClient();

        using var read = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));
        read.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var write = await client.PutAsync(
            new Uri(Endpoint, UriKind.Relative),
            new StringContent("""{"requireLocalHost":false}""", Encoding.UTF8, "application/json"));
        write.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_setting_is_written_to_config_yml_leaves_the_rest_of_the_server_section_and_applies_to_the_next_request()
    {
        using var factory = Create(("Server:Auth", "None"));
        using var api = new Session(factory);
        using var browser = factory.CreateClient(LocalAddress);
        browser.DefaultRequestHeaders.Host = "evil.example";

        using var before = await browser.GetAsync(new Uri(Initialize, UriKind.Relative));
        before.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var configPath = Path.Combine(factory.ConfigDir, "config.yml");
        var yamlBefore = await File.ReadAllTextAsync(configPath);

        var (status, put) = await api.SendAsync(HttpMethod.Put, Endpoint, """{"requireLocalHost":false}""");

        status.Should().Be(HttpStatusCode.OK);
        ((JsonObject)put!)["requireLocalHost"]!.GetValue<bool>().Should().BeFalse();

        var yaml = await File.ReadAllTextAsync(configPath);

        yamlBefore.Should().NotContain("require_local_host");
        yaml.Should().Contain("require_local_host: false");
        yaml.Should().Contain(factory.ApiKey).And.Contain("auth:");

        using var after = await browser.GetAsync(new Uri(Initialize, UriKind.Relative));
        after.StatusCode.Should().Be(HttpStatusCode.OK);

        var (_, back) = await api.SendAsync(HttpMethod.Put, Endpoint, """{"requireLocalHost":true}""");

        ((JsonObject)back!)["requireLocalHost"]!.GetValue<bool>().Should().BeTrue();

        using var again = await browser.GetAsync(new Uri(Initialize, UriKind.Relative));
        again.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_change_that_names_no_field_leaves_the_file_alone()
    {
        using var factory = Create();
        using var api = new Session(factory);

        var (status, body) = await api.SendAsync(HttpMethod.Put, Endpoint, "{}");

        status.Should().Be(HttpStatusCode.OK);
        ((JsonObject)body!)["requireLocalHost"]!.GetValue<bool>().Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(factory.ConfigDir, "config.yml"))).Should().NotContain("require_local_host");
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

    private sealed class Session : IDisposable
    {
        private readonly HttpClient _client;

        public Session(WondarrAppFactory factory)
        {
            _client = factory.CreateClient();
            _client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);
        }

        public void Dispose() => _client.Dispose();

        public async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, string? json = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var response = await _client.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();

            return (response.StatusCode, raw.Length == 0 ? null : JsonNode.Parse(raw));
        }
    }
}
