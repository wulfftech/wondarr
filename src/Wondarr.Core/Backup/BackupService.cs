using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.Backup;

/// <summary>
/// Writes and reads Wondarr's backups: a zip holding <c>wondarr.db</c> — a consistent copy made with
/// SQLite's online backup API, never a copy of the live file, which is in WAL mode — and
/// <c>config.yml</c>. A restore is never applied in place: the archive is validated, written to
/// <c>&lt;ConfigDir&gt;/restore/</c> and swapped in by <see cref="PendingRestore"/> before the
/// database is opened on the next start.
/// </summary>
public sealed partial class BackupService : IBackupService
{
    /// <summary>The name of the database entry inside a backup zip.</summary>
    public const string DatabaseEntryName = "wondarr.db";

    /// <summary>The name of the configuration entry inside a backup zip.</summary>
    public const string ConfigEntryName = "config.yml";

    private const string TimestampFormat = "yyyy.MM.dd_HH.mm.ss";
    private const int TimestampLength = 19;
    private const int CopyBufferSize = 81920;
    private const string TemporaryDatabasePrefix = "backup-database-";

    /// <summary>The most a staged <c>config.yml</c> may extract to; a real one is a few kilobytes.</summary>
    private const long MaxConfigBytes = 16L * 1024 * 1024;

    /// <summary>How old a leftover temporary file must be before the start-up sweep deletes it.</summary>
    private static readonly TimeSpan TemporaryFileAge = TimeSpan.FromHours(1);

    // One backup or staging at a time per process (R-052): the temporary names are unique, but two
    // overlapping restores would still pair one backup's database with the other's config.yml.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly string Version = ResolveVersion();

    private readonly WondarrPaths _paths;
    private readonly IOptions<BackupOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BackupService> _logger;

