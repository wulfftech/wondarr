using Microsoft.Data.Sqlite;
using Wondarr.Core.Configuration;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.Backup;

/// <summary>
/// Applies a staged restore — the two files <see cref="BackupService"/> wrote into
/// <c>&lt;ConfigDir&gt;/restore/</c> — before the database is opened and before
/// <c>ConfigFileInitializer</c> runs. The live files are moved aside as <c>*.pre-restore</c> first
/// (the last <see cref="Generations"/> sets are kept), so a failure half-way can put everything back
/// and the install is never left without a database and without its pre-restore copy.
/// </summary>
/// <remarks>
/// A journal file (<c>restore.journal</c> in the configuration directory) exists from before the
/// first move to after the last. If the process dies in between, the next start finds the journal
/// and finishes the restore (see <see cref="RecoverFromJournal"/>).
/// </remarks>
public static class PendingRestore
{
    /// <summary>The folder a staged restore waits in.</summary>
    public const string RestoreFolderName = "restore";

    /// <summary>The suffix the replaced files are kept under.</summary>
    public const string PreRestoreSuffix = ".pre-restore";

    /// <summary>The file that marks a restore as started and not yet finished.</summary>
    public const string JournalFileName = "restore.journal";

    /// <summary>How many sets of replaced files are kept: <c>.pre-restore</c>, <c>.pre-restore.1</c> and <c>.pre-restore.2</c>.</summary>
    public const int Generations = 3;

    private const string DiscardSuffix = ".discard";

    /// <summary>
    /// Applies the staged restore when both staged files exist, after finishing any restore a crash
    /// left half-done.
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
        var journal = Path.Combine(paths.ConfigDir, JournalFileName);

        string? recovered = null;
        var recoveredApplied = false;

        if (File.Exists(journal))
        {
            recovered = RecoverFromJournal(paths, journal, restoreDirectory, stagedDatabase, stagedConfig, out recoveredApplied);
        }

        if (!File.Exists(stagedDatabase) || !File.Exists(stagedConfig))
        {
            message = recovered;
            return recoveredApplied;
        }

        // Every move, in order: (the path the file was moved from, the path it now sits at). A
        // rollback replays them backwards.
        var moves = new List<(string From, string To)>();
        var discarded = new List<string>();

        try
        {
            File.WriteAllText(journal, "applying a staged restore; delete this file only if no restore is in progress\n");

            // Fold the write-ahead log into the database, so the file set aside is complete by
            // itself and undoing the restore needs no -wal file (R-056).
            Checkpoint(paths.DatabaseFile);

            // The live files first, so the staged ones can take their names. The WAL and
            // shared-memory files move with the database, exactly as AdoptLegacyDatabase moves them.
            foreach (var live in LiveFiles(paths))
            {
                if (File.Exists(live))
                {
                    SetAside(live, moves, discarded);
                }
            }

            Install(stagedDatabase, paths.DatabaseFile, moves);
            Install(stagedConfig, paths.ConfigFile, moves);

            // Everything is in place: the staged copy is the install's database now, so the
            // folder it came from can go, and so can the generation that fell off the end.
            Directory.Delete(restoreDirectory, recursive: true);

            foreach (var file in discarded)
            {
                File.Delete(file);
            }

            File.Delete(journal);
        }
        catch (Exception exception)
        {
            RollBack(moves);

            // The restore folder is left exactly as it was, so the user can inspect what failed.
            DeleteQuietly(journal);
            message = $"a staged restore could not be applied and was rolled back: {exception.Message}";
            return false;
        }

