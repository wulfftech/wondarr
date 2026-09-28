using System.Collections;
using Microsoft.Extensions.Configuration;

namespace Compilarr.Core.Configuration;

/// <summary>
/// Adds Compilarr's configuration sources to a builder.
/// </summary>
public static class ConfigurationBuilderExtensions
{
    /// <summary>
    /// Adds <c>config.yml</c> and then the <c>APP__</c> environment variables, so the
    /// environment overrides the file.
    /// </summary>
    public static IConfigurationBuilder AddCompilarrConfiguration(
        this IConfigurationBuilder builder,
        CompilarrPaths paths,
        IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);

        builder.Add(new YamlConfigurationSource(paths.ConfigFile));
        builder.Add(new AppEnvironmentVariablesConfigurationSource(environment));

        return builder;
    }
}