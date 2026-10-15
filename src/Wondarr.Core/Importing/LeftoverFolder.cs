using Wondarr.Core.Organizer;

namespace Wondarr.Core.Importing;

/// <summary>
/// Deleting a folder together with whatever is left in it (<c>import.clean_leftover_folders</c>). The
/// callers decide <em>which</em> folder is theirs to delete; this is the last gate in front of the
/// recursive delete: it refuses a folder that is a filesystem root, one with a <c>..</c> in its path,
/// and one that is, or holds, a symlink, junction or other reparse point — deleting through a link
/// would leave the real files behind or take a folder somewhere else with it.
/// </summary>
public static class LeftoverFolder
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>
    /// Tells why <paramref name="folder"/> may not be deleted with its contents, or <see langword="null"/>
    /// when it may. A folder that does not exist is not refused: there is nothing to delete.
    /// </summary>
    /// <param name="folder">The folder to delete.</param>
    /// <param name="stopAbove">
    /// When given, every folder from <paramref name="folder"/> up to (not including) this one is checked
    /// for being a link too, so a link on the way cannot lead the delete elsewhere. It has to be an
    /// ancestor of <paramref name="folder"/>.
    /// </param>
    public static string? Refusal(string folder, string? stopAbove = null)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return "there is no folder";
        }

        // ".." is only visible before normalisation: Path.GetFullPath would resolve it away and hide it.
        if (folder.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Contains(".."))
        {
            return "the path has a '..' in it";
        }

        if (!Path.IsPathRooted(folder))
        {
            return "it is not an absolute path";
        }

        string full;

        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (ArgumentException)
        {
            return "it is not a valid path";
        }
        catch (NotSupportedException)
        {
            return "it is not a valid path";
        }

        if (string.Equals(full, Path.GetPathRoot(full), PathRules.Comparison))
        {
            return "it is a filesystem root";
        }

        if (!Directory.Exists(full))
        {
            return null;
        }

        foreach (var level in Levels(full, stopAbove))
        {
            if (IsReparsePoint(level))
            {
                return $"{level} is a link";
            }
        }

        return LinkBelow(full);
    }

    /// <summary>
    /// Deletes <paramref name="folder"/> and everything in it. The caller has asked <see cref="Refusal"/>;
    /// a folder that is already gone is not an error.
    /// </summary>
    /// <param name="folder">The folder to delete.</param>
    public static void Delete(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The folder itself and, when a stop is given, each parent up to but not including it.</summary>
    private static IEnumerable<string> Levels(string folder, string? stopAbove)
    {
        yield return folder;

        if (stopAbove is null)
        {
            yield break;
        }

        var current = Path.GetDirectoryName(folder);

        while (!string.IsNullOrEmpty(current) && !PathRules.AreEqual(current, stopAbove))
        {
            yield return current;

            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>Says which link is under <paramref name="folder"/>, if any; never steps into one.</summary>
    private static string? LinkBelow(string folder)
    {
        var pending = new Stack<string>();
        pending.Push(folder);

        // Not recursive on purpose: a link is reported the moment it is listed and never entered.
        var listing = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };

        try
        {
            while (pending.Count > 0)
            {
                var directory = pending.Pop();

                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", listing))
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return $"{entry.FullName} inside it is a link";
                    }

                    if (entry is DirectoryInfo)
                    {
                        pending.Push(entry.FullName);
                    }
                }
            }
        }
        catch (IOException)
        {
            return "it cannot be read";
        }
        catch (UnauthorizedAccessException)
        {
            return "it cannot be read";
        }

        return null;
    }

    /// <summary>Whether a path is a symlink, a junction or another reparse point; unreadable counts as one.</summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
