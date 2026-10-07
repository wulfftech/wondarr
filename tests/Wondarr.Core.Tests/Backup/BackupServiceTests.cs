using System.Globalization;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Backup;
using Wondarr.Core.Configuration;
using Xunit;

namespace Wondarr.Core.Tests.Backup;

/// <summary>
/// Exercises <see cref="BackupService"/> against a real temporary folder and a real SQLite database
/// in WAL mode with uncheckpointed writes, which is the only way to prove the online backup copies
/// what is still in the write-ahead log.
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    private const string LastWrite = "the-last-write";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wondarr-backup-tests", Guid.NewGuid().ToString("N"));
    private readonly WondarrPaths _paths;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-02T03:04:05Z", CultureInfo.InvariantCulture));
    private readonly BackupService _backups;

    // Held open for the whole test, so the WAL is never checkpointed back into the main file.
    private readonly SqliteConnection _database;

    public BackupServiceTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new WondarrPaths(_root);
        _backups = new BackupService(
            _paths,
            Options.Create(new BackupOptions()),
            _time,
            NullLogger<BackupService>.Instance);

        _database = new SqliteConnection($"Data Source={_paths.DatabaseFile}");
        _database.Open();

        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA wal_autocheckpoint=0;");
        Execute("CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL, ProductVersion TEXT NOT NULL);");
        Execute("INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('20260101000000_Initial', '1.0.0');");
        Execute("CREATE TABLE marker (value TEXT);");

        File.WriteAllText(_paths.ConfigFile, "server:\n  api_key: test\n");
    }

    [Fact]
    public async Task A_backup_of_a_WAL_database_contains_the_uncheckpointed_writes()
    {
        Execute($"INSERT INTO marker (value) VALUES ('{LastWrite}');");

        new FileInfo(_paths.DatabaseFile + "-wal").Length.Should().BeGreaterThan(0,
            "the write has to still be in the write-ahead log for this test to mean anything");

        var backup = await _backups.CreateAsync(BackupType.Manual, CancellationToken.None);

        using (var archive = ZipFile.OpenRead(Path.Combine(_paths.BackupsDir, "manual", backup.Name)))
        {
            archive.GetEntry(BackupService.DatabaseEntryName).Should().NotBeNull();
            archive.GetEntry(BackupService.ConfigEntryName).Should().NotBeNull();
        }

        (await ReadMarkerAsync(backup)).Should().Be(LastWrite);
    }

    [Fact]
    public async Task A_backup_is_named_after_the_version_and_the_moment_it_was_made()
    {
        var backup = await _backups.CreateAsync(BackupType.Scheduled, CancellationToken.None);

        backup.Name.Should().MatchRegex(@"^wondarr_backup_v.+_2026\.01\.02_03\.04\.05\.zip$");
        backup.Id.Should().Be(backup.Name);
        backup.Type.Should().Be(BackupType.Scheduled);
        backup.Time.Should().Be(_time.GetUtcNow().UtcDateTime);
        backup.Size.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_list_is_newest_first_and_reads_the_time_from_the_name()
    {
        await _backups.CreateAsync(BackupType.Manual, CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(2));
        var newest = await _backups.CreateAsync(BackupType.Scheduled, CancellationToken.None);

        var all = await _backups.GetAllAsync(CancellationToken.None);

        all.Should().HaveCount(2);
        all[0].Id.Should().Be(newest.Id);
        all[0].Type.Should().Be(BackupType.Scheduled);
        all[0].Time.Should().Be(_time.GetUtcNow().UtcDateTime);
        all[1].Time.Should().Be(_time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task Delete_and_OpenRead_only_reach_zips_inside_the_backup_folder()
    {
        var backup = await _backups.CreateAsync(BackupType.Manual, CancellationToken.None);
        var stored = Path.Combine(_paths.BackupsDir, "manual", backup.Name);

        // A real zip one level up, a real zip in a sibling folder, and a non-zip next to the backups.
        var outside = Path.Combine(_paths.BackupsDir, "..", "outside.zip");
        var sibling = Path.Combine(_paths.BackupsDir, "..", "backups2", backup.Name);
        var text = Path.Combine(_paths.BackupsDir, "manual", "notes.txt");
        File.Copy(stored, outside);
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.Copy(stored, sibling);
        File.WriteAllText(text, "not a backup");

        var escapes = new[]
        {
            "../outside.zip",
            "../backups2/" + backup.Name,
            "notes.txt",
            stored,
        };

        foreach (var id in escapes)
        {
            _backups.Delete(id).Should().BeFalse($"'{id}' must not resolve to a backup");
            _backups.OpenRead(id).Should().BeNull($"'{id}' must not resolve to a backup");
        }

        File.Exists(outside).Should().BeTrue("nothing outside the backup folder may be deleted");
        File.Exists(sibling).Should().BeTrue("a sibling folder must not pass the confinement check");
        File.Exists(text).Should().BeTrue("a non-zip must not be touched");

        using (var opened = _backups.OpenRead(backup.Id))
        {
            opened.Should().NotBeNull();
        }

        _backups.Delete(backup.Id).Should().BeTrue();
        File.Exists(stored).Should().BeFalse();
    }

    [Fact]
    public async Task CleanUp_deletes_only_old_scheduled_backups()
    {
        var old = await _backups.CreateAsync(BackupType.Scheduled, CancellationToken.None);
        _time.Advance(TimeSpan.FromDays(10));
        var kept = await _backups.CreateAsync(BackupType.Scheduled, CancellationToken.None);
        var manual = await _backups.CreateAsync(BackupType.Manual, CancellationToken.None);
        _time.Advance(TimeSpan.FromDays(20));

        var deleted = await _backups.CleanUpAsync(CancellationToken.None);

        deleted.Should().Be(1);
        Exists(old).Should().BeFalse();
        Exists(kept).Should().BeTrue();
        Exists(manual).Should().BeTrue("manual backups are never deleted automatically");
    }

    [Fact]
    public async Task Staging_a_stored_backup_writes_the_two_files_into_the_restore_folder()
    {
        Execute($"INSERT INTO marker (value) VALUES ('{LastWrite}');");
        var backup = await _backups.CreateAsync(BackupType.Manual, CancellationToken.None);

        var result = await _backups.StageRestoreAsync(backup.Id, CancellationToken.None);

        result.Staged.Should().BeTrue(result.Reason);
        var restore = Path.Combine(_paths.ConfigDir, PendingRestore.RestoreFolderName);
        File.Exists(Path.Combine(restore, BackupService.DatabaseEntryName)).Should().BeTrue();
        File.Exists(Path.Combine(restore, BackupService.ConfigEntryName)).Should().BeTrue();

        using (var connection = new SqliteConnection($"Data Source={Path.Combine(restore, BackupService.DatabaseEntryName)}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM marker;";
            command.ExecuteScalar().Should().Be(LastWrite);
        }
    }

    [Fact]
    public async Task Staging_replaces_an_earlier_staged_restore()
    {
        var first = await _backups.CreateAsync(BackupType.Manual, CancellationToken.None);
        await _backups.StageRestoreAsync(first.Id, CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));
        var second = await _backups.CreateAsync(BackupType.Manual, CancellationToken.None);

        var result = await _backups.StageRestoreAsync(second.Id, CancellationToken.None);

        result.Staged.Should().BeTrue(result.Reason);
        var restore = new DirectoryInfo(Path.Combine(_paths.ConfigDir, PendingRestore.RestoreFolderName));
        restore.GetFiles().Should().HaveCount(2);
        restore.GetFiles().Select(file => file.Name).Should().BeEquivalentTo(
            [BackupService.DatabaseEntryName, BackupService.ConfigEntryName]);
    }

    [Fact]
    public async Task Staging_rejects_a_zip_without_the_database()
    {
        using var zip = Zip((BackupService.ConfigEntryName, Text("server:\n  api_key: test\n")));

        var result = await _backups.StageRestoreAsync(zip, CancellationToken.None);

        result.Staged.Should().BeFalse();
        result.Reason.Should().NotBeNullOrWhiteSpace();
        Directory.Exists(Path.Combine(_paths.ConfigDir, PendingRestore.RestoreFolderName)).Should().BeFalse();
    }

    [Fact]
    public async Task Staging_rejects_a_corrupt_database()
    {
        using var zip = Zip(
            (BackupService.DatabaseEntryName, Text("this is not a database at all")),
            (BackupService.ConfigEntryName, Text("server:\n  api_key: test\n")));

        var result = await _backups.StageRestoreAsync(zip, CancellationToken.None);

        result.Staged.Should().BeFalse();
        result.Reason.Should().NotBeNullOrWhiteSpace();
        Directory.Exists(Path.Combine(_paths.ConfigDir, PendingRestore.RestoreFolderName)).Should().BeFalse();
    }

    [Fact]
    public async Task Staging_rejects_a_database_without_the_migrations_table()
    {
        var path = Path.Combine(_root, "no-migrations.db");

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE something_else (value TEXT);";
            command.ExecuteNonQuery();
        }

        using var zip = Zip(
            (BackupService.DatabaseEntryName, File.ReadAllBytes(path)),
            (BackupService.ConfigEntryName, Text("server:\n  api_key: test\n")));

        var result = await _backups.StageRestoreAsync(zip, CancellationToken.None);

        result.Staged.Should().BeFalse();
        result.Reason.Should().Contain("__EFMigrationsHistory");
    }

    [Fact]
    public async Task Staging_never_writes_an_entry_that_escapes_the_archive_root()
    {
        using var zip = Zip(
            ("../../evil", Text("outside")),
            (BackupService.ConfigEntryName, Text("server:\n  api_key: test\n")));

        var result = await _backups.StageRestoreAsync(zip, CancellationToken.None);

        result.Staged.Should().BeFalse("the archive has no database in it");
        File.Exists(Path.Combine(_paths.ConfigDir, "..", "evil")).Should().BeFalse(
            "nothing may be written outside the restore folder");
        Directory.Exists(Path.Combine(_paths.ConfigDir, PendingRestore.RestoreFolderName)).Should().BeFalse();
    }

    [Fact]
    public async Task Staging_reads_only_the_two_entries_it_knows_by_name()
    {
        Execute($"INSERT INTO marker (value) VALUES ('{LastWrite}');");
        var backup = await _backups.CreateAsync(BackupType.Manual, CancellationToken.None);
        var path = Path.Combine(_paths.BackupsDir, "manual", backup.Name);

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.CreateEntry("../../evil");
        }

        var result = await _backups.StageRestoreAsync(backup.Id, CancellationToken.None);

        result.Staged.Should().BeTrue(result.Reason);
        File.Exists(Path.Combine(_paths.ConfigDir, "..", "evil")).Should().BeFalse(
            "an entry that is not read by its exact name must never be extracted");
        new DirectoryInfo(Path.Combine(_paths.ConfigDir, PendingRestore.RestoreFolderName))
            .GetFiles().Should().HaveCount(2);
    }

    public void Dispose()
    {
        _database.Dispose();

        foreach (var file in Directory.GetFiles(_root, "*.db", SearchOption.AllDirectories))
        {
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={file}"));
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A Windows delete can lose the race with a pooled connection going away; the temp
            // folder is cleaned by the operating system anyway.
        }
    }

    private bool Exists(BackupItem backup) =>
        File.Exists(Path.Combine(_paths.BackupsDir, backup.Type == BackupType.Scheduled ? "scheduled" : "manual", backup.Name));

    private async Task<string?> ReadMarkerAsync(BackupItem backup)
    {
        var extracted = Path.Combine(_root, "extracted.db");

        using (var archive = ZipFile.OpenRead(Path.Combine(_paths.BackupsDir, "manual", backup.Name)))
        {
            archive.GetEntry(BackupService.DatabaseEntryName)!.ExtractToFile(extracted, overwrite: true);
        }

        using var connection = new SqliteConnection($"Data Source={extracted}");
        await connection.OpenAsync(CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM marker;";

        return await command.ExecuteScalarAsync(CancellationToken.None) as string;
    }

    private void Execute(string sql)
    {
        using var command = _database.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static byte[] Text(string content) => Encoding.UTF8.GetBytes(content);

    /// <summary>Builds an in-memory zip whose entries are given as (exact name, content).</summary>
    private static MemoryStream Zip(params (string Name, byte[] Content)[] entries)
    {
        var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = entry.Open();
                writer.Write(content, 0, content.Length);
            }
        }

        buffer.Position = 0;

        return buffer;
    }
}
