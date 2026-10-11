using FluentAssertions;
using Wondarr.Core.Backup;
using Wondarr.Core.Configuration;
using Xunit;

namespace Wondarr.Core.Tests.Backup;

/// <summary>
/// Exercises <see cref="PendingRestore"/> against a real temporary configuration directory: the
/// staged files must replace the live ones, the old ones must survive as <c>*.pre-restore</c>, and
/// a failure half-way must put everything back.
/// </summary>
public sealed class PendingRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wondarr-restore-tests", Guid.NewGuid().ToString("N"));
    private readonly WondarrPaths _paths;

    public PendingRestoreTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new WondarrPaths(_root);
    }

    [Fact]
    public void A_staged_restore_replaces_the_live_files_and_keeps_the_old_ones_as_pre_restore()
    {
        Write(_paths.DatabaseFile, "the old database");
        Write(_paths.DatabaseFile + "-wal", "the old write-ahead log");
        Write(_paths.DatabaseFile + "-shm", "the old shared memory");
        Write(_paths.ConfigFile, "the old config");
        Stage("the new database", "the new config");

        var applied = PendingRestore.ApplyIfStaged(_paths, out var message);

        applied.Should().BeTrue();
        message.Should().NotBeNullOrWhiteSpace();
        Read(_paths.DatabaseFile).Should().Be("the new database");
        Read(_paths.ConfigFile).Should().Be("the new config");
        Read(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix).Should().Be("the old database");
        Read(_paths.DatabaseFile + "-wal" + PendingRestore.PreRestoreSuffix).Should().Be("the old write-ahead log");
        Read(_paths.DatabaseFile + "-shm" + PendingRestore.PreRestoreSuffix).Should().Be("the old shared memory");
        Read(_paths.ConfigFile + PendingRestore.PreRestoreSuffix).Should().Be("the old config");
        Directory.Exists(RestoreFolder).Should().BeFalse("the restore folder is removed once it is applied");
    }

    [Fact]
    public void A_fresh_install_without_a_staged_restore_is_left_alone()
    {
        Write(_paths.DatabaseFile, "the old database");
        Write(_paths.ConfigFile, "the old config");

        var applied = PendingRestore.ApplyIfStaged(_paths, out var message);

        applied.Should().BeFalse();
        message.Should().BeNull();
        Read(_paths.DatabaseFile).Should().Be("the old database");
        Read(_paths.ConfigFile).Should().Be("the old config");
    }

    [Fact]
    public void A_restore_folder_with_only_one_of_the_two_files_is_ignored()
    {
        Write(_paths.DatabaseFile, "the old database");
        Write(_paths.ConfigFile, "the old config");
        Directory.CreateDirectory(RestoreFolder);
        Write(Path.Combine(RestoreFolder, BackupService.DatabaseEntryName), "the new database");

        var applied = PendingRestore.ApplyIfStaged(_paths, out var message);

        applied.Should().BeFalse();
        message.Should().BeNull();
        Read(_paths.DatabaseFile).Should().Be("the old database");
        Read(_paths.ConfigFile).Should().Be("the old config");
        File.Exists(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix).Should().BeFalse();
    }

    [Fact]
    public void A_restore_that_fails_half_way_is_rolled_back_completely()
    {
        Write(_paths.DatabaseFile, "the old database");
        Write(_paths.DatabaseFile + "-wal", "the old write-ahead log");
        Write(_paths.ConfigFile, "the old config");
        Stage("the new database", "the new config");

        // The config file cannot be moved aside, because its *.pre-restore name is taken by a
        // directory — after the database and its WAL have already been moved aside.
        Directory.CreateDirectory(_paths.ConfigFile + PendingRestore.PreRestoreSuffix);

        var applied = PendingRestore.ApplyIfStaged(_paths, out var message);

        applied.Should().BeFalse();
        message.Should().NotBeNullOrWhiteSpace();

        // Everything is back the way it was: the live files, and no half-moved leftovers.
        Read(_paths.DatabaseFile).Should().Be("the old database");
        Read(_paths.DatabaseFile + "-wal").Should().Be("the old write-ahead log");
        Read(_paths.ConfigFile).Should().Be("the old config");
        File.Exists(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix).Should().BeFalse();

        // The restore folder is left for the user to inspect, with both staged files still in it.
        Directory.Exists(RestoreFolder).Should().BeTrue();
        Read(Path.Combine(RestoreFolder, BackupService.DatabaseEntryName)).Should().Be("the new database");
        Read(Path.Combine(RestoreFolder, BackupService.ConfigEntryName)).Should().Be("the new config");
    }

    [Fact]
    public void A_staged_restore_into_a_fresh_install_leaves_no_pre_restore_files()
    {
        Stage("the new database", "the new config");

        var applied = PendingRestore.ApplyIfStaged(_paths, out var message);

        applied.Should().BeTrue();
        message.Should().NotBeNullOrWhiteSpace();
        Read(_paths.DatabaseFile).Should().Be("the new database");
        Read(_paths.ConfigFile).Should().Be("the new config");
        File.Exists(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix).Should().BeFalse();
        File.Exists(_paths.ConfigFile + PendingRestore.PreRestoreSuffix).Should().BeFalse();
    }

    [Fact]
    public void Three_generations_of_replaced_files_are_kept_and_the_oldest_is_deleted()
    {
        Write(_paths.DatabaseFile, "database 0");
        Write(_paths.ConfigFile, "config 0");

        for (var generation = 1; generation <= 4; generation++)
        {
            Stage($"database {generation}", $"config {generation}");
            PendingRestore.ApplyIfStaged(_paths, out _).Should().BeTrue();
        }

        var first = _paths.DatabaseFile + PendingRestore.PreRestoreSuffix;

        Read(_paths.DatabaseFile).Should().Be("database 4");
        Read(first).Should().Be("database 3");
        Read(first + ".1").Should().Be("database 2");
        Read(first + ".2").Should().Be("database 1");
        File.Exists(first + ".3").Should().BeFalse();
        Read(_paths.ConfigFile + PendingRestore.PreRestoreSuffix + ".2").Should().Be("config 1");
        Directory.GetFiles(_root, "*.discard").Should().BeEmpty("the generation that fell off the end is gone");
        File.Exists(Path.Combine(_root, PendingRestore.JournalFileName)).Should().BeFalse();
    }

    [Fact]
    public void A_failed_restore_puts_the_older_generations_back_where_they_were()
    {
        Write(_paths.DatabaseFile, "database now");
        Write(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix, "database -1");
        Write(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix + ".1", "database -2");
        Write(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix + ".2", "database -3");
        Write(_paths.ConfigFile, "config now");
        Stage("the new database", "the new config");
        Directory.CreateDirectory(_paths.ConfigFile + PendingRestore.PreRestoreSuffix);

        PendingRestore.ApplyIfStaged(_paths, out _).Should().BeFalse();

        var first = _paths.DatabaseFile + PendingRestore.PreRestoreSuffix;
        Read(_paths.DatabaseFile).Should().Be("database now");
        Read(first).Should().Be("database -1");
        Read(first + ".1").Should().Be("database -2");
        Read(first + ".2").Should().Be("database -3");
    }

    [Fact]
    public void A_restore_does_not_carry_a_wal_file_aside_because_it_checkpoints_first()
    {
        var source = Path.Combine(_root, "source.db");

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={source};Pooling=False"))
        {
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE marker (value TEXT); INSERT INTO marker VALUES ('kept');";
            command.ExecuteNonQuery();

            // What a crashed process leaves: the database file and a WAL holding the only copy of the write.
            CopyOpenFile(source, _paths.DatabaseFile);
            CopyOpenFile(source + "-wal", _paths.DatabaseFile + "-wal");
        }

        new FileInfo(_paths.DatabaseFile + "-wal").Length.Should().BeGreaterThan(0);
        Write(_paths.ConfigFile, "the old config");
        Stage("the new database", "the new config");

        PendingRestore.ApplyIfStaged(_paths, out _).Should().BeTrue();

        var aside = _paths.DatabaseFile + PendingRestore.PreRestoreSuffix;
        File.Exists(_paths.DatabaseFile + "-wal" + PendingRestore.PreRestoreSuffix).Should().BeFalse();

        // The set-aside file is complete on its own: it holds the write that was only in the WAL.
        using var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={aside};Pooling=False;Mode=ReadOnly");
        check.Open();
        using var select = check.CreateCommand();
        select.CommandText = "SELECT value FROM marker;";
        select.ExecuteScalar().Should().Be("kept");
    }

    [Fact]
    public void A_journal_with_the_database_installed_and_the_config_still_staged_is_finished_at_startup()
    {
        // The crash fell between the two installs: the new database is live, the new config.yml is
        // still in restore/, and the old config sits under its pre-restore name.
        Write(_paths.DatabaseFile, "the new database");
        Write(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix, "the old database");
        Write(_paths.ConfigFile + PendingRestore.PreRestoreSuffix, "the old config");
        Directory.CreateDirectory(RestoreFolder);
        Write(Path.Combine(RestoreFolder, BackupService.ConfigEntryName), "the new config");
        Write(Path.Combine(_root, PendingRestore.JournalFileName), "journal");

        var applied = PendingRestore.ApplyIfStaged(_paths, out var message);

        applied.Should().BeTrue();
        message.Should().Contain("finished");
        Read(_paths.DatabaseFile).Should().Be("the new database");
        Read(_paths.ConfigFile).Should().Be("the new config");
        Read(_paths.ConfigFile + PendingRestore.PreRestoreSuffix).Should().Be("the old config");
        Directory.Exists(RestoreFolder).Should().BeFalse();
        File.Exists(Path.Combine(_root, PendingRestore.JournalFileName)).Should().BeFalse();
    }

    [Fact]
    public void A_journal_with_both_files_still_staged_starts_the_restore_again()
    {
        // The crash fell while the live files were being set aside: the database is aside, the
        // config.yml is not yet.
        Write(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix, "the old database");
        Write(_paths.ConfigFile, "the old config");
        Stage("the new database", "the new config");
        Write(Path.Combine(_root, PendingRestore.JournalFileName), "journal");

        var applied = PendingRestore.ApplyIfStaged(_paths, out _);

        applied.Should().BeTrue();
        Read(_paths.DatabaseFile).Should().Be("the new database");
        Read(_paths.ConfigFile).Should().Be("the new config");
        Read(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix).Should().Be("the old database");
        Read(_paths.ConfigFile + PendingRestore.PreRestoreSuffix).Should().Be("the old config");
        File.Exists(Path.Combine(_root, PendingRestore.JournalFileName)).Should().BeFalse();
    }

    [Fact]
    public void A_second_attempt_that_fails_after_a_crash_leaves_the_live_files_in_place()
    {
        // The crash set the database aside; the second attempt then fails on the config.
        Write(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix, "the live database");
        Write(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix + ".1", "an older database");
        Write(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix + ".2.discard", "the oldest database");
        Write(_paths.ConfigFile, "the live config");
        Stage("the new database", "the new config");
        Write(Path.Combine(_root, PendingRestore.JournalFileName), "journal");
        Directory.CreateDirectory(_paths.ConfigFile + PendingRestore.PreRestoreSuffix);

        var applied = PendingRestore.ApplyIfStaged(_paths, out var message);

        applied.Should().BeFalse();
        message.Should().Contain("rolled back");
        Read(_paths.DatabaseFile).Should().Be("the live database");
        Read(_paths.ConfigFile).Should().Be("the live config");

        // The crashed attempt's rotation is undone too: the generations are as they were before it.
        Read(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix).Should().Be("an older database");
        Read(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix + ".2").Should().Be("the oldest database");
        Directory.GetFiles(_root, "*.discard").Should().BeEmpty();
        Directory.Exists(RestoreFolder).Should().BeTrue();
        File.Exists(Path.Combine(_root, PendingRestore.JournalFileName)).Should().BeFalse("the rollback completed, so nothing is left to recover");
    }

    [Fact]
    public void A_stale_discard_from_a_crashed_attempt_is_deleted_when_the_restore_is_applied()
    {
        Write(_paths.DatabaseFile, "the old database");
        Write(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix + ".2.discard", "stale");
        Stage("the new database", "the new config");

        PendingRestore.ApplyIfStaged(_paths, out _).Should().BeTrue();

        Directory.GetFiles(_root, "*.discard").Should().BeEmpty();
    }

    [Fact]
    public void A_journal_that_leaves_no_database_anywhere_puts_the_old_files_back()
    {
        Write(_paths.DatabaseFile + PendingRestore.PreRestoreSuffix, "the old database");
        Write(_paths.ConfigFile + PendingRestore.PreRestoreSuffix, "the old config");
        Write(Path.Combine(_root, PendingRestore.JournalFileName), "journal");

        var applied = PendingRestore.ApplyIfStaged(_paths, out var message);

        applied.Should().BeFalse();
        message.Should().Contain("rolled back");
        Read(_paths.DatabaseFile).Should().Be("the old database");
        Read(_paths.ConfigFile).Should().Be("the old config");
        File.Exists(Path.Combine(_root, PendingRestore.JournalFileName)).Should().BeFalse();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The temp folder is cleaned by the operating system anyway.
        }
    }

    private static void CopyOpenFile(string from, string to)
    {
        using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = File.Create(to);
        input.CopyTo(output);
    }

    private string RestoreFolder => Path.Combine(_paths.ConfigDir, PendingRestore.RestoreFolderName);

    private void Stage(string database, string config)
    {
        Directory.CreateDirectory(RestoreFolder);
        Write(Path.Combine(RestoreFolder, BackupService.DatabaseEntryName), database);
        Write(Path.Combine(RestoreFolder, BackupService.ConfigEntryName), config);
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Read(string path) => File.ReadAllText(path);
}
