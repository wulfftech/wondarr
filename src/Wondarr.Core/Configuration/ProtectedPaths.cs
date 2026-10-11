using Wondarr.Core.Organizer;

namespace Wondarr.Core.Configuration;

/// <summary>
/// Paths no setting may point at: the root of the file system, Wondarr's data directory
/// (<c>/config</c> in Docker; <see cref="WondarrPaths.ConfigDir"/>) and anything inside it. That
/// directory holds <c>config.yml</c> with the API key, the database and the backups, so a shared
/// folder, a download folder or a library there would publish or overwrite them.
/// </summary>
/// <remarks>
/// Paths are compared after normalising: trailing separators removed and <c>..</c> resolved, with
/// the case kept on Linux (<see cref="PathRules.Comparison"/>). Only a value being set is refused;
/// a configuration that already holds one keeps loading, and the health check names it.
/// </remarks>
public static class ProtectedPaths
{
    /// <summary>
    /// Why <paramref name="path"/> is refused, as a clause that completes "it ...", or
    /// <see langword="null"/> when the path is fine or is not a usable path at all (another rule
    /// reports that).
    /// </summary>
    /// <param name="path">The configured path.</param>
    /// <param name="paths">Where Wondarr's data directory is.</param>
    public static string? Reason(string? path, WondarrPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim());
            var trimmed = Path.TrimEndingDirectorySeparator(full);
            var root = Path.GetPathRoot(full);

            if (trimmed.Length == 0 || (root is not null && string.Equals(Path.TrimEndingDirectorySeparator(root), trimmed, PathRules.Comparison)))
            {
                return "is the root of the file system";
            }

            if (PathRules.AreEqual(paths.ConfigDir, full))
            {
                return "is Wondarr's data directory";
            }

            return PathRules.IsInside(paths.ConfigDir, full) ? "is inside Wondarr's data directory" : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The validation message for a setting that points somewhere it must not, starting with the
    /// setting's YAML key, or <see langword="null"/> when the path is fine.
    /// </summary>
    /// <param name="key">The YAML key, for example <c>soulseek.downloads_dir</c>.</param>
    /// <param name="path">The configured path.</param>
    /// <param name="paths">Where Wondarr's data directory is.</param>
    /// <param name="sharing">Whether the path is shared with other people, which changes what is at stake.</param>
    public static string? Failure(string key, string? path, WondarrPaths paths, bool sharing = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (Reason(path, paths) is not { } reason)
        {
            return null;
        }

        var shown = path!.Trim();

        return sharing
            ? $"{key}: sharing {shown} would publish Wondarr's API key and passwords (it {reason})"
            : $"{key}: {shown} is not allowed: it {reason}, where Wondarr keeps its API key, passwords and database";
    }
}
