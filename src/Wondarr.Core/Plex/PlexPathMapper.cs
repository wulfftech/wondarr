using Wondarr.Core.Domain;
using Wondarr.Core.Organizer;

namespace Wondarr.Core.Plex;

/// <summary>
/// Translates a path in Wondarr's file system into the path the Plex Media Server sees. The two
/// differ whenever Plex runs somewhere else — another container, with the library mounted under a
/// different folder — and a refresh with Wondarr's own path silently scans nothing.
/// </summary>
public static class PlexPathMapper
{
    /// <summary>
    /// Maps a library path for the Plex server. Without a configured Plex root, or for a path outside
    /// the library's own root, the path is returned unchanged: guessing would be worse than passing
    /// it through, and the refresh is then logged against the path Plex was actually given.
    /// </summary>
    /// <param name="library">The library the path belongs to.</param>
    /// <param name="localPath">The path as Wondarr sees it.</param>
    /// <returns>The path as the Plex server should see it.</returns>
    public static string ToServerPath(Library library, string localPath)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        var configured = library.PlexLibraryPath;

        if (string.IsNullOrWhiteSpace(configured))
        {
            return localPath;
        }

        var plexRoot = WithoutTrailingSeparators(configured.Trim());
        var separator = SeparatorOf(plexRoot);

        var rawRoot = library.RootPath ?? string.Empty;
        var root = rawRoot.TrimEnd('/', '\\');

        if (root.Length == 0 && rawRoot.Length == 0)
        {
            // No root at all: nothing says which paths this library owns.
            return localPath;
        }

        string remainder;

        if (root.Length == 0)
        {
            // A root that is nothing but a separator ("/", "\") is the whole file system: the
            // separator belongs to the root itself, so it has already been consumed.
            if (localPath.Length == 0 || localPath[0] != rawRoot[0])
            {
                return localPath;
            }

            remainder = localPath[1..];
        }
        else if (localPath.StartsWith(root, StringComparison.Ordinal))
        {
            remainder = localPath[root.Length..];
        }
        else
        {
            return localPath;
        }

        if (remainder.Length == 0)
        {
            return plexRoot;
        }

        // The root has to end at a separator, whether or not it was written with one: "…/music2"
        // merely starts with "…/music" and is a different folder.
        if (root.Length > 0 && remainder[0] != '/' && remainder[0] != '\\')
        {
            return localPath;
        }

        var relative = TranslateSeparators(remainder.TrimStart('/', '\\'), rawRoot, separator);

        if (relative.Length == 0)
        {
            return plexRoot;
        }

        // A bare drive root ("D:\") already ends with the separator.
        return plexRoot.EndsWith(separator) ? plexRoot + relative : plexRoot + separator + relative;
    }

    /// <summary>
    /// The reverse of <see cref="ToServerPath"/>: maps a folder the Plex server reports back to the
    /// path Wondarr sees it at.
    /// </summary>
    /// <param name="library">The library whose Plex root the path may sit under.</param>
    /// <param name="serverPath">The path as the Plex server reports it.</param>
    /// <returns>
    /// The path in Wondarr's file system, or <see langword="null"/> when the path is not under the
    /// library's Plex root (or, without a Plex root, not under the library's own root).
    /// </returns>
    public static string? ToLocalPath(Library library, string serverPath)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverPath);

        var configured = library.PlexLibraryPath;

        if (string.IsNullOrWhiteSpace(configured))
        {
            // Without a Plex root the two sides share their paths.
            return !string.IsNullOrEmpty(library.RootPath) && PathRules.IsInside(library.RootPath, serverPath)
                ? serverPath
                : null;
        }

        var plexRoot = WithoutTrailingSeparators(configured.Trim());
        var normalized = serverPath.Trim();
        var trimmed = normalized.TrimEnd('/', '\\');

        if (trimmed.Length == 0)
        {
            trimmed = normalized;
        }

        // Windows paths are case-insensitive, and so are the ones a Windows Plex server reports.
        var comparison = IsWindowsPath(plexRoot) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!trimmed.StartsWith(plexRoot, comparison))
        {
            return null;
        }

        var remainder = trimmed[plexRoot.Length..];

        if (remainder.Length == 0)
        {
            return library.RootPath;
        }

        // "…/music2" merely starts with "…/music" and is a different folder.
        if (!plexRoot.EndsWith('/') && !plexRoot.EndsWith('\\') && remainder[0] != '/' && remainder[0] != '\\')
        {
            return null;
        }

        var relative = remainder.TrimStart('/', '\\');

        if (relative.Length == 0)
        {
            return library.RootPath;
        }

        // Only a Windows Plex server writes its folders with backslashes; on any other a backslash is
        // part of a name.
        var slashed = IsWindowsPath(plexRoot) ? relative.Replace('\\', '/') : relative;

        return Path.Combine(library.RootPath ?? string.Empty, slashed.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Rewrites the relative part in the separators the server uses, and only those it really uses
    /// the other way round: a Linux file name may legally contain a backslash, and turning it into a
    /// separator would ask the server to scan a folder that does not exist.
    /// </summary>
    /// <param name="relative">The path below the root, with the root's separator already trimmed.</param>
    /// <param name="localRoot">The library root as Wondarr sees it, which says how it is written.</param>
    /// <param name="separator">The separator the Plex root is written with.</param>
    private static string TranslateSeparators(string relative, string localRoot, char separator)
    {
        if (separator == '\\')
        {
            // A Windows Plex root gets a Windows path throughout.
            return relative.Replace('/', '\\');
        }

        return IsWindowsPath(localRoot) ? relative.Replace('\\', '/') : relative;
    }

    /// <summary>
    /// The separator to join with: the Plex root's own. A drive letter or a backslash anywhere means
    /// the server is on Windows, whatever Wondarr itself runs on.
    /// </summary>
    private static char SeparatorOf(string plexRoot) => IsWindowsPath(plexRoot) ? '\\' : '/';

    /// <summary>Whether a path is written the Windows way: a drive letter, or the start of a UNC path.</summary>
    private static bool IsWindowsPath(string path) =>
        path.Contains('\\') || (path.Length >= 2 && path[1] == ':');

    /// <summary>Trims trailing separators, keeping a bare root such as <c>/</c> or <c>D:\</c> whole.</summary>
    private static string WithoutTrailingSeparators(string plexRoot)
    {
        var trimmed = plexRoot.TrimEnd('/', '\\');

        if (trimmed.Length == 0)
        {
            return plexRoot.Length == 0 ? plexRoot : plexRoot[..1];
        }

        // "D:" is not a folder; the separator has to stay.
        return trimmed.EndsWith(':') ? trimmed + SeparatorOf(trimmed) : trimmed;
    }
}
