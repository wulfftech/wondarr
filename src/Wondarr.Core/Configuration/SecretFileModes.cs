using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Configuration;

/// <summary>
/// The start-up pass that makes every file holding secrets owner-only on installs that created
/// them before Wondarr did so itself, and prepares the database file so SQLite's own files follow.
/// </summary>
public static partial class SecretFileModes
{
    /// <summary>
    /// Creates the database file empty and owner-only when it is missing. Must run before EF first
    /// opens the database: SQLite creates <c>-wal</c> and <c>-shm</c> with the main file's mode.
    /// Does nothing on Windows.
    /// </summary>
    /// <param name="databaseFile">The SQLite database file.</param>
    public static void PrepareDatabase(string databaseFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFile);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // A failure here must not stop start-up: the pass in TightenExisting reports what it cannot change.
        try
        {
            var directory = Path.GetDirectoryName(databaseFile);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            OwnerOnlyFile.EnsureExists(databaseFile);

            foreach (var file in new[] { databaseFile, databaseFile + "-wal", databaseFile + "-shm" })
            {
                OwnerOnlyFile.Tighten(file);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Left to the hosted service's second attempt.
        }
    }

    /// <summary>
    /// Sets <c>config.yml</c>, the database and its <c>-wal</c> and <c>-shm</c>, <c>slskd.yml</c> and
    /// every backup zip to <c>0600</c>, and removes the <c>slskd.yml.*.tmp</c> files an interrupted
    /// write left behind. Does nothing on Windows. Logs one line saying how many modes it changed.
    /// </summary>
    /// <param name="paths">The configuration directory.</param>
    /// <param name="logger">Receives the one line.</param>
    /// <returns>How many files had their mode changed.</returns>
    public static int TightenExisting(WondarrPaths paths, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        if (OperatingSystem.IsWindows())
        {
            return 0;
        }

        var changed = 0;

        // Start-up must never fail over a mode: an unreadable folder or a file that vanishes is skipped.
        try
        {
            foreach (var file in SecretFiles(paths))
            {
                if (OwnerOnlyFile.Tighten(file))
                {
                    changed++;
                }
            }

            SweepStaleSlskdTemporaries(paths);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogFailed(logger, exception.Message);
        }

        if (changed > 0)
        {
            LogTightened(logger, changed);
        }

        return changed;
    }

    private static readonly EnumerationOptions Everything = new() { IgnoreInaccessible = true, RecurseSubdirectories = true };

    private static IEnumerable<string> SecretFiles(WondarrPaths paths)
    {
        yield return paths.ConfigFile;
        yield return paths.DatabaseFile;
        yield return paths.DatabaseFile + "-wal";
        yield return paths.DatabaseFile + "-shm";
        yield return Path.Combine(paths.SlskdDir, "slskd.yml");

        // What a restore set aside: wondarr.db*.pre-restore* and config.yml.pre-restore* (three generations).
        if (Directory.Exists(paths.ConfigDir))
        {
            foreach (var pattern in new[] { Path.GetFileName(paths.DatabaseFile) + "*.pre-restore*", Path.GetFileName(paths.ConfigFile) + ".pre-restore*" })
            {
                foreach (var file in Directory.EnumerateFiles(paths.ConfigDir, pattern))
                {
                    yield return file;
                }
            }
        }

        if (!Directory.Exists(paths.BackupsDir))
        {
            yield break;
        }

        foreach (var zip in Directory.EnumerateFiles(paths.BackupsDir, "*.zip", Everything))
        {
            yield return zip;
        }
    }

    /// <summary>
    /// Deletes <c>slskd.yml.&lt;guid&gt;.tmp</c> files: a crash between writing one and moving it over
    /// <c>slskd.yml</c> leaves a copy of the Soulseek password behind.
    /// </summary>
    private static void SweepStaleSlskdTemporaries(WondarrPaths paths)
    {
        if (!Directory.Exists(paths.SlskdDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(paths.SlskdDir, "slskd.yml.*.tmp", new EnumerationOptions { IgnoreInaccessible = true }))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for the next start.
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not make every file holding secrets owner-only: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Made {Count} file(s) holding secrets readable by the app user only")]
    private static partial void LogTightened(ILogger logger, int count);
}
