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
