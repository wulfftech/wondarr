using System.Collections;
using Microsoft.Extensions.Configuration;

namespace Wondarr.Core.Configuration;

/// <summary>
/// Adds Wondarr's configuration sources to a builder.
/// </summary>
public static class ConfigurationBuilderExtensions
{
    /// <summary>
    /// Adds <c>config.yml</c> and then the <c>APP__</c> environment variables, so the
    /// environment overrides the file.
    /// </summary>
    public static IConfigurationBuilder AddWondarrConfiguration(
        this IConfigurationBuilder builder,
        WondarrPaths paths,
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