        message = "restored the database and config.yml from a staged backup (the previous files are kept as *.pre-restore)";
        return true;
    }

    /// <summary>
    /// Deals with a journal left by a start that died part-way through a restore, working out how
    /// far it got from which files exist.
    /// </summary>
    /// <remarks>
    /// The restore is <b>finished</b>, not rolled back, whenever the database has already been
    /// swapped in: the staged <c>config.yml</c> is still intact in <c>restore/</c>, the user asked
    /// for this restore, and a rollback would have to undo the swap and the whole rotation of the
    /// older generations. If nothing was swapped in yet it simply starts over (every move is
    /// idempotent: a live file that is already set aside is not there to be moved again). Only
    /// when the state is not one of those — the live database is gone and nothing staged can
    /// replace it — does it roll back, by putting the <c>*.pre-restore</c> files back.
    /// </remarks>
    private static string? RecoverFromJournal(
        WondarrPaths paths,
        string journal,
        string restoreDirectory,
        string stagedDatabase,
        string stagedConfig,
        out bool applied)
    {
        applied = false;

        var databaseStaged = File.Exists(stagedDatabase);
        var configStaged = File.Exists(stagedConfig);

        if (databaseStaged && configStaged)
        {
            // Nothing was swapped in. The normal path below starts the restore again.
            DeleteQuietly(journal);
            return null;
        }

        try
        {
            if (!databaseStaged && File.Exists(paths.DatabaseFile))
            {
                // The database went in; the config.yml either is still staged or went in too.
                if (configStaged)
                {
                    File.Move(stagedConfig, paths.ConfigFile, overwrite: true);
                }

                if (Directory.Exists(restoreDirectory))
                {
                    Directory.Delete(restoreDirectory, recursive: true);
                }

                DeleteDiscards(paths);
                DeleteQuietly(journal);
                applied = true;
                return "finished a restore that an earlier start did not complete (the previous files are kept as *.pre-restore)";
            }

            // The live database is missing and nothing staged can replace it: put the old files back.
            foreach (var live in LiveFiles(paths))
            {
                var aside = live + PreRestoreSuffix;

                if (!File.Exists(live) && File.Exists(aside))
                {
                    File.Move(aside, live);
                }
            }

            DeleteQuietly(journal);
            return "an earlier restore was interrupted before it could finish; the previous files were put back (rolled back)";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The journal stays, so the next start tries again.
            return $"an interrupted restore could not be completed and was left as it was (rolled back): {exception.Message}";
        }
    }

    /// <summary>Every live file a restore replaces: the database with its WAL and shared-memory files, and the config file.</summary>
    private static IEnumerable<string> LiveFiles(WondarrPaths paths)
    {
        yield return paths.DatabaseFile;
        yield return paths.DatabaseFile + "-wal";
        yield return paths.DatabaseFile + "-shm";
        yield return paths.ConfigFile;
    }

    /// <summary>
    /// Folds the write-ahead log into the database file and truncates it. Failure is not fatal: a
    /// database that will not open is exactly what a user restores to get away from, and the
    /// <c>-wal</c> file is then simply carried aside with it.
    /// </summary>
    private static void Checkpoint(string databaseFile)
    {
        if (!File.Exists(databaseFile) || !HasSqliteHeader(databaseFile))
        {
            return;
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databaseFile,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = DatabaseTimeouts.BusySeconds,
            }.ToString());

            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Carried on below: whatever is left of the WAL moves aside with the database.
        }
    }

    /// <summary>Whether the file starts with SQLite's magic string; anything else is not opened, so it is never touched.</summary>
    private static bool HasSqliteHeader(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[16];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
                && header.SequenceEqual("SQLite format 3\0"u8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Moves a live file aside under its <c>*.pre-restore</c> name, shifting the older generations
    /// along: <c>.pre-restore.1</c> becomes <c>.2</c>, <c>.pre-restore</c> becomes <c>.1</c>, and
    /// the old <c>.2</c> is parked under a <c>.discard</c> name until the restore has succeeded.
    /// </summary>
    private static void SetAside(string live, List<(string From, string To)> moves, List<string> discarded)
    {
        var first = live + PreRestoreSuffix;

        for (var generation = Generations - 1; generation >= 1; generation--)
        {
            var older = GenerationPath(first, generation);
            var newer = GenerationPath(first, generation - 1);

            if (generation == Generations - 1 && File.Exists(older))
            {
                var parked = older + DiscardSuffix;
                File.Move(older, parked, overwrite: true);
                moves.Add((older, parked));
                discarded.Add(parked);
            }

            if (File.Exists(newer))
            {
                File.Move(newer, older);
                moves.Add((newer, older));
            }
        }

        File.Move(live, first);
        moves.Add((live, first));
    }

    private static string GenerationPath(string first, int generation) =>
        generation == 0 ? first : $"{first}.{generation}";

    /// <summary>Moves a staged file into place.</summary>
    private static void Install(string staged, string live, List<(string From, string To)> moves)
    {
        File.Move(staged, live);
        moves.Add((staged, live));
    }

    /// <summary>Deletes the <c>.discard</c> files a finished restore leaves behind.</summary>
    private static void DeleteDiscards(WondarrPaths paths)
    {
        foreach (var live in LiveFiles(paths))
        {
            DeleteQuietly(GenerationPath(live + PreRestoreSuffix, Generations - 1) + DiscardSuffix);
        }
    }

    /// <summary>
    /// Puts everything back by replaying the moves backwards: the staged files that were moved in
    /// return to the restore folder, the <c>*.pre-restore</c> files take their old names back, and
    /// the older generations shift down again. A rollback that itself fails is swallowed — the
    /// ordering is what matters, and the database is either live or still sitting under its
    /// <c>*.pre-restore</c> name.
    /// </summary>
    private static void RollBack(List<(string From, string To)> moves)
    {
        try
        {
            for (var index = moves.Count - 1; index >= 0; index--)
            {
                var (from, to) = moves[index];

                if (File.Exists(to))
                {
                    File.Move(to, from, overwrite: true);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done here; the message the caller logs says the restore failed.
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover journal is harmless: the next start looks at the files again.
        }
    }
}
