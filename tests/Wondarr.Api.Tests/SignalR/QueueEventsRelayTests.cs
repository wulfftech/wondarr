using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Wondarr.Api.SignalR;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Messaging;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// A queue item change published by the poll reaches a connected SignalR client as a
/// <c>queue</c>/<c>updated</c> message, so the queue screen follows a grab without polling.
/// </summary>
public sealed class QueueEventsRelayTests
{
    private const string HubPath = "signalr/events";

    /// <summary>The longest a test waits for a broadcast.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_queue_item_change_is_broadcast_as_a_queue_message()
    {
        using var factory = new WondarrAppFactory();
        await using var connection = CreateConnection(factory, factory.ApiKey);

        var received = new TaskCompletionSource<SignalRMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<SignalRMessage>("ReceiveMessage", message =>
        {
            if (string.Equals(message.Name, SignalRMessageNames.Queue, StringComparison.Ordinal))
            {
                received.TrySetResult(message);
            }
        });

        await connection.StartAsync();

        var events = factory.Services.GetRequiredService<IEventAggregator>();

        await events.PublishAsync(
            new QueueItemChangedEvent(7, 42, QueueItemState.Downloading)
            {
                Progress = 0.5,
                Message = "half way",
            });

        var message = await received.Task.WaitAsync(Timeout);

        message.Body.Action.Should().Be("updated");

        var resource = message.Body.Resource.Should().BeOfType<JsonElement>().Subject;
        resource.GetProperty("id").GetInt64().Should().Be(7);
        resource.GetProperty("songId").GetInt64().Should().Be(42);
        resource.GetProperty("state").GetString().Should().Be("downloading");
        resource.GetProperty("progress").GetDouble().Should().Be(0.5);
        resource.GetProperty("message").GetString().Should().Be("half way");
    }

    [Fact]
    public async Task An_imported_song_is_broadcast_as_a_song_message()
    {
        using var factory = new WondarrAppFactory();
        await using var connection = CreateConnection(factory, factory.ApiKey);

        var received = new TaskCompletionSource<SignalRMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<SignalRMessage>("ReceiveMessage", message =>
        {
            if (string.Equals(message.Name, SignalRMessageNames.Song, StringComparison.Ordinal))
            {
                received.TrySetResult(message);
            }
        });

        await connection.StartAsync();

        var events = factory.Services.GetRequiredService<IEventAggregator>();

        await events.PublishAsync(new SongImportedEvent(42, 9, true));

        var message = await received.Task.WaitAsync(Timeout);

        message.Body.Action.Should().Be("updated");

        var resource = message.Body.Resource.Should().BeOfType<JsonElement>().Subject;
        resource.GetProperty("id").GetInt64().Should().Be(42);
    }

    private static HubConnection CreateConnection(WondarrAppFactory factory, string key) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, $"{HubPath}?access_token={Uri.EscapeDataString(key)}"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .AddJsonProtocol(options =>
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
            .Build();
}
