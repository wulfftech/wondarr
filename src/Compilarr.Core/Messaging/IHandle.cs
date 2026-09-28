namespace Compilarr.Core.Messaging;

/// <summary>
/// Handles one kind of <see cref="IEvent"/>. Implementations are registered as services and are
/// resolved in a fresh DI scope for every publish.
/// </summary>
/// <typeparam name="TEvent">The event type this handler reacts to.</typeparam>
public interface IHandle<TEvent>
    where TEvent : IEvent
{
    /// <summary>Reacts to <paramref name="message"/>.</summary>
    /// <param name="message">The published event.</param>
    /// <param name="cancellationToken">Cancels the handler.</param>
    Task HandleAsync(TEvent message, CancellationToken cancellationToken);
}
