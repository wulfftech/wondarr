using Microsoft.Extensions.Options;

namespace Wondarr.Core.Organizer;

/// <summary>Turns a path a download client reported into the path it really is on this host.</summary>
public interface IRemotePathMapper
{
    /// <summary>
    /// The local path <paramref name="remotePath"/> stands for at <paramref name="host"/>, or the
    /// path unchanged when no mapping covers it.
    /// </summary>
    string MapToLocal(string host, string remotePath);
}

/// <summary>
/// Applies the <c>import.remote_path_mappings</c> list. The first mapping whose host matches and
/// whose remote path is a prefix of the reported path — on a segment boundary, so
/// <c>/down</c> never matches <c>/downloads</c> — wins.
/// </summary>
public sealed class RemotePathMapper : IRemotePathMapper
{
    private readonly IOptionsMonitor<ImportOptions> _options;

    /// <summary>Creates the mapper over the live import options.</summary>
    public RemotePathMapper(IOptionsMonitor<ImportOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <inheritdoc />
    public string MapToLocal(string host, string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath))
        {
            return remotePath;
        }

        // Both sides are compared separator-blind: the client may be on Windows and Wondarr not.
        var normalizedRemote = Normalize(remotePath);

        foreach (var mapping in _options.CurrentValue.RemotePathMappings)
        {
            if (!string.Equals(mapping.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var prefix = Normalize(mapping.RemotePath);

            if (prefix.Length == 0 || !IsPrefix(prefix, normalizedRemote, out var remainder))
            {
                continue;
            }

            if (remainder.Length == 0)
            {
                return mapping.LocalPath;
            }

            return Path.Combine(mapping.LocalPath, remainder.Replace('/', Path.DirectorySeparatorChar));
        }

        return remotePath;
    }

    /// <summary>Slash form, without a trailing separator.</summary>
    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Whether <paramref name="prefix"/> covers <paramref name="path"/> and where the covered part
    /// ends. A prefix that stops mid-segment does not count.
    /// </summary>
    private static bool IsPrefix(string prefix, string path, out string remainder)
    {
        remainder = string.Empty;

        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // Anything but a separator right after the prefix means the segment continues.
        if (path.Length > prefix.Length && path[prefix.Length] != '/')
        {
            return false;
        }

        remainder = path[prefix.Length..].TrimStart('/');
        return true;
    }
}
