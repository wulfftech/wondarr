using Wondarr.Core.Messaging;

namespace Wondarr.Core.Jobs;

/// <summary>
/// Published every time a command row changes state (queued, started, progress, finished). P0-07
/// relays this over SignalR to the UI.
/// </summary>
/// <param name="Command">The command as it looks after the change.</param>
public sealed record CommandUpdatedEvent(CommandRecord Command) : IEvent;
