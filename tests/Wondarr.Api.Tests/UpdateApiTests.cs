using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.Updates;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// Not parallelised: the environment-lock test sets a real <c>APP__UPDATE__CHECK_ENABLED</c> variable
/// for the duration of its run, and a concurrently starting host would read it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UpdateApiTestsSeries
{
    /// <summary>The collection's name, shared by the test class.</summary>
    public const string Name = "Update API";
}

/// <summary>The update endpoints, with GitHub stubbed at the HTTP layer so no test reaches the network.</summary>
[Collection(UpdateApiTestsSeries.Name)]
public sealed class UpdateApiTests
{
    private const string Endpoint = "/api/v1/update";

    private const string Releases = """
        [
          {"tag_name":"v0.2.0","name":"Wondarr 0.2.0","html_url":"https://github.com/wulfftech/wondarr/releases/tag/v0.2.0","body":"## Added\n- a thing","draft":false,"prerelease":false,"published_at":"2026-10-09T10:00:00Z"},
          {"tag_name":"v0.1.0","name":"Wondarr 0.1.0","html_url":"https://github.com/wulfftech/wondarr/releases/tag/v0.1.0","body":"first","draft":false,"prerelease":false,"published_at":"2026-10-01T10:00:00Z"}
        ]
        """;

    [Fact]
    public async Task The_update_endpoints_need_the_api_key()
    {
        using var factory = Factory(new Github());
        using var client = factory.CreateClient();

        using var status = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));
        status.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var check = await client.PostAsync(new Uri($"{Endpoint}/check", UriKind.Relative), content: null);
        check.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var settings = await client.GetAsync(new Uri($"{Endpoint}/settings", UriKind.Relative));
        settings.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var put = await client.PutAsync(
            new Uri($"{Endpoint}/settings", UriKind.Relative),
            new StringContent("""{"checkEnabled":false}""", Encoding.UTF8, "application/json"));
        put.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Before_any_check_the_status_has_the_running_version_and_asks_nothing()
    {
        var github = new Github();
        using var factory = Factory(github, version: "0.1.0+abc");
        using var api = new Session(factory);

        var (status, body) = await api.SendAsync(HttpMethod.Get, Endpoint);

        status.Should().Be(HttpStatusCode.OK);

        var resource = (JsonObject)body!;

        resource["currentVersion"]!.GetValue<string>().Should().Be("0.1.0");
        resource["isDevelopmentBuild"]!.GetValue<bool>().Should().BeFalse();
        resource["updateAvailable"]!.GetValue<bool>().Should().BeFalse();
        resource["checkEnabled"]!.GetValue<bool>().Should().BeTrue();
        resource["latestVersion"].Should().BeNull();
        resource["lastError"].Should().BeNull();
        github.Requests.Should().Be(0);
    }

    [Fact]
    public async Task Check_asks_github_and_reports_the_newer_release()
    {
        var github = new Github();
        using var factory = Factory(github, version: "0.1.0");
        using var api = new Session(factory);

        var (status, body) = await api.SendAsync(HttpMethod.Post, $"{Endpoint}/check");

        status.Should().Be(HttpStatusCode.OK);

        var resource = (JsonObject)body!;

        resource["latestVersion"]!.GetValue<string>().Should().Be("0.2.0");
        resource["updateAvailable"]!.GetValue<bool>().Should().BeTrue();
        resource["releaseName"]!.GetValue<string>().Should().Be("Wondarr 0.2.0");
        resource["releaseUrl"]!.GetValue<string>().Should().Be("https://github.com/wulfftech/wondarr/releases/tag/v0.2.0");
        resource["releaseNotes"]!.GetValue<string>().Should().Contain("a thing");
        resource["publishedAt"]!.GetValue<DateTimeOffset>().Should().Be(new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero));
        resource["checkedAt"].Should().NotBeNull();

        // The GET now serves the same answer without another request.
        var (_, again) = await api.SendAsync(HttpMethod.Get, Endpoint);

        ((JsonObject)again!)["latestVersion"]!.GetValue<string>().Should().Be("0.2.0");
        github.Requests.Should().Be(1);
    }

    [Fact]
    public async Task A_second_manual_check_within_a_minute_does_not_ask_again()
    {
        var github = new Github();
        using var factory = Factory(github, version: "0.1.0");
        using var api = new Session(factory);

        await api.SendAsync(HttpMethod.Post, $"{Endpoint}/check");
        await api.SendAsync(HttpMethod.Post, $"{Endpoint}/check");

        github.Requests.Should().Be(1);
    }

    [Fact]
    public async Task A_github_failure_is_a_sentence_in_last_error_and_still_a_200()
    {
        var github = new Github { Status = HttpStatusCode.BadGateway };
        using var factory = Factory(github, version: "0.1.0");
        using var api = new Session(factory);

        var (status, body) = await api.SendAsync(HttpMethod.Post, $"{Endpoint}/check");

        status.Should().Be(HttpStatusCode.OK);
        ((JsonObject)body!)["lastError"]!.GetValue<string>().Should().Contain("502");
    }

    [Fact]
    public async Task A_development_build_reports_the_latest_release_but_no_update()
    {
        using var factory = Factory(new Github(), version: "0.0.0-develop.12+abc");
        using var api = new Session(factory);

        var (_, body) = await api.SendAsync(HttpMethod.Post, $"{Endpoint}/check");

        var resource = (JsonObject)body!;

        resource["isDevelopmentBuild"]!.GetValue<bool>().Should().BeTrue();
        resource["latestVersion"]!.GetValue<string>().Should().Be("0.2.0");
        resource["updateAvailable"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task The_setting_is_written_to_the_file_applied_live_and_stops_the_requests()
    {
        var github = new Github();
        using var factory = Factory(github, version: "0.1.0");
        using var api = new Session(factory);

        var (_, before) = await api.SendAsync(HttpMethod.Get, $"{Endpoint}/settings");

        ((JsonObject)before!)["checkEnabled"]!.GetValue<bool>().Should().BeTrue();
        ((JsonObject)before)["checkEnabledLocked"]!.GetValue<bool>().Should().BeFalse();

        var (status, put) = await api.SendAsync(HttpMethod.Put, $"{Endpoint}/settings", """{"checkEnabled":false}""");

        status.Should().Be(HttpStatusCode.OK);
        ((JsonObject)put!)["checkEnabled"]!.GetValue<bool>().Should().BeFalse();

        var yaml = await File.ReadAllTextAsync(Path.Combine(factory.ConfigDir, "config.yml"));

        yaml.Should().Contain("update:").And.Contain("check_enabled: false");

        var (_, check) = await api.SendAsync(HttpMethod.Post, $"{Endpoint}/check");

        ((JsonObject)check!)["checkEnabled"]!.GetValue<bool>().Should().BeFalse();
        ((JsonObject)check)["updateAvailable"]!.GetValue<bool>().Should().BeFalse();
        github.Requests.Should().Be(0);
    }

    [Fact]
    public async Task A_setting_the_environment_owns_is_locked_and_a_change_is_refused_with_the_variable_named()
    {
        Environment.SetEnvironmentVariable("APP__UPDATE__CHECK_ENABLED", "false");

        try
        {
            using var factory = Factory(new Github(), version: "0.1.0");
            using var api = new Session(factory);

            var (_, read) = await api.SendAsync(HttpMethod.Get, $"{Endpoint}/settings");

            ((JsonObject)read!)["checkEnabled"]!.GetValue<bool>().Should().BeFalse();
            ((JsonObject)read)["checkEnabledLocked"]!.GetValue<bool>().Should().BeTrue();

            var (status, body) = await api.SendAsync(HttpMethod.Put, $"{Endpoint}/settings", """{"checkEnabled":true}""");

            status.Should().Be(HttpStatusCode.BadRequest);
            ((JsonObject)body!)["errors"]!["settings"]!.AsArray()
                .Should().Contain(message => message!.GetValue<string>().Contains("APP__UPDATE__CHECK_ENABLED"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP__UPDATE__CHECK_ENABLED", null);
        }
    }

    private static WondarrAppFactory Factory(Github github, string version = "0.1.0") =>
        new(configureServices: services =>
        {
            services.AddHttpClient(UpdateCheckService.ClientName).ConfigurePrimaryHttpMessageHandler(() => github);
            services.AddSingleton(RunningVersion.Parse(version));
        });

    /// <summary>A fake GitHub: answers the releases list (or a status) and counts the requests.</summary>
    private sealed class Github : HttpMessageHandler
    {
        private int _requests;

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);

            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Status == HttpStatusCode.OK ? Releases : "{}", Encoding.UTF8, "application/json"),
            });
        }
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
