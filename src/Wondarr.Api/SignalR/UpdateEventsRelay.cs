using Wondarr.Api.Updates;
using Wondarr.Core.Messaging;
using Wondarr.Core.Updates;

namespace Wondarr.Api.SignalR;

/// <summary>
/// Broadcasts an <c>update</c>/<c>updated</c> message after every check that asked GitHub, so an open
/// tab refetches <c>GET /api/v1/update</c> and shows the badge without a reload.
/// </summary>
public sealed class UpdateEventsRelay : IHandle<UpdateCheckedEvent>
{
    private readonly SignalRBroadcaster _broadcaster;

    /// <summary>Initialises a new instance of the <see cref="UpdateEventsRelay"/> class.</summary>
    /// <param name="broadcaster">Pushes to the events hub.</param>
    public UpdateEventsRelay(SignalRBroadcaster broadcaster)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);

        _broadcaster = broadcaster;
    }

    /// <inheritdoc />
    public Task HandleAsync(UpdateCheckedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        return _broadcaster.BroadcastAsync(
            SignalRMessageNames.Update,
            "updated",
            UpdateResource.From(message.Status),
            cancellationToken);
    }
}
