using Wondarr.Core.Domain;

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
        var rootEndedWithSeparator = root.Length != rawRoot.Length;

        if (root.Length == 0)
        {
            // A root of "/" (or "\") stays as it is; trimming it to nothing would match everything.
            root = rawRoot;
        }

        if (root.Length == 0 || !localPath.StartsWith(root, StringComparison.Ordinal))
        {
            return localPath;
        }

        var remainder = localPath[root.Length..];

        if (remainder.Length == 0)
        {
            return plexRoot;
        }

        // "…/music2" starts with "…/music" but is not inside it.
        if (!rootEndedWithSeparator && remainder[0] != '/' && remainder[0] != '\\')
        {
            return localPath;
        }

        // The relative part keeps the separators Wondarr wrote it with; the server only understands
        // its own, so a Windows Plex root gets a Windows path throughout.
        var relative = remainder.TrimStart('/', '\\').Replace('/', separator).Replace('\\', separator);

        if (relative.Length == 0)
        {
            return plexRoot;
        }

        // A bare drive root ("D:\") already ends with the separator.
        return plexRoot.EndsWith(separator) ? plexRoot + relative : plexRoot + separator + relative;
    }

    /// <summary>
    /// The separator to join with: the Plex root's own. A drive letter or a backslash anywhere means
    /// the server is on Windows, whatever Wondarr itself runs on.
    /// </summary>
    private static char SeparatorOf(string plexRoot) =>
        plexRoot.Contains('\\') || (plexRoot.Length >= 2 && plexRoot[1] == ':') ? '\\' : '/';

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
