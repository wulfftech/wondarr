using System.Net;
using System.Text;
using System.Text.Json;
using Wondarr.Core.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wondarr.Api.Tests;

public sealed class CommandTests
{
    private const string CommandEndpoint = "/api/v1/command";

    /// <summary>The longest a test waits for a command to reach a state.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Posting_a_command_without_a_key_is_unauthorized()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await PostCommandAsync(client, "{\"name\":\"Heartbeat\"}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Posting_an_unknown_command_is_a_bad_request()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await PostCommandAsync(client, "{\"name\":\"NoSuchCommand\"}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("NoSuchCommand");
    }

    [Fact]
    public async Task Posting_a_heartbeat_returns_201_and_the_command_completes()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await PostCommandAsync(client, "{\"name\":\"Heartbeat\"}");
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await ReadJsonAsync(response);
        created.GetProperty("name").GetString().Should().Be("Heartbeat");
        created.GetProperty("commandName").GetString().Should().Be("Heartbeat");
        created.GetProperty("priority").GetString().Should().Be("normal");
        created.GetProperty("trigger").GetString().Should().Be("manual");

        var id = created.GetProperty("id").GetInt64();

        var finished = await WaitForTerminalStatusAsync(client, id);

        finished.GetProperty("status").GetString().Should().Be("completed");
        finished.GetProperty("result").GetString().Should().Be("successful");
        finished.GetProperty("message").GetString().Should().Be("Heartbeat OK");
        finished.GetProperty("exception").ValueKind.Should().Be(JsonValueKind.Null);
        finished.GetProperty("body").GetString().Should().Contain("Heartbeat");
        finished.GetProperty("ended").ValueKind.Should().Be(JsonValueKind.String);
        finished.GetProperty("duration").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task Listing_commands_includes_the_posted_command()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var posted = await PostCommandAsync(client, "{\"name\":\"Heartbeat\"}");
        var created = await ReadJsonAsync(posted);
        var id = created.GetProperty("id").GetInt64();

        using var response = await client.GetAsync(new Uri(CommandEndpoint, UriKind.Relative));
        var commands = await ReadJsonAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        commands.EnumerateArray().Should().Contain(command => command.GetProperty("id").GetInt64() == id);
    }

    [Fact]
    public async Task Reading_a_command_that_does_not_exist_is_a_not_found()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(new Uri($"{CommandEndpoint}/987654", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deleting_a_completed_command_is_a_conflict()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var posted = await PostCommandAsync(client, "{\"name\":\"Heartbeat\"}");
        var id = (await ReadJsonAsync(posted)).GetProperty("id").GetInt64();
        await WaitForTerminalStatusAsync(client, id);

        using var response = await client.DeleteAsync(new Uri($"{CommandEndpoint}/{id}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Deleting_a_queued_command_cancels_it()
    {
        // Three blocking handlers occupy every worker, so the fourth command stays queued until the
        // test deletes it.
        var blockers = Enumerable.Range(1, 3)
            .Select(index => new BlockingApiCommandHandler($"Hold{index}"))
            .ToArray();

        // A fourth handler that is registered but never started: its command must stay queued.
        var waiting = new BlockingApiCommandHandler("Hold4");

        using var factory = new WondarrAppFactory(configureServices: services =>
        {
            foreach (var blocker in blockers.Append(waiting))
            {
                services.AddSingleton<ICommandHandler>(blocker);
            }
        });
        using var client = Authenticated(factory);

        var runningIds = new List<long>();
        foreach (var blocker in blockers)
        {
            using var posted = await PostCommandAsync(client, $"{{\"name\":\"{blocker.Name}\"}}");
            runningIds.Add((await ReadJsonAsync(posted)).GetProperty("id").GetInt64());
        }

        foreach (var id in runningIds)
        {
            await WaitForStatusAsync(client, id, "started");
        }

        using var queuedResponse = await PostCommandAsync(client, "{\"name\":\"Hold4\"}");
        var queued = await ReadJsonAsync(queuedResponse);
        var queuedId = queued.GetProperty("id").GetInt64();
        queued.GetProperty("status").GetString().Should().Be("queued");

        using var cancelled = await client.DeleteAsync(new Uri($"{CommandEndpoint}/{queuedId}", UriKind.Relative));
        cancelled.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var reread = await client.GetAsync(new Uri($"{CommandEndpoint}/{queuedId}", UriKind.Relative));
        (await ReadJsonAsync(reread)).GetProperty("status").GetString().Should().Be("cancelled");

        using var again = await client.DeleteAsync(new Uri($"{CommandEndpoint}/{queuedId}", UriKind.Relative));
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private static HttpClient Authenticated(WondarrAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        return client;
    }

    private static Task<HttpResponseMessage> PostCommandAsync(HttpClient client, string json) =>
        client.PostAsync(
            new Uri(CommandEndpoint, UriKind.Relative),
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();

        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static async Task<JsonElement> WaitForTerminalStatusAsync(HttpClient client, long id)
    {
        var deadline = DateTime.UtcNow + Timeout;
        var last = default(JsonElement);

        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync(new Uri($"{CommandEndpoint}/{id}", UriKind.Relative));
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            last = await ReadJsonAsync(response);

            if (last.GetProperty("status").GetString() is "completed" or "failed" or "aborted")
            {
                return last;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException($"Command {id} never finished; last seen as {last}");
    }

    private static async Task WaitForStatusAsync(HttpClient client, long id, string status)
    {
        var deadline = DateTime.UtcNow + Timeout;
        var last = string.Empty;

        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync(new Uri($"{CommandEndpoint}/{id}", UriKind.Relative));
            var command = await ReadJsonAsync(response);
            last = command.GetProperty("status").GetString() ?? string.Empty;

            if (string.Equals(last, status, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException($"Command {id} never reached '{status}'; last seen as '{last}'");
    }
}

/// <summary>A command handler that blocks until the host is stopped, so workers stay busy.</summary>
internal sealed class BlockingApiCommandHandler : ICommandHandler
{
    public BlockingApiCommandHandler(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        Name = name;
    }

    public string Name { get; }

    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        return null;
    }
}
