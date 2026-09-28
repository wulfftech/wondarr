namespace Wondarr.Core.Messaging;

/// <summary>
/// In-process publish/subscribe. Publishing is awaited but never fails because of a handler: the
/// publisher must not be brought down by a subscriber.
/// </summary>
public interface IEventAggregator
{
    /// <summary>
    /// Runs every <see cref="IHandle{TEvent}"/> registered for <paramref name="message"/>, in turn.
    /// A handler that throws is logged and the remaining handlers still run.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="message">The event to publish.</param>
    /// <param name="cancellationToken">Cancels the handlers.</param>
    Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : IEvent;
}
