using Microsoft.AspNetCore.SignalR;

namespace Compilarr.Api.SignalR;

/// <summary>
/// The one place that pushes to <see cref="EventsHub"/>. Relays depend on this rather than on
/// <see cref="IHubContext{THub}"/> so the message shape is built in a single spot.
/// </summary>
public sealed class SignalRBroadcaster
{
    private readonly IHubContext<EventsHub, IEventsClient> _hubContext;

    /// <summary>Initialises a new instance of the <see cref="SignalRBroadcaster"/> class.</summary>
    public SignalRBroadcaster(IHubContext<EventsHub, IEventsClient> hubContext)
    {
        ArgumentNullException.ThrowIfNull(hubContext);

        _hubContext = hubContext;
    }

    /// <summary>Sends one message to every connected client.</summary>
    /// <param name="name">The resource family, one of <see cref="SignalRMessageNames"/>.</param>
    /// <param name="action">The change: <c>updated</c>, <c>sync</c> or <c>deleted</c>.</param>
    /// <param name="resource">The resource the action applies to, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    public Task BroadcastAsync(
        string name,
        string action,
        object? resource,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(action);

        return _hubContext.Clients.All.ReceiveMessage(
            new SignalRMessage(name, new SignalRMessageBody(action, resource)));
    }
}
