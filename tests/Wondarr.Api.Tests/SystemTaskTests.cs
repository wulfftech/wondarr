using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// <c>GET /api/v1/system/task</c> reads the <c>job</c> table the scheduler filled in at start-up, so
/// these tests also prove the hosted service ran.
/// </summary>
public sealed class SystemTaskTests
{
    private const string TaskEndpoint = "/api/v1/system/task";

    [Fact]
    public async Task Tasks_without_a_key_are_unauthorized()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(TaskEndpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tasks_list_the_scheduled_tasks_with_their_intervals()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.GetAsync(new Uri(TaskEndpoint, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var tasks = document.RootElement.EnumerateArray().ToList();
        tasks.Should().HaveCount(2);

        var heartbeat = tasks.Single(task => task.GetProperty("taskName").GetString() == "Heartbeat");
        heartbeat.GetProperty("name").GetString().Should().Be("Heartbeat");
        heartbeat.GetProperty("interval").GetInt32().Should().Be(1);
        heartbeat.GetProperty("lastDuration").GetString().Should().Be("00:00:00");
        heartbeat.GetProperty("nextExecution").GetDateTime().Should().BeAfter(DateTime.UtcNow);

        var checkHealth = tasks.Single(task => task.GetProperty("taskName").GetString() == "CheckHealth");
        checkHealth.GetProperty("name").GetString().Should().Be("Check Health");
        checkHealth.GetProperty("interval").GetInt32().Should().Be(15);

        // camelCase everywhere: no PascalCase leftovers.
        heartbeat.TryGetProperty("TaskName", out _).Should().BeFalse();
    }

    [Fact]
    public async Task One_task_can_be_read_by_id()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.GetAsync(new Uri(TaskEndpoint, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var id = document.RootElement.EnumerateArray().First().GetProperty("id").GetInt64();

        using var one = await client.GetAsync(new Uri($"{TaskEndpoint}/{id}", UriKind.Relative));

        one.StatusCode.Should().Be(HttpStatusCode.OK);
        using var oneDocument = JsonDocument.Parse(await one.Content.ReadAsStringAsync());
        oneDocument.RootElement.GetProperty("id").GetInt64().Should().Be(id);
    }

    [Fact]
    public async Task An_unknown_task_id_is_not_found()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.GetAsync(new Uri($"{TaskEndpoint}/999999", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
