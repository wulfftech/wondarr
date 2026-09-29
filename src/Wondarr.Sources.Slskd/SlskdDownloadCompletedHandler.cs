using Wondarr.Core.Importing;
using Wondarr.Core.Messaging;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// Wakes the queue poll when slskd reports a finished file, so the import starts in the same second
/// instead of at the next tick. The event is only a hint: the poll reads the transfers API anyway, so
/// a lost event costs latency and never correctness (ARCHITECTURE §5.5).
/// </summary>
public sealed class SlskdDownloadCompletedHandler : IHandle<SlskdDownloadCompletedEvent>
{
    private readonly IQueueTracker _tracker;

    /// <summary>Initialises a new instance of the <see cref="SlskdDownloadCompletedHandler"/> class.</summary>
    /// <param name="tracker">The queue poll to wake.</param>
    public SlskdDownloadCompletedHandler(IQueueTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        _tracker = tracker;
    }

    /// <inheritdoc />
    public Task HandleAsync(SlskdDownloadCompletedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        _tracker.Wake();

        return Task.CompletedTask;
    }
}