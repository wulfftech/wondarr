using System.Collections;
using Microsoft.Extensions.Configuration;

namespace Compilarr.Core.Configuration;

/// <summary>
/// The directories and files Compilarr owns inside its configuration directory.
/// </summary>
public sealed record CompilarrPaths(string ConfigDir)
{
    /// <summary>The user-editable YAML settings file.</summary>
    public string ConfigFile => Path.Combine(ConfigDir, "config.yml");

    /// <summary>The SQLite database.</summary>
    public string DatabaseFile => Path.Combine(ConfigDir, "compilarr.db");

    /// <summary>Log files.</summary>
    public string LogsDir => Path.Combine(ConfigDir, "logs");

    /// <summary>State of the bundled slskd process.</summary>
    public string SlskdDir => Path.Combine(ConfigDir, "slskd");

    /// <summary>Database backups.</summary>
    public string BackupsDir => Path.Combine(ConfigDir, "backups");

    /// <summary>
    /// Resolves the configuration directory: the <c>ConfigDir</c> setting first, then the
    /// <c>COMPILARR_CONFIG_DIR</c> environment variable, then <c>/config</c> on Linux and
    /// <c>%LOCALAPPDATA%/Compilarr</c> elsewhere. Always returns an absolute path.
    /// </summary>
    public static CompilarrPaths Resolve(IConfiguration configuration, IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var configured = configuration["ConfigDir"];
        var resolved = FirstNonEmpty(configured, ReadEnvironmentVariable(environment, "COMPILARR_CONFIG_DIR"));

        resolved ??= OperatingSystem.IsLinux()
            ? "/config"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compilarr");

        return new CompilarrPaths(Path.GetFullPath(resolved));
    }

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? ReadEnvironmentVariable(IDictionary environment, string name)
    {
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string key && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value as string;
            }
        }

        return null;
    }
}
