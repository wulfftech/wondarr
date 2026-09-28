using System.Net;
using System.Text;
using System.Text.Json;
using Compilarr.Api.SignalR;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Compilarr.Api.Tests;

/// <summary>
/// Exercises the events hub through the test server, over long polling: negotiate must be refused
/// without a key, and a real command or a health refresh must arrive as a message.
/// </summary>
public sealed class SignalRTests
{
    private const string HubPath = "signalr/events";
    private const string CommandEndpoint = "/api/v1/command";
    private const string HealthEndpoint = "/api/v1/health";

    /// <summary>The longest a test waits for a broadcast.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Connecting_without_a_key_is_rejected()
    {
        using var factory = new CompilarrAppFactory();
        await using var connection = CreateConnection(factory, key: null, inHeader: false);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());

        exception.Message.Should().Contain("401");
    }

    [Fact]
    public async Task Connecting_with_the_key_as_a_query_parameter_succeeds()
    {
        using var factory = new CompilarrAppFactory();
        await using var connection = CreateConnection(factory, factory.ApiKey, inHeader: false);

        await connection.StartAsync();

        connection.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task Connecting_with_the_key_in_a_header_succeeds()
    {
        using var factory = new CompilarrAppFactory();
        await using var connection = CreateConnection(factory, factory.ApiKey, inHeader: true);

        await connection.StartAsync();

        connection.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task Posting_a_command_broadcasts_updated_messages_until_it_completes()
    {
        using var factory = new CompilarrAppFactory();
        await using var connection = CreateConnection(factory, factory.ApiKey, inHeader: false);

        var updates = new List<SignalRMessage>();
        var completed = new TaskCompletionSource<SignalRMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<SignalRMessage>("ReceiveMessage", message =>
        {
            if (!string.Equals(message.Name, SignalRMessageNames.Command, StringComparison.Ordinal))
            {
                return;
            }

            lock (updates)
            {
                updates.Add(message);
            }

            if (string.Equals(ResourceStatus(message), "completed", StringComparison.Ordinal))
            {
                completed.TrySetResult(message);
            }
        });

        await connection.StartAsync();

        using var client = Authenticated(factory);
        using var posted = await PostCommandAsync(client, "{\"name\":\"Heartbeat\"}");
        posted.StatusCode.Should().Be(HttpStatusCode.Created);

        var id = (await ReadJsonAsync(posted)).GetProperty("id").GetInt64();

        var finished = await completed.Task.WaitAsync(Timeout);

        finished.Body.Action.Should().Be("updated");
        ResourceId(finished).Should().Be(id);
        ResourceStatus(finished).Should().Be("completed");

        lock (updates)
        {
            updates.Should().NotBeEmpty();
            updates.Should().OnlyContain(message => message.Body.Action == "updated");
            updates.Should().OnlyContain(message => message.Body.Resource is JsonElement);
        }
    }

    [Fact]
    public async Task Refreshing_health_broadcasts_a_sync_message()
    {
        using var factory = new CompilarrAppFactory();
        await using var connection = CreateConnection(factory, factory.ApiKey, inHeader: false);

        var synced = new TaskCompletionSource<SignalRMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<SignalRMessage>("ReceiveMessage", message =>
        {
            if (string.Equals(message.Name, SignalRMessageNames.Health, StringComparison.Ordinal))
            {
                synced.TrySetResult(message);
            }
        });

        await connection.StartAsync();

        using var client = Authenticated(factory);
        using var response = await client.GetAsync(new Uri($"{HealthEndpoint}?refresh=true", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var message = await synced.Task.WaitAsync(Timeout);

        message.Body.Action.Should().Be("sync");
        message.Body.Resource.Should().BeOfType<JsonElement>();
        ((JsonElement)message.Body.Resource!).GetArrayLength().Should().BeGreaterThan(0);
    }

    private static HubConnection CreateConnection(CompilarrAppFactory factory, string? key, bool inHeader)
    {
        var path = key is null || inHeader ? HubPath : $"{HubPath}?access_token={Uri.EscapeDataString(key)}";
        var url = new Uri(factory.Server.BaseAddress, path);

        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;

                if (inHeader && key is not null)
                {
                    options.Headers.Add("X-Api-Key", key);
                }
            })
            .AddJsonProtocol(options =>
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
            .Build();
    }

    private static HttpClient Authenticated(CompilarrAppFactory factory)
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

    private static string? ResourceStatus(SignalRMessage message) =>
        message.Body.Resource is JsonElement resource && resource.TryGetProperty("status", out var status)
            ? status.GetString()
            : null;

    private static long? ResourceId(SignalRMessage message) =>
        message.Body.Resource is JsonElement resource && resource.TryGetProperty("id", out var id)
            ? id.GetInt64()
            : null;
}
