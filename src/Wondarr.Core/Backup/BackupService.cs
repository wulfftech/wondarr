using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;

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

    /// <summary>The folder the backups live in: <c>backup.folder</c>, or the default under the configuration directory.</summary>
    private string Folder => string.IsNullOrWhiteSpace(_options.Value.Folder)
        ? _paths.BackupsDir
        : Path.GetFullPath(_options.Value.Folder);

    /// <inheritdoc />
    public async Task<BackupItem> CreateAsync(BackupType type, CancellationToken cancellationToken)
    {
        var directory = DirectoryOf(type);
        Directory.CreateDirectory(directory);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var name = $"wondarr_backup_v{Version}_{now.ToString(TimestampFormat, CultureInfo.InvariantCulture)}.zip";
        var target = Path.Combine(directory, name);

        // Both halves are built under temporary names in the destination folder, so a crash never
        // leaves a half-written zip under its final name.
        var temporaryZip = target + ".tmp";
        var temporaryDatabase = Path.Combine(directory, "wondarr.db.tmp");

        try
        {
            if (File.Exists(temporaryDatabase))
            {
                File.Delete(temporaryDatabase);
            }

            // The online backup API is the only consistent way to copy a WAL database: it reads
            // through SQLite, so the copy includes the writes that are still in the write-ahead log.
            // The temporary copy is not pooled, so the file can be deleted as soon as it is closed.
            using (var source = new SqliteConnection($"Data Source={_paths.DatabaseFile}"))
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

            File.Move(temporaryZip, target, overwrite: true);

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

                database.ExtractToFile(temporaryDatabase, overwrite: true);

                var validation = await ValidateDatabaseAsync(temporaryDatabase, cancellationToken).ConfigureAwait(false);

                if (validation is not null)
                {
                    return StagedRestoreResult.Failed(validation);
                }

                Stage(config, temporaryDatabase);
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
    private void Stage(ZipArchiveEntry config, string temporaryDatabase)
    {
        var restoreDirectory = Path.Combine(_paths.ConfigDir, "restore");

        if (Directory.Exists(restoreDirectory))
        {
            Directory.Delete(restoreDirectory, recursive: true);
        }

        Directory.CreateDirectory(restoreDirectory);

        File.Move(temporaryDatabase, Path.Combine(restoreDirectory, DatabaseEntryName));
        config.ExtractToFile(Path.Combine(restoreDirectory, ConfigEntryName), overwrite: true);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} scheduled backups older than {RetentionDays} days")]
    private static partial void LogCleanedUp(ILogger logger, int count, int retentionDays);
}
