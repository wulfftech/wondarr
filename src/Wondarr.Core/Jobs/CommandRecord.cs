using Wondarr.Core.Persistence;

namespace Wondarr.Core.Jobs;

/// <summary>
/// One queued command and its outcome, stored in the <c>command</c> table. The shape mirrors
/// Lidarr's <c>CommandModel</c> (<c>src/NzbDrone.Core/Messaging/Commands/CommandModel.cs</c>) minus
/// the priority and client-notification bookkeeping Wondarr does not have yet.
/// </summary>
public sealed class CommandRecord : EntityBase
{
    /// <summary>Gets or sets the command name, for example <c>Heartbeat</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the raw JSON body the caller sent, or <see langword="null"/>.</summary>
    public string? Body { get; set; }

    /// <summary>Gets or sets where the command is in its life cycle.</summary>
    public CommandStatus Status { get; set; } = CommandStatus.Queued;

    /// <summary>Gets or sets whether the command succeeded.</summary>
    public CommandResult Result { get; set; } = CommandResult.Unknown;

    /// <summary>Gets or sets what queued the command.</summary>
    public CommandTrigger Trigger { get; set; } = CommandTrigger.Unspecified;

    /// <summary>Gets or sets the handler's completion or progress message.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the message of the exception that failed the command.</summary>
    public string? Exception { get; set; }

    /// <summary>Gets or sets the UTC instant the command was queued.</summary>
    public DateTime QueuedAt { get; set; }

    /// <summary>Gets or sets the UTC instant a handler picked the command up.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Gets or sets the UTC instant the command reached a terminal state.</summary>
    public DateTime? EndedAt { get; set; }
}
