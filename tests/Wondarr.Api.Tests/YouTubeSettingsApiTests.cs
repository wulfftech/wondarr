using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wondarr.Core.Media;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The YouTube settings endpoints: the section as it is stored, the partial update, the health
/// probe's answer and the Test button's fresh probe. The process runner is an NSubstitute fake, so no
/// test runs yt-dlp, and the settings are written into the factory's own <c>config.yml</c>.
/// </summary>
/// <remarks>
/// Not parallelised: the environment-lock test sets a real <c>APP__YOUTUBE__*</c> variable for the
/// duration of its run, and a concurrently executing test that reads the environment would race it.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class YouTubeSettingsApiTestsSeries
{
    /// <summary>The collection's name, shared by the test class.</summary>
    public const string Name = "YouTube settings API";
}

/// <inheritdoc cref="YouTubeSettingsApiTestsSeries" />
[Collection(YouTubeSettingsApiTestsSeries.Name)]
public sealed class YouTubeSettingsApiTests
{
    private const string Endpoint = "/api/v1/youtube";

    [Fact]
    public async Task The_settings_report_the_section_with_its_defaults_when_nothing_is_set()
    {
        using var factory = Factory();
        using var api = new Session(factory);

        var (status, body) = await api.GetAsync($"{Endpoint}/settings");

        status.Should().Be(HttpStatusCode.OK);

        var settings = (JsonObject)body!;

        settings["enabled"]!.GetValue<bool>().Should().BeFalse("the source is off by default (DECISIONS.md #6)");
        settings["cookiesPath"].Should().BeNull();
        settings["poTokenBaseUrl"].Should().BeNull();
        settings["allowVideos"]!.GetValue<bool>().Should().BeFalse();
        settings["searchLimit"]!.GetValue<int>().Should().Be(20);
        settings["readOnlyFields"]!.AsArray().Should().BeEmpty();

        var policy = (JsonObject)settings["outputPolicy"]!;

        policy["codec"]!.GetValue<string>().Should().Be("aac");
        policy["mode"]!.GetValue<string>().Should().Be("cbr");
        policy["bitrateKbps"]!.GetValue<int>().Should().Be(256);
        policy["vbrQuality"]!.GetValue<int>().Should().Be(0);
        policy["sampleRate"]!.GetValue<string>().Should().Be("keep");

        var pacing = (JsonObject)settings["ytdlp"]!;

        pacing["sleepRequestsSeconds"]!.GetValue<double>().Should().Be(0.75);
        pacing["sleepIntervalSeconds"]!.GetValue<int>().Should().Be(10);
        pacing["maxSleepIntervalSeconds"]!.GetValue<int>().Should().Be(20);
        pacing["retries"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public async Task A_partial_update_changes_only_the_fields_it_carries()
    {
        using var factory = Factory();
        using var api = new Session(factory);

        var (status, body) = await api.PutAsync(
            $"{Endpoint}/settings",
            new { enabled = true, cookiesPath = "/config/cookies.txt" });

        status.Should().Be(HttpStatusCode.OK);

        var settings = (JsonObject)body!;

        settings["enabled"]!.GetValue<bool>().Should().BeTrue();
        settings["cookiesPath"]!.GetValue<string>().Should().Be("/config/cookies.txt");

        // Everything the body left out is untouched.
        settings["allowVideos"]!.GetValue<bool>().Should().BeFalse();
        settings["searchLimit"]!.GetValue<int>().Should().Be(20);
        settings["poTokenBaseUrl"].Should().BeNull();

        var pacing = (JsonObject)settings["ytdlp"]!;

        pacing["sleepRequestsSeconds"]!.GetValue<double>().Should().Be(0.75);

        // The file carries the change, so a restart does not lose it.
        var yaml = await File.ReadAllTextAsync(Path.Combine(factory.ConfigDir, "config.yml"));

        yaml.Should().Contain("enabled: true");
        yaml.Should().Contain("cookies_path: \"/config/cookies.txt\"");
        yaml.Should().NotContain("search_limit");
    }

    [Fact]
    public async Task An_empty_cookies_path_clears_it()
    {
        using var factory = Factory();
        using var api = new Session(factory);

        await api.PutAsync($"{Endpoint}/settings", new { cookiesPath = "/config/cookies.txt" });

        var (status, body) = await api.PutAsync($"{Endpoint}/settings", new { cookiesPath = string.Empty });

        status.Should().Be(HttpStatusCode.OK);
        ((JsonObject)body!)["cookiesPath"].Should().BeNull();
    }

    [Fact]
    public async Task An_out_of_range_value_is_refused_with_the_yaml_key_named()
    {
        using var factory = Factory();
        using var api = new Session(factory);

        var (status, body) = await api.PutAsync($"{Endpoint}/settings", new { searchLimit = 500 });

        status.Should().Be(HttpStatusCode.BadRequest);

        var messages = Problem(body)["errors"]!["settings"]!.AsArray();

        messages.Should().Contain(message => message!.GetValue<string>().Contains("youtube.search_limit:"));
    }

    [Fact]
    public async Task An_invalid_output_policy_is_refused_with_the_json_key_named()
    {
        using var factory = Factory();
        using var api = new Session(factory);

        var (status, body) = await api.PutAsync(
            $"{Endpoint}/settings",
            new { outputPolicy = new { codec = "flac" } });

        status.Should().Be(HttpStatusCode.BadRequest);

        var messages = Problem(body)["errors"]!["settings"]!.AsArray();

        messages.Should().Contain(message => message!.GetValue<string>().Contains("codec"));
    }

    [Fact]
    public async Task The_default_output_policy_is_stored_and_read_back()
    {
        using var factory = Factory();
        using var api = new Session(factory);

        var (status, body) = await api.PutAsync(
            $"{Endpoint}/settings",
            new { outputPolicy = new { codec = "mp3", mode = "vbr", vbrQuality = 2 } });

        status.Should().Be(HttpStatusCode.OK);

        var policy = (JsonObject)((JsonObject)body!)["outputPolicy"]!;

        policy["codec"]!.GetValue<string>().Should().Be("mp3");
        policy["mode"]!.GetValue<string>().Should().Be("vbr");
        policy["vbrQuality"]!.GetValue<int>().Should().Be(2);

        // A field the body left out keeps the default, the way a library's policy does.
        policy["bitrateKbps"]!.GetValue<int>().Should().Be(256);

        var (_, reread) = await api.GetAsync($"{Endpoint}/settings");

        ((JsonObject)((JsonObject)reread!)["outputPolicy"]!)["codec"]!.GetValue<string>().Should().Be("mp3");
    }

    [Fact]
    public async Task A_pacing_change_lands_in_the_nested_ytdlp_section_of_the_file()
    {
        using var factory = Factory();
        using var api = new Session(factory);

        var (status, body) = await api.PutAsync(
            $"{Endpoint}/settings",
            new { ytdlp = new { retries = 2, sleepIntervalSeconds = 15 } });

        status.Should().Be(HttpStatusCode.OK);

        var pacing = (JsonObject)((JsonObject)body!)["ytdlp"]!;

        pacing["retries"]!.GetValue<int>().Should().Be(2);
        pacing["sleepIntervalSeconds"]!.GetValue<int>().Should().Be(15);
        pacing["sleepRequestsSeconds"]!.GetValue<double>().Should().Be(0.75, "an absent flag keeps its value");

        // The nested keys land as a ytdlp: mapping, not as flat ytdlp:retries keys.
        var yaml = await File.ReadAllTextAsync(Path.Combine(factory.ConfigDir, "config.yml"));

        yaml.Should().Contain("ytdlp:").And.Contain("retries: 2").And.Contain("sleep_interval_seconds: 15");
        yaml.Should().NotContain("ytdlp:retries");
    }

    [Fact]
    public async Task A_field_the_environment_sets_is_refused_with_the_variable_named()
    {
        // The service reads the process environment, the way WondarrPaths.Resolve does, so the
        // variable is set for real and cleared again before the test returns.
        Environment.SetEnvironmentVariable("APP__YOUTUBE__SEARCH_LIMIT", "7");

        try
        {
            using var factory = Factory();
            using var api = new Session(factory);

            var (status, body) = await api.PutAsync($"{Endpoint}/settings", new { searchLimit = 9 });

            status.Should().Be(HttpStatusCode.BadRequest);

            var messages = Problem(body)["errors"]!["settings"]!.AsArray();

            messages.Should().Contain(message =>
                message!.GetValue<string>().Contains("APP__YOUTUBE__SEARCH_LIMIT"));

            var (_, read) = await api.GetAsync($"{Endpoint}/settings");

            var settings = (JsonObject)read!;

            settings["searchLimit"]!.GetValue<int>().Should().Be(7);
            settings["readOnlyFields"]!.AsArray().Should().Contain(
                field => field!.GetValue<string>() == "searchLimit");
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP__YOUTUBE__SEARCH_LIMIT", null);
        }
    }

    [Fact]
    public async Task The_status_reports_the_probe_the_health_checks_share()
    {
        var runner = Runner(("yt-dlp", "2026.01.01"), ("deno", "2.1.1"));
        using var factory = Factory(runner);
        using var api = new Session(factory);

        var (status, body) = await api.GetAsync($"{Endpoint}/status");

        status.Should().Be(HttpStatusCode.OK);

        var probe = (JsonObject)body!;

        probe["version"]!.GetValue<string>().Should().Be("2026.01.01");
        probe["hasJsRuntime"]!.GetValue<bool>().Should().BeTrue();
        probe["binaryAvailable"]!.GetValue<bool>().Should().BeTrue();

        // The status is the cached answer: the second read runs no process again.
        await api.GetAsync($"{Endpoint}/status");

        await runner.Received(2).RunAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_test_button_runs_the_probe_again()
    {
        var runner = Runner(("yt-dlp", "2026.01.01"), ("deno", "2.1.1"));
        using var factory = Factory(runner);
        using var api = new Session(factory);

        await api.GetAsync($"{Endpoint}/status");

        var (status, body) = await api.PostAsync($"{Endpoint}/test");

        status.Should().Be(HttpStatusCode.OK);

        var probe = (JsonObject)body!;

        probe["version"]!.GetValue<string>().Should().Be("2026.01.01");
        probe["hasJsRuntime"]!.GetValue<bool>().Should().BeTrue();

        // A fresh probe: the version and the JS runtime are asked for again, whatever was cached.
        await runner.Received(4).RunAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_binary_is_reported_as_a_probe_that_did_not_answer()
    {
        var runner = Substitute.For<IProcessRunner>();

        runner
            .RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ProcessResult>(new MediaToolMissingException("yt-dlp")));

        using var factory = Factory(runner);
        using var api = new Session(factory);

        var (status, body) = await api.PostAsync($"{Endpoint}/test");

        status.Should().Be(HttpStatusCode.OK);

        var probe = (JsonObject)body!;

        probe["version"].Should().BeNull();
        probe["binaryAvailable"]!.GetValue<bool>().Should().BeFalse();
        probe["hasJsRuntime"]!.GetValue<bool>().Should().BeFalse();
    }

    /// <summary>
    /// A runner that answers <c>--version</c> with the given versions, so the tests can count the
    /// probes: a probe asks yt-dlp first and the JS runtime second.
    /// </summary>
    private static IProcessRunner Runner(params (string Tool, string Version)[] versions)
    {
        var runner = Substitute.For<IProcessRunner>();
        var answers = versions.ToDictionary(pair => pair.Tool, pair => pair.Version, StringComparer.Ordinal);

        runner
            .RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var tool = callInfo.ArgAt<string>(0);

                return Task.FromResult(
                    answers.TryGetValue(tool, out var version)
                        ? new ProcessResult(0, $"{version}\n", string.Empty, TimedOut: false)
                        : new ProcessResult(1, string.Empty, string.Empty, TimedOut: false));
            });

        return runner;
    }

    /// <summary>A factory whose process runner is the given fake.</summary>
    private static WondarrAppFactory Factory(IProcessRunner? runner = null) =>
        new(configureServices: services =>
        {
            if (runner is not null)
            {
                services.RemoveAll<IProcessRunner>();
                services.AddSingleton(runner);
            }
        });

    /// <summary>The <c>errors.settings</c> member of an RFC 7807 validation problem.</summary>
    private static JsonObject Problem(JsonNode? body) => (JsonObject)body!;

    /// <summary>The API's client for one test, keeping every response body it saw.</summary>
    private sealed class Session : IDisposable
    {
        private readonly HttpClient _client;

        public Session(WondarrAppFactory factory)
        {
            _client = factory.CreateClient();
            _client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);
            Factory = factory;
        }

        private WondarrAppFactory Factory { get; }

        public Task<(HttpStatusCode Status, JsonNode? Body)> GetAsync(string path) =>
            SendAsync(HttpMethod.Get, path, json: null);

        public Task<(HttpStatusCode Status, JsonNode? Body)> PostAsync(string path) =>
            SendAsync(HttpMethod.Post, path, json: null);

        public Task<(HttpStatusCode Status, JsonNode? Body)> PutAsync(string path, object body) =>
            SendAsync(HttpMethod.Put, path, JsonSerializer.Serialize(body));

        public void Dispose() => _client.Dispose();

        private async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(
            HttpMethod method,
            string path,
            string? json)
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
