using Wondarr.Core.Importing;
using Wondarr.Core.Organizer;

namespace Wondarr.Sources.Torznab.Clients;

/// <summary>
/// The gate in front of deleting a finished usenet job's folder (<c>import.clean_leftover_folders</c>).
/// SABnzbd never deletes the folder of a job that completed, so what the post left beside the wanted
/// file — the <c>.nfo</c>, the cover, a whole album's other tracks — stays unless Wondarr removes it.
/// The folder is the client's to name and the file system is Wondarr's to protect, so the client's word
/// is not enough: the folder has to be below the folder the client finishes the category in, and never
/// that folder itself.
/// </summary>
public static class UsenetJobFolder
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>
    /// Tells why <paramref name="folder"/> may not be deleted, or <see langword="null"/> when it may. A
    /// folder that is not there is not refused: nothing is left to clean.
    /// </summary>
    /// <param name="folder">The job's folder as the client reports it, mapped to Wondarr's view.</param>
    /// <param name="completedFolder">The folder the client finishes the category in, mapped the same way.</param>
    /// <param name="stagingDir">The grab's own staging folder, which must stay out of the way of the delete.</param>
    /// <param name="keptFile">The wanted file already moved out of the job (staged), which must survive; null when the item has none.</param>
    public static string? Refusal(string folder, string? completedFolder, string stagingDir, string? keptFile)
    {
        if (string.IsNullOrWhiteSpace(completedFolder))
        {
            return "the download client does not say where it finishes this category's jobs";
        }

        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder) || !Path.IsPathRooted(completedFolder))
        {
            return "it is not an absolute path";
        }

        // ".." is only visible before normalisation: Path.GetFullPath would resolve it away and hide it.
        if (folder.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Contains(".."))
        {
            return "the path has a '..' in it";
        }

        try
        {
            if (IsFilesystemRoot(folder))
            {
                return "it is a filesystem root";
            }

            // Everything is below a root, so a root vouches for nothing.
            if (IsFilesystemRoot(completedFolder))
            {
                return $"the completed folder {completedFolder} is a filesystem root";
            }

            if (PathRules.AreEqual(completedFolder, folder))
            {
                return "it is the completed folder itself";
            }

            if (!PathRules.IsInside(completedFolder, folder))
            {
                return $"it is outside the completed folder {completedFolder}";
            }

            // The wanted file and its staging folder must not be in the way of the delete.
            if (PathRules.IsInside(folder, stagingDir))
            {
                return "it holds the grab's staging folder";
            }

            if (keptFile is not null && PathRules.IsInside(folder, keptFile))
            {
                return "it holds the wanted file";
            }
        }
        catch (ArgumentException)
        {
            return "it is not a valid path";
        }
        catch (NotSupportedException)
        {
            return "it is not a valid path";
        }

        // Last: a link in the folder, or on the way down from the completed folder, is not followed.
        return LeftoverFolder.Refusal(folder, completedFolder);
    }

    private static bool IsFilesystemRoot(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        return string.Equals(full, Path.GetPathRoot(full), PathRules.Comparison);
    }
}
