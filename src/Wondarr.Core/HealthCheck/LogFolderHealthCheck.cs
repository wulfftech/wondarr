using Wondarr.Core.Configuration;

namespace Wondarr.Core.HealthCheck;

/// <summary>Checks that Wondarr can write to its log directory.</summary>
public sealed class LogFolderHealthCheck : FolderHealthCheck
{
    /// <summary>Initialises a new instance of the <see cref="LogFolderHealthCheck"/> class.</summary>
    public LogFolderHealthCheck(WondarrPaths paths)
        : base((paths ?? throw new ArgumentNullException(nameof(paths))).LogsDir)
    {
    }

    /// <inheritdoc />
    public override string Name => nameof(LogFolderHealthCheck);
}
