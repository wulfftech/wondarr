using System.Collections;
using Microsoft.Extensions.Configuration;

namespace Wondarr.Core.Configuration;

/// <summary>
/// The directories and files Wondarr owns inside its configuration directory, and the media root
/// its default music and download folders hang off.
/// </summary>
public sealed record WondarrPaths(string ConfigDir)
{
    /// <summary>The media root when nothing sets one: the <c>/data</c> volume of the image.</summary>
    public const string DefaultMediaRoot = "/data";

    /// <summary>
    /// The folder the default library root and the default download folders live under. It only
    /// moves defaults: a path set in <c>config.yml</c>, in the environment or in the web UI is used
    /// as it is, and an existing library keeps its root.
    /// </summary>
    public string MediaRoot { get; init; } = DefaultMediaRoot;

    /// <summary>
    /// The default library's root on a new database, and the default Soulseek share:
    /// <c>/data/music</c> unless the media root is moved.
    /// </summary>
    public string DefaultLibraryRoot => MediaPath("music");

    /// <summary>
    /// The defaults that follow the media root, as configuration keys, or none when it is
    /// <see cref="DefaultMediaRoot"/> (the options' own defaults are those paths already).
    /// </summary>
    public IReadOnlyDictionary<string, string?> MediaRootDefaults() =>
        MediaRoot == DefaultMediaRoot
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Soulseek:DownloadsDir"] = MediaPath("downloads", "slskd"),
                ["Soulseek:IncompleteDir"] = MediaPath("downloads", "slskd", "incomplete"),
                ["YouTube:DownloadsDir"] = MediaPath("downloads", "youtube"),
                ["Import:ContainerStagingPath"] = MediaPath("downloads", "containers"),
            };

    /// <summary>The user-editable YAML settings file.</summary>
    public string ConfigFile => Path.Combine(ConfigDir, "config.yml");

    /// <summary>The SQLite database.</summary>
    public string DatabaseFile => Path.Combine(ConfigDir, "wondarr.db");

    /// <summary>Log files.</summary>
    public string LogsDir => Path.Combine(ConfigDir, "logs");

    /// <summary>State of the bundled slskd process.</summary>
    public string SlskdDir => Path.Combine(ConfigDir, "slskd");

    /// <summary>Database backups.</summary>
    public string BackupsDir => Path.Combine(ConfigDir, "backups");

    /// <summary>The database file name before the project was renamed Wondarr (0.0.1-alpha.1).</summary>
    public string LegacyDatabaseFile => Path.Combine(ConfigDir, "compilarr.db");

    /// <summary>
    /// Takes over a database written under the project's former name: when <c>compilarr.db</c> exists
    /// and <c>wondarr.db</c> does not, renames it together with its <c>-wal</c> and <c>-shm</c> files.
    /// Runs before the database is opened. Returns <see langword="true"/> when it moved a database.
    /// </summary>
    public bool AdoptLegacyDatabase()
    {
        if (File.Exists(DatabaseFile) || !File.Exists(LegacyDatabaseFile))
        {
            return false;
        }

        // The WAL and shared-memory files first, so a crash in between leaves a database whose
        // pending writes sit next to it under the new name, never an orphaned WAL.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(LegacyDatabaseFile + suffix))
            {
                File.Move(LegacyDatabaseFile + suffix, DatabaseFile + suffix);
            }
        }

        File.Move(LegacyDatabaseFile, DatabaseFile);
        return true;
    }

    /// <summary>
    /// Resolves the configuration directory: the <c>ConfigDir</c> setting first, then the
    /// <c>WONDARR_CONFIG_DIR</c> environment variable (or the pre-rename <c>COMPILARR_CONFIG_DIR</c>),
    /// then <c>/config</c> on Linux and
    /// <c>%LOCALAPPDATA%/Wondarr</c> elsewhere. Always returns an absolute path. The media root is
    /// the <c>MediaRoot</c> setting, then <c>WONDARR_MEDIA_ROOT</c>, then <see cref="DefaultMediaRoot"/>.
    /// </summary>
    public static WondarrPaths Resolve(IConfiguration configuration, IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var configured = configuration["ConfigDir"];
        var resolved = FirstNonEmpty(
            configured,
            ReadEnvironmentVariable(environment, "WONDARR_CONFIG_DIR"),
            ReadEnvironmentVariable(environment, "COMPILARR_CONFIG_DIR"));

        resolved ??= OperatingSystem.IsLinux()
            ? "/config"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wondarr");

        var mediaRoot = FirstNonEmpty(
            configuration["MediaRoot"],
            ReadEnvironmentVariable(environment, "WONDARR_MEDIA_ROOT"));

        return new WondarrPaths(Path.GetFullPath(resolved))
        {
            MediaRoot = mediaRoot is null
                ? DefaultMediaRoot
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(mediaRoot.Trim())),
        };
    }

    // Joined with '/', like the image's own defaults, so the default root gives exactly /data/music
    // on every platform.
    private string MediaPath(params string[] parts) =>
        string.Join('/', [MediaRoot.TrimEnd('/', '\\'), .. parts]);

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
