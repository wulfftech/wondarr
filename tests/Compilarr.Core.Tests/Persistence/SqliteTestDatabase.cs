using Compilarr.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Compilarr.Core.Tests.Persistence;

/// <summary>
/// A real SQLite file in a per-test temp directory. WAL needs a file, so an in-memory database will not do.
/// </summary>
internal sealed class SqliteTestDatabase : IDisposable
{
    private readonly string _directory;

    public SqliteTestDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "compilarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        FilePath = Path.Combine(_directory, "compilarr.db");
    }

    public string FilePath { get; }

    public CompilarrDbContext CreateContext(TimeProvider timeProvider) =>
        new(CreateOptions(), timeProvider);

    /// <summary>Runs the migrator once, as startup does.</summary>
    public async Task MigrateAsync(TimeProvider timeProvider)
    {
        await using var context = CreateContext(timeProvider);
        var migrator = new DatabaseMigrator(context, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(CancellationToken.None);
    }

    /// <summary>Reads the first column of every row returned by <paramref name="sql"/> on a fresh connection.</summary>
    public async Task<List<string>> ReadStringsAsync(string sql)
    {
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={FilePath}"));

        await using var connection = new SqliteConnection($"Data Source={FilePath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    public void Dispose()
    {
        // Windows keeps the file handle until the pooled connections are gone.
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={FilePath}"));

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private DbContextOptions<CompilarrDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<CompilarrDbContext>()
            .UseSqlite($"Data Source={FilePath}")
            .UseSnakeCaseNamingConvention()
            .Options;
}
