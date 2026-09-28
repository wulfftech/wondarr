namespace Compilarr.Core.Jobs;

/// <summary>
/// What put a command in the queue. Names follow Lidarr
/// (<c>src/NzbDrone.Core/Messaging/Commands/CommandTrigger.cs</c>).
/// </summary>
public enum CommandTrigger
{
    /// <summary>The caller did not say.</summary>
    Unspecified = 0,

    /// <summary>A person asked for it (the API, the UI).</summary>
    Manual = 1,

    /// <summary>A schedule asked for it.</summary>
    Scheduled = 2,
}
