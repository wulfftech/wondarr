namespace Wondarr.Core.Jobs;

/// <summary>
/// Where a command is in its life cycle. The names and ordering follow Lidarr
/// (<c>src/NzbDrone.Core/Messaging/Commands/CommandStatus.cs</c>); the values are stored as strings.
/// </summary>
public enum CommandStatus
{
    /// <summary>Waiting for a free executor slot.</summary>
    Queued = 0,

    /// <summary>A handler is running.</summary>
    Started = 1,

    /// <summary>The handler returned without throwing.</summary>
    Completed = 2,

    /// <summary>The handler threw.</summary>
    Failed = 3,

    /// <summary>The host stopped while the handler ran.</summary>
    Aborted = 4,

    /// <summary>Cancelled before it started.</summary>
    Cancelled = 5,

    /// <summary>Left <see cref="Queued"/> or <see cref="Started"/> by a previous process.</summary>
    Orphaned = 6,
}
