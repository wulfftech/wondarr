namespace Wondarr.Core.Organizer;

/// <summary>
/// Applies <c>import.set_permissions</c>'s file and folder modes to what Wondarr placed. Shared by the audio placer
/// and the music video importer, so both read the setting the same way.
/// </summary>
public static class PlacementPermissions
{
    /// <summary>Sets the configured file mode on <paramref name="files"/> and the folder mode on <paramref name="directories"/>; does nothing while the setting is off.</summary>
    /// <param name="disk">The disk seam.</param>
    /// <param name="options">The import settings.</param>
    /// <param name="files">The files Wondarr placed.</param>
    /// <param name="directories">The folders Wondarr created.</param>
    public static void Apply(IDiskOperations disk, ImportOptions options, IEnumerable<string> files, IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(directories);

        if (!options.SetPermissions)
        {
            return;
        }

        var fileMode = ImportOptions.ParseMode(options.FileMode);
        var folderMode = ImportOptions.ParseMode(options.FolderMode);

        foreach (var file in files)
        {
            disk.SetUnixFileMode(file, fileMode);
        }

        foreach (var directory in directories)
        {
            disk.SetUnixFileMode(directory, folderMode);
        }
    }
}
