using Microsoft.AspNetCore.SignalR;

namespace Wondarr.Api.SignalR;

/// <summary>
/// The events hub the UI connects to at <c>/signalr/events</c>. It has no server-callable methods:
/// everything travels from the server to the clients, so the strongly typed
/// <see cref="IEventsClient"/> is the whole contract.
/// </summary>
public sealed class EventsHub : Hub<IEventsClient>
{
}