    /// <summary>Initialises a new instance of the <see cref="BackupService"/> class.</summary>
    /// <param name="paths">The configuration directory the database and config file live in.</param>
    /// <param name="options">The backup settings, including the folder the backups live in.</param>
    /// <param name="timeProvider">The clock the backup names and the retention read.</param>
    /// <param name="logger">The logger.</param>
    public BackupService(
        WondarrPaths paths,
        IOptions<BackupOptions> options,
        TimeProvider timeProvider,
        ILogger<BackupService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The running app's version, as it is recorded in backup names and in the database.</summary>
    public static string CurrentVersion => Version;

    /// <summary>The folder the backups live in: <c>backup.folder</c>, or the default under the configuration directory.</summary>
    private string Folder => string.IsNullOrWhiteSpace(_options.Value.Folder)
        ? _paths.BackupsDir
        : Path.GetFullPath(_options.Value.Folder);

    /// <inheritdoc />
    public Task<BackupItem> CreateAsync(BackupType type, CancellationToken cancellationToken) =>
        CreateCoreAsync(type, $"wondarr_backup_v{Version}", cancellationToken);

    /// <inheritdoc />
    public Task<BackupItem> CreatePreMigrationAsync(string previousVersion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousVersion);

        var safe = string.Concat(previousVersion.Select(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_'));

        // A scheduled backup, so the usual retention clears it out in time.
        return CreateCoreAsync(BackupType.Scheduled, $"wondarr_pre-{safe}", cancellationToken);
    }

    private async Task<BackupItem> CreateCoreAsync(BackupType type, string namePrefix, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await CreateLockedAsync(type, namePrefix, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<BackupItem> CreateLockedAsync(BackupType type, string namePrefix, CancellationToken cancellationToken)
    {
        var directory = DirectoryOf(type);
        Directory.CreateDirectory(directory);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var stem = $"{namePrefix}_{now.ToString(TimestampFormat, CultureInfo.InvariantCulture)}";

        // Both halves are built under temporary names in the destination folder, so a crash never
        // leaves a half-written zip under its final name. The names are unique, so nothing else
        // can be writing to them.
        var unique = Guid.NewGuid().ToString("N");
        var temporaryZip = Path.Combine(directory, $"{stem}.{unique}.zip.tmp");
        var temporaryDatabase = Path.Combine(directory, $"{TemporaryDatabasePrefix}{unique}.tmp");

        try
        {
            // The online backup API is the only consistent way to copy a WAL database: it reads
            // through SQLite, so the copy includes the writes that are still in the write-ahead log.
            // The temporary copy is not pooled, so the file can be deleted as soon as it is closed.
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _paths.DatabaseFile,
                Pooling = false,
                DefaultTimeout = DatabaseTimeouts.BusySeconds,
            }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporaryDatabase,
                Pooling = false,
            }.ToString()))
            {
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                source.BackupDatabase(destination);
            }

            using (var archive = new ZipArchive(File.Create(temporaryZip), ZipArchiveMode.Create))
            {
                await WriteEntryAsync(archive, DatabaseEntryName, temporaryDatabase, cancellationToken)
                    .ConfigureAwait(false);
                await WriteEntryAsync(archive, ConfigEntryName, _paths.ConfigFile, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Never overwrites: a second backup in the same second gets a numeric suffix.
            var name = stem + ".zip";
            var target = Path.Combine(directory, name);

            for (var suffix = 1; ; suffix++)
            {
                try
                {
                    File.Move(temporaryZip, target, overwrite: false);
                    break;
                }
                catch (IOException) when (File.Exists(target))
                {
                    name = $"{stem}_{suffix}.zip";
                    target = Path.Combine(directory, name);
                }
            }

            LogCreated(_logger, type, target);

            return new BackupItem(name, name, type, new FileInfo(target).Length, now);
        }
        finally
        {
            DeleteIfExists(temporaryDatabase);
            DeleteIfExists(temporaryZip);
        }
    }

    /// <inheritdoc />
    public int SweepTemporaryFiles()
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - TemporaryFileAge;
        var swept = 0;

        var targets = new List<(string Directory, string Pattern)>
        {
            (_paths.ConfigDir, "restore-upload-*.tmp"),
            (_paths.ConfigDir, "restore-database-*.tmp"),
            (_paths.ConfigDir, "restore-config-*.tmp"),
        };

        foreach (var (_, directory) in TypeDirectories())
        {
            targets.Add((directory, "*.zip.tmp"));
            targets.Add((directory, $"{TemporaryDatabasePrefix}*.tmp"));
        }

        foreach (var (directory, pattern) in targets)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, pattern))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoff)
                    {
                        continue;
                    }

                    File.Delete(file);
                    swept++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    LogSweepFailed(_logger, file, exception.Message);
                }
            }
        }

        if (swept > 0)
        {
            LogSwept(_logger, swept);
        }

        return swept;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BackupItem>> GetAllAsync(CancellationToken cancellationToken)
    {
        var backups = new List<BackupItem>();

        foreach (var (type, directory) in TypeDirectories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.zip"))
            {
                var info = new FileInfo(file);
                var name = info.Name;

                backups.Add(new BackupItem(
                    name,
                    name,
                    type,
                    info.Length,
                    ParseTime(name, info.LastWriteTimeUtc)));
            }
        }

        backups.Sort(static (left, right) => right.Time.CompareTo(left.Time));

        return Task.FromResult<IReadOnlyList<BackupItem>>(backups);
    }

    /// <inheritdoc />
    public bool Delete(string id)
    {
        var path = Resolve(id);

        if (path is null)
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <inheritdoc />
    public FileStream? OpenRead(string id)
    {
        var path = Resolve(id);

        return path is null ? null : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <inheritdoc />
    public async Task<int> CleanUpAsync(CancellationToken cancellationToken)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromDays(_options.Value.RetentionDays);
        var deleted = 0;

        var directory = DirectoryOf(BackupType.Scheduled);
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.zip"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = new FileInfo(file);

            if (ParseTime(info.Name, info.LastWriteTimeUtc) >= cutoff)
            {
                continue;
            }

            File.Delete(file);
            deleted++;
        }

        if (deleted > 0)
        {
            LogCleanedUp(_logger, deleted, _options.Value.RetentionDays);
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<StagedRestoreResult> StageRestoreAsync(Stream zip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zip);

        // The upload can be hundreds of megabytes, so it is streamed to a temporary file under the
        // configuration directory rather than buffered in memory.
        var temporaryZip = Path.Combine(_paths.ConfigDir, $"restore-upload-{Guid.NewGuid():N}.tmp");
        var temporaryDatabase = Path.Combine(_paths.ConfigDir, $"restore-database-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var buffer = File.Create(temporaryZip))
            {
                await zip.CopyToAsync(buffer, CopyBufferSize, cancellationToken).ConfigureAwait(false);
            }

            // Blocks of their own, so the archive's file handle is gone before the finally below
            // deletes the temporary zip — even when the archive itself turns out not to be a zip.
            using (var file = File.OpenRead(temporaryZip))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
            {
                // Entries are only ever read by their exact top-level names, so an entry named
                // "../evil" is simply not found and zip-slip is impossible by construction.
                var database = archive.GetEntry(DatabaseEntryName);
                var config = archive.GetEntry(ConfigEntryName);

                if (database is null || config is null)
                {
                    return StagedRestoreResult.Failed(
                        $"the archive must contain '{DatabaseEntryName}' and '{ConfigEntryName}' at its top level");
                }

                var cap = _options.Value.MaxRestoreDatabaseBytes;

                if (!await ExtractCappedAsync(database, temporaryDatabase, cap, cancellationToken).ConfigureAwait(false))
                {
                    return StagedRestoreResult.Failed(
                        $"the database in the archive is larger than the {cap} byte limit (backup.max_restore_database_bytes)");
                }

                var validation = await ValidateDatabaseAsync(temporaryDatabase, cancellationToken).ConfigureAwait(false);

                if (validation is not null)
                {
                    return StagedRestoreResult.Failed(validation);
                }

                var temporaryConfig = Path.Combine(_paths.ConfigDir, $"restore-config-{Guid.NewGuid():N}.tmp");

                try
                {
                    if (!await ExtractCappedAsync(config, temporaryConfig, MaxConfigBytes, cancellationToken).ConfigureAwait(false))
                    {
                        return StagedRestoreResult.Failed(
                            $"config.yml in the archive is larger than {MaxConfigBytes} bytes");
                    }

                    await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

                    try
                    {
                        Stage(temporaryConfig, temporaryDatabase);
                    }
                    finally
                    {
                        Gate.Release();
                    }
                }
                finally
                {
                    DeleteIfExists(temporaryConfig);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or SqliteException)
        {
            return StagedRestoreResult.Failed($"the archive is not a valid Wondarr backup: {exception.Message}");
        }
        finally
        {
            DeleteIfExists(temporaryDatabase);
            DeleteIfExists(temporaryZip);
        }

        return StagedRestoreResult.Success;
    }

    /// <inheritdoc />
    public async Task<StagedRestoreResult> StageRestoreAsync(string id, CancellationToken cancellationToken)
    {
        var path = Resolve(id);

        if (path is null)
        {
            return StagedRestoreResult.Failed($"no backup named '{id}' exists");
        }

        await using var zip = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        return await StageRestoreAsync(zip, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the two staged files into <c>&lt;ConfigDir&gt;/restore/</c>, replacing any earlier
    /// staged restore.
    /// </summary>
    private void Stage(string temporaryConfig, string temporaryDatabase)
    {
        var restoreDirectory = Path.Combine(_paths.ConfigDir, "restore");

        if (Directory.Exists(restoreDirectory))
        {
            Directory.Delete(restoreDirectory, recursive: true);
        }

        Directory.CreateDirectory(restoreDirectory);

        File.Move(temporaryDatabase, Path.Combine(restoreDirectory, DatabaseEntryName));
        File.Move(temporaryConfig, Path.Combine(restoreDirectory, ConfigEntryName));
    }

    /// <summary>
    /// Copies an archive entry to a file, counting the bytes actually produced rather than trusting
    /// the size the archive declares, and stops at <paramref name="cap"/>.
    /// </summary>
    /// <returns><see langword="false"/> when the entry holds more than <paramref name="cap"/> bytes (the partial file is deleted).</returns>
    private static async Task<bool> ExtractCappedAsync(
        ZipArchiveEntry entry,
        string destination,
        long cap,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        long total = 0;

        await using (var source = entry.Open())
        await using (var target = File.Create(destination))
        {
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;

                if (total > cap)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        if (total <= cap)
        {
            return true;
        }

        DeleteIfExists(destination);
        return false;
    }

    /// <summary>
    /// Checks that the extracted database opens, passes SQLite's integrity check and carries the
    /// migrations table a Wondarr database always has.
    /// </summary>
    /// <returns><see langword="null"/> when the database is sound, or the reason it is not.</returns>
    private static async Task<string?> ValidateDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;

            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return $"the database in the archive failed the integrity check ({result})";
            }
        }

        using (var tables = connection.CreateCommand())
        {
            tables.CommandText =
                "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory';";
            var count = Convert.ToInt64(await tables.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

            if (count == 0)
            {
                return "the database in the archive has no __EFMigrationsHistory table, so it is not a Wondarr backup";
            }
        }

        // A backup from a newer Wondarr would start, then fail on every query that touches a column
        // this build has never heard of (R-039).
        using (var history = connection.CreateCommand())
        {
            // The snake_case convention renames the history columns too, so read positionally.
            history.CommandText = "SELECT * FROM \"__EFMigrationsHistory\";";

            string? newest = null;
            using var reader = await history.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetString(0);

                if (!KnownMigrations.Ids.Contains(id) && (newest is null || string.CompareOrdinal(id, newest) > 0))
                {
                    newest = id;
                }
            }

            if (newest is not null)
            {
                return $"this backup comes from a newer Wondarr: it contains the migration '{newest}', which this version does not know";
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a backup id to its file, confined to <c>scheduled/</c> and <c>manual/</c>: an id that
    /// is not a <c>.zip</c>, that is absolute, or that resolves outside those folders — through
    /// <c>..</c> or a sibling folder — is not found.
    /// </summary>
    private string? Resolve(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || Path.IsPathRooted(id)
            || !id.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var (_, directory) in TypeDirectories())
        {
            string full;

            try
            {
                full = Path.GetFullPath(Path.Combine(directory, id));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            // The trailing separator is what keeps a sibling folder (backups2) out.
            if (!full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    private (BackupType Type, string Directory)[] TypeDirectories() =>
    [
        (BackupType.Scheduled, DirectoryOf(BackupType.Scheduled)),
        (BackupType.Manual, DirectoryOf(BackupType.Manual)),
    ];

    private string DirectoryOf(BackupType type) => Path.Combine(
        Folder,
        type == BackupType.Scheduled ? "scheduled" : "manual");

    /// <summary>Reads the timestamp out of a backup's name, falling back to the file's write time.</summary>
    private static DateTime ParseTime(string name, DateTime fallbackUtc)
    {
        var stem = Path.GetFileNameWithoutExtension(name);

        // A backup made in the same second as another carries a numeric suffix (..._1).
        var suffixed = CollisionSuffix().Match(stem);
        if (suffixed.Success)
        {
            stem = suffixed.Groups["stem"].Value;
        }

        return stem.Length >= TimestampLength
            && DateTime.TryParseExact(
                stem[^TimestampLength..],
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var time)
            ? time
            : fallbackUtc;
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string entryName,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName);

        await using var target = entry.Open();
        await using var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        await source.CopyToAsync(target, CopyBufferSize, cancellationToken).ConfigureAwait(false);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The running app's version, the way the system-status endpoint reads it: the entry assembly's
    /// informational version, without the source revision a local build appends.
    /// </summary>
    private static string ResolveVersion()
    {
        var version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

        var plus = version.IndexOf('+', StringComparison.Ordinal);

        return plus >= 0 ? version[..plus] : version;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created a {Type} backup at {Path}")]
    private static partial void LogCreated(ILogger logger, BackupType type, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Swept {Count} leftover temporary backup and restore files")]
    private static partial void LogSwept(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the leftover temporary file {Path}: {Reason}")]
    private static partial void LogSweepFailed(ILogger logger, string path, string reason);

    [GeneratedRegex(@"^(?<stem>.*\d{2}\.\d{2}_\d{2}\.\d{2}\.\d{2})_\d+$")]
    private static partial Regex CollisionSuffix();

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} scheduled backups older than {RetentionDays} days")]
    private static partial void LogCleanedUp(ILogger logger, int count, int retentionDays);
}
