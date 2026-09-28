namespace Wondarr.Core.Jobs;

/// <summary>
/// Whether a finished command succeeded. Names follow Lidarr
/// (<c>src/NzbDrone.Core/Messaging/Commands/CommandResult.cs</c>).
/// </summary>
public enum CommandResult
{
    /// <summary>The command has not finished, so there is nothing to report.</summary>
    Unknown = 0,

    /// <summary>The command finished and did what it was asked to.</summary>
    Successful = 1,

    /// <summary>The command finished without doing what it was asked to.</summary>
    Unsuccessful = 2,
}
