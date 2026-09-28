using System.Collections.Concurrent;
using Compilarr.Core.Jobs;
using Compilarr.Core.Messaging;

namespace Compilarr.Core.Tests.Jobs;

/// <summary>Records every <see cref="CommandUpdatedEvent"/> in the order the aggregator delivers them.</summary>
internal sealed class RecordingEventHandler : IHandle<CommandUpdatedEvent>
{
    private readonly ConcurrentQueue<(long Id, CommandStatus Status)> _updates = new();

    /// <summary>The statuses seen so far, oldest first.</summary>
    public IReadOnlyList<(long Id, CommandStatus Status)> Updates => _updates.ToArray();

    public Task HandleAsync(CommandUpdatedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        _updates.Enqueue((message.Command.Id, message.Command.Status));

        return Task.CompletedTask;
    }
}

/// <summary>An event handler that always throws, so the aggregator's isolation can be checked.</summary>
internal sealed class ThrowingEventHandler : IHandle<CommandUpdatedEvent>
{
    public Task HandleAsync(CommandUpdatedEvent message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("the event handler exploded");
}
