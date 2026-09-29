using System.Collections;

namespace Wondarr.Core.Configuration;

/// <summary>
/// Reads which settings the environment has taken over. The environment wins over
/// <c>config.yml</c> (the <c>APP__</c> source is added last), so a field set there is reported to the
/// settings UI as read-only instead of being silently ignored when the user changes it.
/// </summary>
public static class EnvironmentOverrides
{
    private const string Prefix = "APP__";

    /// <summary>
    /// The keys under <c>APP__&lt;SECTION&gt;__</c> that the environment sets, normalised the way
    /// the binder sees them: <c>__</c> becomes <c>:</c>, underscores are dropped and the result is
    /// lower-cased, so <c>APP__SOULSEEK__USERNAME</c> yields <c>username</c> and
    /// <c>APP__SOULSEEK__LISTEN_PORT</c> yields <c>listenport</c>. A nested key also reports the
    /// field it hangs off, so an index variable such as
    /// <c>APP__SOULSEEK__SHARED_FOLDERS__0</c> yields <c>sharedfolders:0</c> <em>and</em>
    /// <c>sharedfolders</c>.
    /// </summary>
    /// <param name="environment">The process environment, or a test's substitute for it.</param>
    /// <param name="section">Section name, for example <c>soulseek</c>.</param>
    public static IReadOnlySet<string> SectionKeys(IDictionary environment, string section)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(section);

        var prefix = $"{Prefix}{section}__";
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is not string rawKey || !rawKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = rawKey[prefix.Length..]
                .Replace("__", ":", StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();

            if (key.Length == 0)
            {
                continue;
            }

            keys.Add(key);

            // A list variable is written APP__SOULSEEK__SHARED_FOLDERS__0: the binder sees the item
            // as the nested key sharedfolders:0, while the field the UI has to lock is the head of
            // that path, so both spellings are reported.
            var separator = key.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                keys.Add(key[..separator]);
            }
        }

        return keys;
    }
}
