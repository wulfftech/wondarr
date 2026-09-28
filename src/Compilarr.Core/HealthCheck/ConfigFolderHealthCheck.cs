using Compilarr.Core.Configuration;

namespace Compilarr.Core.HealthCheck;

/// <summary>Checks that Compilarr can write to its configuration directory.</summary>
public sealed class ConfigFolderHealthCheck : FolderHealthCheck
{
    /// <summary>Initialises a new instance of the <see cref="ConfigFolderHealthCheck"/> class.</summary>
    public ConfigFolderHealthCheck(CompilarrPaths paths)
        : base((paths ?? throw new ArgumentNullException(nameof(paths))).ConfigDir)
    {
    }

    /// <inheritdoc />
    public override string Name => nameof(ConfigFolderHealthCheck);
}