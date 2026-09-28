using Wondarr.Core.HealthCheck;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.HealthCheck;

public class DatabaseHealthCheckTests
{
    [Fact]
    public async Task Reports_ok_with_the_sqlite_version_when_the_database_is_migrated()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);

        var result = await new DatabaseHealthCheck(context).CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Ok);
        result.Message.Should().StartWith("Database OK (SQLite ");
        result.Message.Should().NotContain("pending");
    }

    [Fact]
    public async Task Reports_an_error_when_migrations_are_pending()
    {
        using var database = new SqliteTestDatabase();
        await using var context = database.CreateContext(new FakeTimeProvider());

        var result = await new DatabaseHealthCheck(context).CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().Contain("pending migration");
    }

    [Fact]
    public async Task Reports_an_error_when_the_database_cannot_be_opened()
    {
        // A parent directory that does not exist: SQLite cannot create the file there.
        var missingDirectory = Path.Combine(
            Path.GetTempPath(),
            "wondarr-tests",
            Guid.NewGuid().ToString("N"),
            "missing");

        var options = new DbContextOptionsBuilder<WondarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(missingDirectory, "wondarr.db")}")
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var context = new WondarrDbContext(options, new FakeTimeProvider());

        var result = await new DatabaseHealthCheck(context).CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().StartWith("Database is not available: ");
    }
}
