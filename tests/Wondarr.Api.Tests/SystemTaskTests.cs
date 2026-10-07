using System.Net;
using System.Text.Json;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// <c>GET /api/v1/system/task</c> reads the <c>job</c> table the scheduler filled in at start-up, so
/// these tests also prove the hosted service ran. The run times come from the <c>command</c> table.
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
        tasks.Should().HaveCount(6, "the catalog holds the two Phase 0 tasks, the missing-song and upgrade searches, the reference-library scan and the backup");

        var heartbeat = tasks.Single(task => task.GetProperty("taskName").GetString() == "Heartbeat");
        heartbeat.GetProperty("name").GetString().Should().Be("Heartbeat");
        heartbeat.GetProperty("interval").GetInt32().Should().Be(1);
        heartbeat.GetProperty("lastDuration").GetString().Should().Be("00:00:00");
        heartbeat.GetProperty("nextExecution").GetDateTime().Should().BeAfter(DateTime.UtcNow);

        var checkHealth = tasks.Single(task => task.GetProperty("taskName").GetString() == "CheckHealth");
        checkHealth.GetProperty("name").GetString().Should().Be("Check Health");
        checkHealth.GetProperty("interval").GetInt32().Should().Be(15);

        var missingSearch = tasks.Single(task => task.GetProperty("taskName").GetString() == "MissingSearch");
        missingSearch.GetProperty("name").GetString().Should().Be("Missing Search");
        missingSearch.GetProperty("interval").GetInt32().Should().Be(360);

        var upgradeSearch = tasks.Single(task => task.GetProperty("taskName").GetString() == "UpgradeSearch");
        upgradeSearch.GetProperty("name").GetString().Should().Be("Upgrade Search");
        upgradeSearch.GetProperty("interval").GetInt32().Should().Be(1440);

        var referenceScan = tasks.Single(task => task.GetProperty("taskName").GetString() == "ReferenceLibraryScan");
        referenceScan.GetProperty("interval").GetInt32().Should().Be(1440);

        var backup = tasks.Single(task => task.GetProperty("taskName").GetString() == "Backup");
        backup.GetProperty("name").GetString().Should().Be("Backup");
        backup.GetProperty("interval").GetInt32().Should().Be(7 * 24 * 60);

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

    [Fact]
    public async Task A_finished_command_gives_the_task_its_run_time()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        var startedAt = TruncatedToSecond(DateTime.UtcNow.AddSeconds(-2));
        var endedAt = startedAt.AddSeconds(2);

        using (var scope = factory.Services.CreateScope())
        {
            await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
            context.Commands.Add(new CommandRecord
            {
                Name = "Heartbeat",
                Status = CommandStatus.Completed,
                Result = CommandResult.Successful,
                Trigger = CommandTrigger.Scheduled,
                QueuedAt = startedAt.AddSeconds(-1),
                StartedAt = startedAt,
                EndedAt = endedAt,
            });
            await context.SaveChangesAsync();
        }

        using var response = await client.GetAsync(new Uri(TaskEndpoint, UriKind.Relative));
        var tasks = await ReadTasksAsync(response);

        var heartbeat = tasks.Single(task => task.GetProperty("taskName").GetString() == "Heartbeat");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        heartbeat.GetProperty("lastDuration").GetString().Should().Be("00:00:02");
        heartbeat.GetProperty("lastStartTime").GetDateTime().Should().BeCloseTo(startedAt, TimeSpan.FromMilliseconds(50));
        // The job row's own last-run time is when the task finished, not when it started.
        heartbeat.GetProperty("lastExecution").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_task_without_commands_reports_the_fallbacks()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        var lastRunAt = TruncatedToSecond(DateTime.UtcNow.AddMinutes(-5));

        using (var scope = factory.Services.CreateScope())
        {
            await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
            var job = await context.Jobs.SingleAsync(row => row.Name == "Heartbeat");
            job.LastRunAt = lastRunAt;
            await context.SaveChangesAsync();
        }

        using var response = await client.GetAsync(new Uri(TaskEndpoint, UriKind.Relative));
        var tasks = await ReadTasksAsync(response);

        var heartbeat = tasks.Single(task => task.GetProperty("taskName").GetString() == "Heartbeat");

        heartbeat.GetProperty("lastDuration").GetString().Should().Be("00:00:00");
        heartbeat.GetProperty("lastStartTime").GetDateTime().Should().BeCloseTo(lastRunAt, TimeSpan.FromMilliseconds(50));
        heartbeat.GetProperty("lastExecution").GetDateTime().Should().BeCloseTo(lastRunAt, TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task The_newest_finished_command_wins()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        var startedAt = TruncatedToSecond(DateTime.UtcNow.AddSeconds(-30));

        using (var scope = factory.Services.CreateScope())
        {
            await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
            context.Commands.Add(new CommandRecord
            {
                Name = "heartbeat",
                Status = CommandStatus.Completed,
                QueuedAt = startedAt,
                StartedAt = startedAt,
                EndedAt = startedAt.AddSeconds(1),
            });
            context.Commands.Add(new CommandRecord
            {
                Name = "Heartbeat",
                Status = CommandStatus.Completed,
                QueuedAt = startedAt.AddSeconds(10),
                StartedAt = startedAt.AddSeconds(10),
                EndedAt = startedAt.AddSeconds(13),
            });
            // A command that never finished is not a run time.
            context.Commands.Add(new CommandRecord
            {
                Name = "Heartbeat",
                Status = CommandStatus.Started,
                QueuedAt = startedAt.AddSeconds(20),
                StartedAt = startedAt.AddSeconds(20),
            });
            await context.SaveChangesAsync();
        }

        using var response = await client.GetAsync(new Uri(TaskEndpoint, UriKind.Relative));
        var tasks = await ReadTasksAsync(response);

        var heartbeat = tasks.Single(task => task.GetProperty("taskName").GetString() == "Heartbeat");

        heartbeat.GetProperty("lastDuration").GetString().Should().Be("00:00:03");
        heartbeat.GetProperty("lastStartTime").GetDateTime().Should().BeCloseTo(startedAt.AddSeconds(10), TimeSpan.FromMilliseconds(50));
    }

    private static HttpClient Authenticated(WondarrAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        return client;
    }

    private static async Task<IReadOnlyList<JsonElement>> ReadTasksAsync(HttpResponseMessage response)
    {
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return [.. document.RootElement.EnumerateArray()];
    }

    /// <summary>Whole seconds, so the stored value survives the database round trip exactly.</summary>
    private static DateTime TruncatedToSecond(DateTime value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));
}
