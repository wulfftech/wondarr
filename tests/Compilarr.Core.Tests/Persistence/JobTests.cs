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
}