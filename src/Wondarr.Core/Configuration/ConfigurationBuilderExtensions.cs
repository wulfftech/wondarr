using System.Collections;
using Microsoft.Extensions.Configuration;

namespace Wondarr.Core.Configuration;

/// <summary>
/// Adds Wondarr's configuration sources to a builder.
/// </summary>
public static class ConfigurationBuilderExtensions
{
    /// <summary>
    /// Adds the defaults that follow the media root, then <c>config.yml</c>, then the <c>APP__</c>
    /// environment variables, so the environment overrides the file and both override the defaults.
    /// </summary>
    public static IConfigurationBuilder AddWondarrConfiguration(
        this IConfigurationBuilder builder,
        WondarrPaths paths,
        IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);

        // Below the file, and not APP__ variables: a moved default stays editable in the web UI.
        var mediaRootDefaults = paths.MediaRootDefaults();
        if (mediaRootDefaults.Count > 0)
        {
            builder.AddInMemoryCollection(mediaRootDefaults);
        }

        builder.Add(new YamlConfigurationSource(paths.ConfigFile));
        builder.Add(new AppEnvironmentVariablesConfigurationSource(environment));

        return builder;
    }
}
