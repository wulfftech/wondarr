using System.Net;
using System.Text.Json;
using Wondarr.Core.HealthCheck;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wondarr.Api.Tests;

public sealed class HealthTests
{
    private const string HealthEndpoint = "/api/v1/health";

    [Fact]
    public async Task Health_without_a_key_is_unauthorized()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(HealthEndpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_reports_the_database_and_the_folders_as_ok()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.GetAsync(new Uri(HealthEndpoint, UriKind.Relative));
        var entries = await ReadEntriesAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var core = new[] { "DatabaseHealthCheck", "ConfigFolderHealthCheck", "LogFolderHealthCheck" };
        entries.Where(entry => core.Contains(entry.Source)).Should().HaveCount(3)
            .And.OnlyContain(entry => entry.Type == "ok");

        // No slskd binary on a test machine: the bundled-slskd check reports a warning, which is
        // not an error, so the endpoint still answers 200.
        entries.Should().ContainSingle(entry => entry.Source == "slskd" && entry.Type == "warning");
    }

    [Fact]
    public async Task Health_returns_503_when_a_check_reports_an_error()
    {
        using var factory = new WondarrAppFactory(configureServices: services => services.AddSingleton<IHealthCheck>(
            new FakeHealthCheck("Failing", () => new HealthCheck("Failing", HealthCheckResult.Error, "something is broken", null))));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.GetAsync(new Uri(HealthEndpoint, UriKind.Relative));
        var entries = await ReadEntriesAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        entries.Should().ContainSingle(entry => entry.Source == "Failing" && entry.Type == "error");
    }

    [Fact]
    public async Task Health_turns_a_thrown_exception_into_an_error_entry()
    {
        using var factory = new WondarrAppFactory(configureServices: services => services.AddSingleton<IHealthCheck>(
            new FakeHealthCheck("Exploding", () => throw new InvalidOperationException("the check exploded"))));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.GetAsync(new Uri(HealthEndpoint, UriKind.Relative));
        var entries = await ReadEntriesAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        entries.Should().ContainSingle(entry => entry.Source == "Exploding" && entry.Type == "error");
        entries.Single(entry => entry.Source == "Exploding").Message.Should().Contain("the check exploded");
    }

    [Fact]
    public async Task Refresh_runs_the_checks_again()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.GetAsync(new Uri($"{HealthEndpoint}?refresh=true", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadEntriesAsync(response)).Should().HaveCount(
            5,
            "database, config folder, log folder, slskd and the media tools");
    }

    private static async Task<List<(string Source, string Type, string Message)>> ReadEntriesAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        return document.RootElement
            .EnumerateArray()
            .Select(entry => (
                Source: entry.GetProperty("source").GetString()!,
                Type: entry.GetProperty("type").GetString()!,
                Message: entry.GetProperty("message").GetString()!))
            .ToList();
    }

    [Fact]
    public async Task Health_results_are_shared_between_requests()
    {
        var calls = 0;
        using var factory = new WondarrAppFactory(configureServices: services => services.AddSingleton<IHealthCheck>(
            new FakeHealthCheck("Counting", () =>
            {
                Interlocked.Increment(ref calls);
                return new HealthCheck("Counting", HealthCheckResult.Ok, "fine", null);
            })));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        (await client.GetAsync(new Uri("/api/v1/health", UriKind.Relative))).EnsureSuccessStatusCode();
        (await client.GetAsync(new Uri("/api/v1/health", UriKind.Relative))).EnsureSuccessStatusCode();

        calls.Should().Be(1);
    }
}
