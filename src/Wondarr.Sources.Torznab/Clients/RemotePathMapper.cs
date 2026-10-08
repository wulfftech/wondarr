namespace Wondarr.Sources.Torznab.Clients;

/// <summary>One entry of a download client's remote path mappings (DECISIONS build session 8 #8).</summary>
/// <param name="Remote">The path as the download client reports it.</param>
/// <param name="Local">The same path as Wondarr sees it on this host.</param>
public sealed record RemotePathMapping(string Remote, string Local);

/// <summary>
/// Turns a path a download client reported into the path it really is on this host: the longest
/// <see cref="RemotePathMapping.Remote"/> prefix that matches on a path-segment boundary — so
/// <c>/data</c> never matches <c>/database</c> — is replaced by its <c>Local</c>. P7-06 reuses this.
/// </summary>
public static class RemotePathMapper
{
    /// <summary>Maps <paramref name="clientPath"/> through <paramref name="mappings"/>.</summary>
    /// <param name="clientPath">The path the client reported.</param>
    /// <param name="mappings">The client's remote path mappings, in any order.</param>
    /// <returns>The local path, or <paramref name="clientPath"/> unchanged when no mapping covers it.</returns>
    public static string Map(string clientPath, IReadOnlyList<RemotePathMapping> mappings)
    {
        if (string.IsNullOrEmpty(clientPath) || mappings.Count == 0)
        {
            return clientPath;
        }

        // Both sides are compared separator-blind: the client may be on Windows and Wondarr not.
        var normalized = clientPath.Replace('\\', '/');

        RemotePathMapping? best = null;
        var bestLength = -1;
        var bestRemainder = string.Empty;

        foreach (var mapping in mappings)
        {
            var remote = mapping.Remote.Replace('\\', '/').TrimEnd('/');

            if (remote.Length == 0 || !normalized.StartsWith(remote, StringComparison.Ordinal))
            {
                continue;
            }

            // Anything but a separator right after the prefix means the segment continues.
            if (normalized.Length > remote.Length && normalized[remote.Length] != '/')
            {
                continue;
            }

            if (remote.Length <= bestLength)
            {
                continue;
            }

            best = mapping;
            bestLength = remote.Length;
            bestRemainder = normalized.Length > remote.Length ? normalized[(remote.Length + 1)..] : string.Empty;
        }

        if (best is null)
        {
            return clientPath;
        }

        if (bestRemainder.Length == 0)
        {
            return best.Local;
        }

        return Path.Combine(best.Local, bestRemainder.Replace('/', Path.DirectorySeparatorChar));
    }
}
