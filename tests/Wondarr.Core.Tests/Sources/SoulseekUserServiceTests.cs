using Wondarr.Core.Decisions;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Sources;

public sealed class SoulseekUserServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Reputation_is_recorded_case_insensitively_and_counts_the_last_day()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        var service = new SoulseekUserService(database.CreateContext(time), time);
        var token = CancellationToken.None;

        await service.RecordSuccessAsync("Alice", token);
        await service.RecordSuccessAsync("alice", token);
        await service.RecordFailureAsync("ALICE", token);
        await service.RecordFailureAsync("alice", token);

        var reputation = await service.GetReputationAsync(["aLiCe", "Nobody"], token);
        reputation.Should().ContainKey("alice").WhoseValue.Should().Be(new UserReputation(2, 2, 2));
        reputation.Should().NotContainKey("Nobody");

        // Four spellings, one row: the username column is NOCASE and unique.
        await using (var context = database.CreateContext(time))
        {
            (await context.SoulseekUsers.CountAsync(token)).Should().Be(1);
        }

        // A failure 25 hours later is inside the window; the two from yesterday are not, but still count.
        time.Advance(TimeSpan.FromHours(25));
        await service.RecordFailureAsync("alice", token);

        (await service.GetReputationAsync(["alice"], token))["alice"]
            .Should().Be(new UserReputation(2, 3, 1));
    }

    [Fact]
    public async Task Only_the_newest_ten_failures_are_kept()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        var service = new SoulseekUserService(database.CreateContext(time), time);
        var token = CancellationToken.None;

        for (var attempt = 0; attempt < 12; attempt++)
        {
            await service.RecordFailureAsync("Bob", token);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        await using var context = database.CreateContext(time);
        var user = await context.SoulseekUsers.AsNoTracking().SingleAsync(token);

        user.Failures.Should().Be(12);
        user.RecentFailures.Should().HaveCount(10);
        user.RecentFailures.Should().BeInAscendingOrder();
        user.RecentFailures[0].Should().Be(Now.AddMinutes(2));
        user.RecentFailures[^1].Should().Be(Now.AddMinutes(11));
        user.RecentFailures.Should().OnlyContain(instant => instant.Kind == DateTimeKind.Utc);
    }

    [Fact]
    public async Task The_ignore_list_is_case_insensitive_and_keeps_the_reason()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        var service = new SoulseekUserService(database.CreateContext(time), time);
        var token = CancellationToken.None;

        await service.SetIgnoredAsync("Bob", true, "keeps stalling", token);

        var ignored = await service.GetIgnoredAsync(token);
        ignored.Should().HaveCount(1);
        ignored.Should().Contain("bob").And.Contain("BOB");

        await service.SetIgnoredAsync("bob", false, null, token);

        (await service.GetIgnoredAsync(token)).Should().BeEmpty();

        await using var context = database.CreateContext(time);
        var user = await context.SoulseekUsers.AsNoTracking().SingleAsync(token);
        user.Username.Should().Be("Bob");
        user.Ignored.Should().BeFalse();
        user.IgnoredReason.Should().BeNull();
    }
}
