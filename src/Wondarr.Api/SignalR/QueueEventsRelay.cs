using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Messaging;

namespace Wondarr.Api.SignalR;

/// <summary>
/// Broadcasts the queue poll's progress as <c>queue</c>/<c>updated</c> messages and an imported song
/// as <c>song</c>/<c>updated</c>, so the queue and the song list follow a grab without polling. The
/// resource is the little the UI needs to patch one row of its list.
/// </summary>
public sealed class QueueEventsRelay : IHandle<QueueItemChangedEvent>, IHandle<SongImportedEvent>
{
    private readonly SignalRBroadcaster _broadcaster;

    /// <summary>Initialises a new instance of the <see cref="QueueEventsRelay"/> class.</summary>
    /// <param name="broadcaster">The one place that pushes to the events hub.</param>
    public QueueEventsRelay(SignalRBroadcaster broadcaster)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);

        _broadcaster = broadcaster;
    }

    /// <inheritdoc />
    public Task HandleAsync(QueueItemChangedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        return _broadcaster.BroadcastAsync(
            SignalRMessageNames.Queue,
            "updated",
            new QueueItemUpdateResource(message.QueueItemId, message.SongId, message.State, message.Progress, message.Message),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task HandleAsync(SongImportedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        return _broadcaster.BroadcastAsync(
            SignalRMessageNames.Song,
            "updated",
            new SongUpdateResource(message.SongId),
            cancellationToken);
    }
}

/// <summary>What a <c>queue</c>/<c>updated</c> message carries about one item.</summary>
/// <param name="Id">The queue item's id.</param>
/// <param name="SongId">The song the grab is for.</param>
/// <param name="State">Where the grab stands.</param>
/// <param name="Progress">How far the download has got, 0–1.</param>
/// <param name="Message">The last thing the source or the poll said, or <see langword="null"/>.</param>
internal sealed record QueueItemUpdateResource(
    long Id,
    long SongId,
    QueueItemState State,
    double Progress,
    string? Message);

/// <summary>What a <c>song</c>/<c>updated</c> message carries about one song.</summary>
/// <param name="Id">The song's id.</param>
internal sealed record SongUpdateResource(long Id);