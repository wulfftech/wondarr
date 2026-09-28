namespace Wondarr.Api.SignalR;

/// <summary>
/// The client-side methods the events hub may call. Server-callable hub methods do not exist yet:
/// the hub is a one-way feed of <see cref="SignalRMessage"/>s.
/// </summary>
public interface IEventsClient
{
    /// <summary>Receives one broadcast.</summary>
    /// <param name="message">The message to act on.</param>
    Task ReceiveMessage(SignalRMessage message);
}
