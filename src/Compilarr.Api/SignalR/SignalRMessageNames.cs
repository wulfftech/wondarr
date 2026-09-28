namespace Compilarr.Api.SignalR;

/// <summary>
/// The <c>name</c> of every message the events hub sends. The UI switches on these, so they are
/// constants rather than literals; <c>queue</c> is reserved for the queue broadcasts of a later
/// phase and nothing emits it yet.
/// </summary>
public static class SignalRMessageNames
{
    /// <summary>A command changed state; the resource is an <c>CommandResource</c>.</summary>
    public const string Command = "command";

    /// <summary>Health results were recomputed; the resource is the health resource array.</summary>
    public const string Health = "health";

    /// <summary>Reserved for queue broadcasts; nothing emits this yet.</summary>
    public const string Queue = "queue";
}
