using System.Net;
using System.Text.Json;
using Compilarr.Core.HealthCheck;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Compilarr.Api.Tests;

public sealed class HealthTests
{
    private const string HealthEndpoint = "/api/v1/health";

    [Fact]
    public async Task Health_without_a_key_is_unauthorized()
    {
        using var factory = new CompilarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(HealthEndpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_reports_the_database_and_the_folders_as_ok()
    {
        using var factory = new CompilarrAppFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.GetAsync(new Uri(HealthEndpoint, UriKind.Relative));
        var entries = await ReadEntriesAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        entries.Should().HaveCount(3);
        entries.Select(entry => entry.Source).Should().BeEquivalentTo(
            "DatabaseHealthCheck",
            "ConfigFolderHealthCheck",
            "LogFolderHealthCheck");
        entries.Should().OnlyContain(entry => entry.Type == "ok");
    }

    [Fact]
    public async Task Health_returns_503_when_a_check_reports_an_error()
    {
        using var factory = new CompilarrAppFactory(configureServices: services => services.AddSingleton<IHealthCheck>(
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
        using var factory = new CompilarrAppFactory(configureServices: services => services.AddSingleton<IHealthCheck>(
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
        using var factory = new CompilarrAppFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.GetAsync(new Uri($"{HealthEndpoint}?refresh=true", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadEntriesAsync(response)).Should().HaveCount(3);
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
}