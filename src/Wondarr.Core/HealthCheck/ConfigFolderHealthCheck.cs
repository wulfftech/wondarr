using Wondarr.Core.Configuration;

namespace Wondarr.Core.HealthCheck;

/// <summary>Checks that Wondarr can write to its configuration directory.</summary>
public sealed class ConfigFolderHealthCheck : FolderHealthCheck
{
    /// <summary>Initialises a new instance of the <see cref="ConfigFolderHealthCheck"/> class.</summary>
    public ConfigFolderHealthCheck(WondarrPaths paths)
        : base((paths ?? throw new ArgumentNullException(nameof(paths))).ConfigDir)
    {
    }

    /// <inheritdoc />
    public override string Name => nameof(ConfigFolderHealthCheck);
}
