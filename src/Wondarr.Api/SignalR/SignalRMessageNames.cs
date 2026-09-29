namespace Wondarr.Api.SignalR;

/// <summary>
/// The <c>name</c> of every message the events hub sends. The UI switches on these, so they are
/// constants rather than literals.
/// </summary>
public static class SignalRMessageNames
{
    /// <summary>A command changed state; the resource is an <c>CommandResource</c>.</summary>
    public const string Command = "command";

    /// <summary>Health results were recomputed; the resource is the health resource array.</summary>
    public const string Health = "health";

    /// <summary>A queue item changed; the resource is a queue item projection.</summary>
    public const string Queue = "queue";

    /// <summary>A song changed; the resource names it.</summary>
    public const string Song = "song";
}
