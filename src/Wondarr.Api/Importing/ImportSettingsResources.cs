using Wondarr.Core.Importing;

namespace Wondarr.Api.Importing;

/// <summary>The import setting of Settings &gt; Download clients.</summary>
/// <param name="CleanLeftoverFolders">Whether, after an import, Wondarr removes what the download left behind.</param>
/// <param name="CleanLeftoverFoldersLocked">Whether the environment sets it (<c>APP__IMPORT__CLEAN_LEFTOVER_FOLDERS</c>), so it cannot be changed here.</param>
public sealed record ImportSettingsResource(bool CleanLeftoverFolders, bool CleanLeftoverFoldersLocked)
{
    /// <summary>Maps the service's settings onto the resource.</summary>
    /// <param name="settings">The settings to map.</param>
    public static ImportSettingsResource From(ImportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new ImportSettingsResource(settings.CleanLeftoverFolders, settings.CleanLeftoverFoldersLocked);
    }
}

/// <summary>A change to the import settings. An absent field is left alone.</summary>
/// <param name="CleanLeftoverFolders">Whether to remove what a download left behind after an import.</param>
public sealed record ImportSettingsUpdateResource(bool? CleanLeftoverFolders = null);
