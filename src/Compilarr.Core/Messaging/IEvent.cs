namespace Compilarr.Core.Messaging;

/// <summary>
/// Marker for everything published through the <see cref="IEventAggregator"/>. Handlers are
/// resolved by the event's runtime type, so an event type of its own is what makes a message
/// routable.
/// </summary>
public interface IEvent
{
}
