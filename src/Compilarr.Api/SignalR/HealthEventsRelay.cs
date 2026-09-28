using Compilarr.Api.Health;
using Compilarr.Core.HealthCheck;
using Compilarr.Core.Messaging;

namespace Compilarr.Api.SignalR;

/// <summary>
/// Broadcasts a freshly computed health result set as a <c>health</c>/<c>sync</c> message. The
/// action is <c>sync</c> rather than <c>updated</c> because the resource is the whole collection,
/// which is what the health endpoint returns.
/// </summary>
public sealed class HealthEventsRelay : IHandle<HealthCheckCompletedEvent>
{
    private readonly SignalRBroadcaster _broadcaster;

    /// <summary>Initialises a new instance of the <see cref="HealthEventsRelay"/> class.</summary>
    public HealthEventsRelay(SignalRBroadcaster broadcaster)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);

        _broadcaster = broadcaster;
    }

    /// <inheritdoc />
    public Task HandleAsync(HealthCheckCompletedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // The same projection the health endpoint serves, so the UI can replace its copy wholesale.
        var resources = message.Results
            .Select(result => new HealthResource(
                result.Source,
                result.Type,
                result.Message,
                result.WikiUrl?.ToString()))
            .ToList();

        return _broadcaster.BroadcastAsync(
            SignalRMessageNames.Health,
            "sync",
            resources,
            cancellationToken);
    }
}
