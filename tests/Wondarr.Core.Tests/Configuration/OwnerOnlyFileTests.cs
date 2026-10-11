using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Configuration;
using Xunit;

namespace Wondarr.Core.Tests.Configuration;

/// <summary>
/// R-054: every file that holds a secret is 0600 whatever <c>UMASK</c> is. Unix modes do not exist
/// on Windows, so these tests return early there and run on the Linux CI runner.
/// </summary>
public sealed class OwnerOnlyFileTests
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode Group = OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead;

    [Fact]
    public void A_file_is_created_owner_only_even_under_a_permissive_umask()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryConfigDirectory();
        var path = Path.Combine(directory.Paths.ConfigDir, "secret.bin");

        using (new Umask(0))
        {
            using var stream = OwnerOnlyFile.Create(path);
            stream.WriteByte(1);
        }

        File.GetUnixFileMode(path).Should().Be(OwnerOnly);
    }

    [Fact]
    public void A_file_is_already_0600_the_moment_it_is_opened_before_anything_is_written()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryConfigDirectory();
        var path = Path.Combine(directory.Paths.ConfigDir, "secret.bin");

        using (new Umask(0))
        {
            using var stream = OwnerOnlyFile.Create(path, FileMode.CreateNew);

            // Nothing has been written, and the mode is already final: there was never a wider one.
            stream.Length.Should().Be(0);
            File.GetUnixFileMode(path).Should().Be(OwnerOnly);
        }
    }

    [Fact]
    public void The_first_run_config_yml_is_0600_under_umask_002()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryConfigDirectory();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        using (new Umask(0x2))
        {
            ConfigFileInitializer.EnsureInitialized(directory.Paths, configuration);
        }

        File.GetUnixFileMode(directory.Paths.ConfigFile).Should().Be(OwnerOnly);

        // The database is created empty and owner-only here, before any hosted service can open it.
        File.GetUnixFileMode(directory.Paths.DatabaseFile).Should().Be(OwnerOnly);
        Directory.GetFiles(directory.Paths.ConfigDir, "*.tmp").Should().BeEmpty("the first write is atomic, through a temporary file that is moved into place");
    }

    [Fact]
    public void Tighten_changes_a_0664_file_to_0600_once()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryConfigDirectory();
        var path = Path.Combine(directory.Paths.ConfigDir, "x");
        File.WriteAllText(path, "x");
        File.SetUnixFileMode(path, Group);

        OwnerOnlyFile.Tighten(path).Should().BeTrue();
        OwnerOnlyFile.Tighten(path).Should().BeFalse("it is already 0600");
        File.GetUnixFileMode(path).Should().Be(OwnerOnly);
    }

    [Fact]
    public void The_startup_pass_tightens_a_0664_database_and_every_other_file_that_holds_secrets()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryConfigDirectory();
        var paths = directory.Paths;
        Directory.CreateDirectory(paths.SlskdDir);
        Directory.CreateDirectory(Path.Combine(paths.BackupsDir, "manual"));

        var zip = Path.Combine(paths.BackupsDir, "manual", "wondarr_backup.zip");
        var logLine = Path.Combine(paths.LogsDir, "wondarr.log");
        Directory.CreateDirectory(paths.LogsDir);

        string[] secretFiles =
        [
            paths.ConfigFile,
            paths.DatabaseFile,
            paths.DatabaseFile + "-wal",
            paths.DatabaseFile + "-shm",
            Path.Combine(paths.SlskdDir, "slskd.yml"),
            zip,
            paths.ConfigFile + ".pre-restore",
            paths.DatabaseFile + ".pre-restore.1",
            paths.DatabaseFile + "-wal.pre-restore.2",
        ];

        foreach (var file in secretFiles.Append(logLine))
        {
            File.WriteAllText(file, "x");
            File.SetUnixFileMode(file, Group);
        }

        var stale = Path.Combine(paths.SlskdDir, "slskd.yml.0123456789abcdef.tmp");
        File.WriteAllText(stale, "password");

        var changed = SecretFileModes.TightenExisting(paths, NullLogger.Instance);

        changed.Should().Be(secretFiles.Length);

        foreach (var file in secretFiles)
        {
            File.GetUnixFileMode(file).Should().Be(OwnerOnly, file);
        }

        File.GetUnixFileMode(logLine).Should().Be(Group, "logs and everything else still follow UMASK");
        File.Exists(stale).Should().BeFalse("an interrupted slskd.yml write leaves the password behind");

        SecretFileModes.TightenExisting(paths, NullLogger.Instance).Should().Be(0, "there is nothing left to change");
    }

    [Fact]
    public void A_missing_database_is_created_empty_and_owner_only_so_sqlites_own_files_follow()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryConfigDirectory();

        using (new Umask(0x2))
        {
            SecretFileModes.PrepareDatabase(directory.Paths.DatabaseFile);
        }

        File.GetUnixFileMode(directory.Paths.DatabaseFile).Should().Be(OwnerOnly);
        new FileInfo(directory.Paths.DatabaseFile).Length.Should().Be(0);

        // SQLite creates -wal and -shm with the mode of the main file: opening it must leave all three 0600.
        using (new Umask(0x2))
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={directory.Paths.DatabaseFile};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (1);";
            command.ExecuteNonQuery();

            foreach (var file in new[] { directory.Paths.DatabaseFile, directory.Paths.DatabaseFile + "-wal", directory.Paths.DatabaseFile + "-shm" })
            {
                File.GetUnixFileMode(file).Should().Be(OwnerOnly, file);
            }
        }
    }

    [Fact]
    public void An_existing_database_is_left_for_the_startup_pass()
    {
        using var directory = new TemporaryConfigDirectory();
        File.WriteAllText(directory.Paths.DatabaseFile, "existing");

        SecretFileModes.PrepareDatabase(directory.Paths.DatabaseFile);

        File.ReadAllText(directory.Paths.DatabaseFile).Should().Be("existing");
    }
}
