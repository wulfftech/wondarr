using Wondarr.Core.Configuration;

namespace Wondarr.Core.Backup;

/// <summary>
/// Applies a staged restore — the two files <see cref="BackupService"/> wrote into
/// <c>&lt;ConfigDir&gt;/restore/</c> — before the database is opened and before
/// <c>ConfigFileInitializer</c> runs. The live files are moved aside as <c>*.pre-restore</c> first,
/// so a failure half-way can put everything back and the install is never left without a database
/// and without its pre-restore copy.
/// </summary>
public static class PendingRestore
{
    /// <summary>The folder a staged restore waits in.</summary>
    public const string RestoreFolderName = "restore";

    /// <summary>The suffix the replaced files are kept under.</summary>
    public const string PreRestoreSuffix = ".pre-restore";

    /// <summary>
    /// Applies the staged restore when both staged files exist.
    /// </summary>
    /// <param name="paths">The configuration directory the restore folder lives in.</param>
    /// <param name="message">
    /// What happened: <see langword="null"/> when nothing was staged, otherwise a line for the
    /// start-up log whether the restore was applied or rolled back.
    /// </param>
    /// <returns>Whether a restore was applied.</returns>
    public static bool ApplyIfStaged(WondarrPaths paths, out string? message)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var restoreDirectory = Path.Combine(paths.ConfigDir, RestoreFolderName);
        var stagedDatabase = Path.Combine(restoreDirectory, BackupService.DatabaseEntryName);
        var stagedConfig = Path.Combine(restoreDirectory, BackupService.ConfigEntryName);

        if (!File.Exists(stagedDatabase) || !File.Exists(stagedConfig))
        {
            message = null;
            return false;
        }

        // (the path the file was moved from, the path it now sits at)
        var setAside = new List<(string From, string To)>();
        var installed = new List<(string From, string To)>();

        try
        {
            // The live files first, so the staged ones can take their names. The WAL and
            // shared-memory files move with the database, exactly as AdoptLegacyDatabase moves them.
            foreach (var live in LiveFiles(paths))
            {
                if (File.Exists(live))
                {
                    SetAside(live, setAside);
                }
            }

            Install(stagedDatabase, paths.DatabaseFile, installed);
            Install(stagedConfig, paths.ConfigFile, installed);

            // Everything is in place: the staged copy is the install's database now, so the
            // folder it came from can go.
            Directory.Delete(restoreDirectory, recursive: true);
        }
        catch (Exception exception)
        {
            RollBack(installed, setAside);

            // The restore folder is left exactly as it was, so the user can inspect what failed.
            message = $"a staged restore could not be applied and was rolled back: {exception.Message}";
            return false;
        }

        message = "restored the database and config.yml from a staged backup (the previous files are kept as *.pre-restore)";
        return true;
    }

    /// <summary>Every live file a restore replaces: the database with its WAL and shared-memory files, and the config file.</summary>
    private static IEnumerable<string> LiveFiles(WondarrPaths paths)
    {
        yield return paths.DatabaseFile;
        yield return paths.DatabaseFile + "-wal";
        yield return paths.DatabaseFile + "-shm";
        yield return paths.ConfigFile;
    }

    /// <summary>Moves a live file aside under its <c>*.pre-restore</c> name, replacing an older one.</summary>
    private static void SetAside(string live, List<(string From, string To)> moves)
    {
        var aside = live + PreRestoreSuffix;

        if (File.Exists(aside))
        {
            File.Delete(aside);
        }

        File.Move(live, aside);
        moves.Add((live, aside));
    }

    /// <summary>Moves a staged file into place.</summary>
    private static void Install(string staged, string live, List<(string From, string To)> moves)
    {
        File.Move(staged, live);
        moves.Add((staged, live));
    }

    /// <summary>
    /// Puts everything back: the staged files that were moved in return to the restore folder, and
    /// the <c>*.pre-restore</c> files take their old names back. A rollback that itself fails is
    /// swallowed — the ordering is what matters, and the database is either live or still sitting
    /// under its <c>*.pre-restore</c> name.
    /// </summary>
    private static void RollBack(List<(string From, string To)> installed, List<(string From, string To)> setAside)
    {
        try
        {
            for (var index = installed.Count - 1; index >= 0; index--)
            {
                var (staged, live) = installed[index];

                if (File.Exists(live))
                {
                    File.Move(live, staged);
                }
            }

            for (var index = setAside.Count - 1; index >= 0; index--)
            {
                var (live, aside) = setAside[index];

                if (File.Exists(aside))
                {
                    if (File.Exists(live))
                    {
                        File.Delete(live);
                    }

                    File.Move(aside, live);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done here; the message the caller logs says the restore failed.
        }
    }
}
