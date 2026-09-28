using System.Collections;
using Microsoft.Extensions.Configuration;

namespace Wondarr.Core.Configuration;

/// <summary>
/// Configuration source over a dictionary of environment variables, restricted to the
/// <c>APP__</c> prefix so the process environment can be overridden in tests.
/// </summary>
public sealed class AppEnvironmentVariablesConfigurationSource : IConfigurationSource
{
    private const string Prefix = "APP__";

    public AppEnvironmentVariablesConfigurationSource(IDictionary environment) => Environment = environment;

    /// <summary>The environment variables to read.</summary>
    public IDictionary Environment { get; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new AppEnvironmentVariablesConfigurationProvider(Environment);
}

/// <summary>
/// Flattens <c>APP__</c>-prefixed variables into configuration keys:
/// <c>APP__SERVER__URL_BASE</c> → <c>server:urlbase</c>.
/// </summary>
public sealed class AppEnvironmentVariablesConfigurationProvider : ConfigurationProvider
{
    private const string Prefix = "APP__";

    private readonly IDictionary _environment;

    public AppEnvironmentVariablesConfigurationProvider(IDictionary environment) => _environment = environment;

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in _environment)
        {
            if (entry.Key is not string rawKey || !rawKey.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = rawKey[Prefix.Length..].Replace("__", ":", StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);

            if (key.Length > 0)
            {
                data[key] = entry.Value as string;
            }
        }

        Data = data;
    }
}
