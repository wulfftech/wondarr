using System.Collections.Concurrent;

namespace Wondarr.Core.Discover;

/// <summary>
/// Remembers which warnings this process has already logged, so a source that fails on every request
/// (a rejected algorithm name, say) says so once instead of on every page view.
/// </summary>
public sealed class RecommendationWarnings
{
    private readonly ConcurrentDictionary<string, bool> _seen = new(StringComparer.Ordinal);

    /// <summary>Returns <see langword="true"/> the first time a key is asked for, and <see langword="false"/> after.</summary>
    /// <param name="key">What the warning is about.</param>
    public bool FirstTime(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return _seen.TryAdd(key, true);
    }
}
