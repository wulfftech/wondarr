using Compilarr.Core.HealthCheck;
using Compilarr.Core.Messaging;

namespace Compilarr.Core.Tests.HealthCheck;

/// <summary>
/// Records everything published through the aggregator so a test can assert what
/// <see cref="HealthCheckService"/> announced.
/// </summary>
internal sealed class RecordingEventAggregator : IEventAggregator
{
    private readonly List<IEvent> _published = [];

    /// <summary>The published events, in order.</summary>
    public IReadOnlyList<IEvent> Published => _published;

    /// <summary>Only the completion events, which is what the service is expected to publish.</summary>
    public IReadOnlyList<HealthCheckCompletedEvent> Completed =>
        _published.OfType<HealthCheckCompletedEvent>().ToList();

    /// <inheritdoc />
    public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : IEvent
    {
        _published.Add(message);

        return Task.CompletedTask;
    }
}
