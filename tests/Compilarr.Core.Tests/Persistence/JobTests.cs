using Compilarr.Core.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Compilarr.Core.Tests.Persistence;

public class JobTests
{
    [Fact]
    public async Task Inserting_two_jobs_with_the_same_name_fails()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);

        context.Jobs.Add(new Job { Name = "wanted-search", Interval = TimeSpan.FromHours(6) });
        await context.SaveChangesAsync(CancellationToken.None);

        context.Jobs.Add(new Job { Name = "wanted-search" });
        var act = async () => await context.SaveChangesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task SaveChanges_stamps_CreatedAt_and_UpdatedAt_from_the_TimeProvider()
    {
        using var database = new SqliteTestDatabase();
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(start);
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var job = new Job { Name = "queue-poll" };
        context.Jobs.Add(job);
        await context.SaveChangesAsync(CancellationToken.None);

        job.CreatedAt.Should().Be(start.UtcDateTime);
        job.UpdatedAt.Should().Be(start.UtcDateTime);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        job.LastResult = "ok";
        await context.SaveChangesAsync(CancellationToken.None);

        job.CreatedAt.Should().Be(start.UtcDateTime);
        job.UpdatedAt.Should().Be(start.UtcDateTime.AddMinutes(1));
    }

    [Fact]
    public async Task Timestamps_read_back_from_the_database_are_utc()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await database.MigrateAsync(timeProvider);

        await using (var context = database.CreateContext(timeProvider))
        {
            context.Jobs.Add(new Job { Name = "heartbeat", LastRunAt = timeProvider.GetUtcNow().UtcDateTime });
            await context.SaveChangesAsync(CancellationToken.None);
        }

        await using var fresh = database.CreateContext(timeProvider);
        var job = await fresh.Jobs.SingleAsync(CancellationToken.None);

        job.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        job.LastRunAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
        job.LastRunAt.Should().Be(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
    }
}
