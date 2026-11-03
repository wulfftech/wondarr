using Wondarr.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// Applies the defaults that depend on whether the user wrote the setting at all, which the options
/// binder cannot express.
/// </summary>
/// <remarks>
/// <para>
/// The binder adds the items it finds to the list the property already holds, so a non-empty default
/// on <see cref="SoulseekOptions.SharedFolders"/> would merge with — rather than be replaced by —
/// the value in <c>config.yml</c>. The property therefore starts empty and the default is applied
/// here, once, only when configuration says nothing about the key.
/// </para>
/// <para>
/// "Says nothing" is not the same as "is empty": an empty YAML sequence flattens to no key at all,
/// so <see cref="ConfigFileWriter"/> writes a marker (<c>shared_folders_set: true</c>) beside the
/// empty list and this class treats it as the user having chosen to share nothing.
/// </para>
/// </remarks>
public sealed class SoulseekOptionsPostConfigure : IPostConfigureOptions<SoulseekOptions>
{
    /// <summary>The marker <see cref="ConfigFileWriter"/> writes beside an explicitly empty list.</summary>
    private const string SharedFoldersMarker = "Soulseek:SharedFoldersSet";

    private readonly IConfiguration _configuration;

    /// <summary>The seeded default library's root: what "share my library" shares out of the box.</summary>
    private readonly string _defaultSharedFolder;

    /// <summary>Initialises a new instance of the <see cref="SoulseekOptionsPostConfigure"/> class.</summary>
    /// <param name="configuration">The configuration the options were bound from.</param>
    /// <param name="paths">Where the media root is; without it (a test host) the share is <c>/data/music</c>.</param>
    public SoulseekOptionsPostConfigure(IConfiguration configuration, WondarrPaths? paths = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _configuration = configuration;
        _defaultSharedFolder = paths?.DefaultLibraryRoot ?? $"{WondarrPaths.DefaultMediaRoot}/music";
    }

    /// <inheritdoc />
    public void PostConfigure(string? name, SoulseekOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_configuration.GetSection("Soulseek:SharedFolders").Exists()
            || _configuration[SharedFoldersMarker] is not null)
        {
            return;
        }

        options.SharedFolders = [_defaultSharedFolder];
    }
}
