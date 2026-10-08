using System.Text.RegularExpressions;
using Wondarr.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Organizer;

/// <summary>
/// The <c>import</c> section of <c>config.yml</c>: where replaced files go, whether Wondarr owns
/// the permissions of what it places, and the paths a download client reports differently from us.
/// </summary>
public sealed class ImportOptions
{
    /// <summary>
    /// Where replaced (upgraded) files are parked instead of deleted. Null means
    /// <c>&lt;config dir&gt;/recycle</c>; see <see cref="ResolveRecycleBinPath"/>.
    /// </summary>
    public string? RecycleBinPath { get; set; }

    /// <summary>How long a recycled file is kept, in days. Zero keeps them forever.</summary>
    public int RecycleBinCleanupDays { get; set; } = 7;

    /// <summary>Whether Wondarr sets the Unix mode of the files and folders it places.</summary>
    public bool SetPermissions { get; set; }

    /// <summary>The Unix mode applied to placed files, in octal, when <see cref="SetPermissions"/> is set.</summary>
    public string FileMode { get; set; } = "0664";

    /// <summary>The Unix mode applied to folders Wondarr creates, in octal.</summary>
    public string FolderMode { get; set; } = "0775";

    /// <summary>Download-client path prefixes and the local paths they stand for.</summary>
    public List<RemotePathMapping> RemotePathMappings { get; set; } = [];

    /// <summary>
    /// Where a song's file from a torrent or a usenet post is staged before the import: a torrent's
    /// file is hard-linked here (copied when the link fails — another filesystem), a usenet file is
    /// moved here. Keep it on the same filesystem as the torrent client's downloads so the links
    /// work (DECISIONS build session 8 #5).
    /// </summary>
    public string ContainerStagingPath { get; set; } = "/data/downloads/containers";

    /// <summary>
    /// The recycle bin directory actually used: the configured one, or <c>recycle</c> inside the
    /// configuration directory. Always absolute.
    /// </summary>
    public string ResolveRecycleBinPath(WondarrPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // Without a trailing separator: every path built from this root is compared and walked as
        // "inside the bin", and "recycle/" and "recycle" have to read as one place.
        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(
                string.IsNullOrWhiteSpace(RecycleBinPath)
                    ? Path.Combine(paths.ConfigDir, "recycle")
                    : RecycleBinPath));
    }

    /// <summary>Parses an octal mode string such as <c>0664</c> into a <see cref="UnixFileMode"/>.</summary>
    public static UnixFileMode ParseMode(string value) => (UnixFileMode)Convert.ToInt32(value, 8);
}

/// <summary>
/// One download client's view of the disk: paths it reports under <see cref="RemotePath"/> are
/// really under <see cref="LocalPath"/> on the host Wondarr runs on.
/// </summary>
public sealed class RemotePathMapping
{
    /// <summary>The download client's host name, as configured in that client.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The path prefix as the download client reports it.</summary>
    public string RemotePath { get; set; } = string.Empty;

    /// <summary>The local path that prefix stands for.</summary>
    public string LocalPath { get; set; } = string.Empty;
}

/// <summary>
/// Validates <see cref="ImportOptions"/>. Every failure message starts with the YAML key so the
/// user can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed partial class ImportOptionsValidator : IValidateOptions<ImportOptions>
{
    /// <summary>
    /// Directories the recycle bin may never be: the container's data and configuration roots, in
    /// case <see cref="WondarrPaths.ConfigDir"/> was resolved somewhere else.
    /// </summary>
    private static readonly string[] ContainerDirectories = ["/data", "/config"];

    private readonly WondarrPaths? _paths;

    /// <summary>Creates the validator with the paths in use, when they are known.</summary>
    public ImportOptionsValidator(WondarrPaths? paths = null) => _paths = paths;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        CheckMode("import.file_mode", options.FileMode, failures);
        CheckMode("import.folder_mode", options.FolderMode, failures);

        if (options.RecycleBinCleanupDays is < 0 or > 365)
        {
            failures.Add(
                $"import.recycle_bin_cleanup_days: must be between 0 and 365 days (was '{options.RecycleBinCleanupDays}')");
        }

        // A bin at the root of a volume would make the cleanup pass walk and delete the whole disk.
        if (!string.IsNullOrWhiteSpace(options.RecycleBinPath) && IsFilesystemRoot(options.RecycleBinPath))
        {
            failures.Add(
                $"import.recycle_bin_path: must not be a filesystem root (was '{options.RecycleBinPath}')");
        }

        // And one at the data or configuration root would park recycled files among the downloads,
        // the database and the settings — the places Wondarr must never tidy up on its own.
        if (!string.IsNullOrWhiteSpace(options.RecycleBinPath)
            && IsProtectedDirectory(options.RecycleBinPath, _paths))
        {
            failures.Add(
                $"import.recycle_bin_path: must not be the data or configuration directory (was '{options.RecycleBinPath}')");
        }

        if (string.IsNullOrWhiteSpace(options.ContainerStagingPath) || IsFilesystemRoot(options.ContainerStagingPath))
        {
            failures.Add(
                $"import.container_staging_path: must be a folder, not empty or a filesystem root (was '{options.ContainerStagingPath}')");
        }

        for (var index = 0; index < options.RemotePathMappings.Count; index++)
        {
            var mapping = options.RemotePathMappings[index];

            if (string.IsNullOrWhiteSpace(mapping.RemotePath))
            {
                failures.Add(
                    $"import.remote_path_mappings[{index}].remote_path: must not be empty");
            }

            if (string.IsNullOrWhiteSpace(mapping.LocalPath))
            {
                failures.Add(
                    $"import.remote_path_mappings[{index}].local_path: must not be empty");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Whether the configured bin is the root of a volume, such as <c>/</c> or <c>C:\</c>.</summary>
    private static bool IsFilesystemRoot(string path)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

            return string.Equals(full, Path.GetPathRoot(full), PathRules.Comparison);
        }
        catch (ArgumentException)
        {
            // Not a path at all. The user finds that out when the bin is first used.
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Whether the configured bin is a directory Wondarr keeps its own files in.</summary>
    private static bool IsProtectedDirectory(string path, WondarrPaths? paths)
    {
        var candidates = new List<string>(ContainerDirectories);

        if (!string.IsNullOrWhiteSpace(paths?.ConfigDir))
        {
            candidates.Add(paths!.ConfigDir);
        }

        return candidates.Any(candidate => PathsAreEqual(path, candidate));
    }

    /// <summary>
    /// Whether two configured paths name the same directory. Paths that are not paths at all (a
    /// Windows-style value in a Linux container, say) simply do not match.
    /// </summary>
    private static bool PathsAreEqual(string first, string second)
    {
        try
        {
            return PathRules.AreEqual(first, second);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static void CheckMode(string key, string? value, List<string> failures)
    {
        if (value is null || !OctalMode().IsMatch(value))
        {
            failures.Add($"{key}: must be three or four octal digits (was '{value}')");
        }
    }

    // Three digits, or four with a leading zero: the forms chmod accepts for a plain mode.
    [GeneratedRegex("^0?[0-7]{3}$")]
    private static partial Regex OctalMode();
}
