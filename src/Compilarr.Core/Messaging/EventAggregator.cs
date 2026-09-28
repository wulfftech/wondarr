using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Compilarr.Core.Messaging;

/// <summary>
/// Resolves handlers from a new DI scope per publish, so a handler may depend on scoped services
/// (the database, repositories) without the aggregator itself being scoped. Registered as a
/// singleton: there is no per-subscriber state to keep.
/// </summary>
public sealed partial class EventAggregator : IEventAggregator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EventAggregator> _logger;

    /// <summary>Initialises a new instance of the <see cref="EventAggregator"/> class.</summary>
    public EventAggregator(IServiceScopeFactory scopeFactory, ILogger<EventAggregator> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(message);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var handlers = scope.ServiceProvider.GetServices<IHandle<TEvent>>();

        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A subscriber that blows up is a subscriber problem: it must not stop the others,
                // and it must never take down the publisher.
                LogHandlerFailed(typeof(TEvent).Name, handler.GetType().Name, exception);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Handler {HandlerName} failed while handling {EventName}")]
    private partial void LogHandlerFailed(string eventName, string handlerName, Exception exception);
}
