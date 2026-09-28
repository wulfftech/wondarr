using Wondarr.Api.Commands;
using Wondarr.Core.Jobs;
using Wondarr.Core.Messaging;

namespace Wondarr.Api.SignalR;

/// <summary>
/// Broadcasts every command state change to the UI as a <c>command</c>/<c>updated</c> message. The
/// resource is the same <c>CommandResource</c> the command API returns, so a client can patch its
/// list from the message without re-fetching.
/// </summary>
public sealed class CommandEventsRelay : IHandle<CommandUpdatedEvent>
{
    private readonly SignalRBroadcaster _broadcaster;

    /// <summary>Initialises a new instance of the <see cref="CommandEventsRelay"/> class.</summary>
    public CommandEventsRelay(SignalRBroadcaster broadcaster)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);

        _broadcaster = broadcaster;
    }

    /// <inheritdoc />
    public Task HandleAsync(CommandUpdatedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        return _broadcaster.BroadcastAsync(
            SignalRMessageNames.Command,
            "updated",
            message.Command.ToResource(),
            cancellationToken);
    }
}
