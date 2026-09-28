using Compilarr.Core.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Compilarr.Core.Tests.Persistence;

public class SettingsRepositoryTests
{
    private sealed record ThemeSetting
    {
        public string Mode { get; init; } = string.Empty;
    }

    [Fact]
    public async Task SetAsync_then_GetAsync_round_trips_through_a_new_context()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using (var writer = database.CreateContext(timeProvider))
        {
            await new SettingsRepository(writer)
                .SetAsync("ui.theme", new { Mode = "dark" }, CancellationToken.None);
        }

        await using var reader = database.CreateContext(timeProvider);
        var value = await new SettingsRepository(reader)
            .GetAsync<ThemeSetting>("ui.theme", CancellationToken.None);

        value.Should().NotBeNull();
        value!.Mode.Should().Be("dark");
    }

    [Fact]
    public async Task SetAsync_on_an_existing_key_updates_one_row_and_moves_only_UpdatedAt()
    {
        using var database = new SqliteTestDatabase();
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(start);
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var repository = new SettingsRepository(context);

        await repository.SetAsync("ui.theme", new ThemeSetting { Mode = "dark" }, CancellationToken.None);
        var created = await context.Settings.AsNoTracking().SingleAsync();

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await repository.SetAsync("ui.theme", new ThemeSetting { Mode = "light" }, CancellationToken.None);

        var updated = await context.Settings.AsNoTracking().SingleAsync();
        context.Settings.Should().HaveCount(1);
        updated.CreatedAt.Should().Be(created.CreatedAt);
        updated.UpdatedAt.Should().Be(start.UtcDateTime.AddMinutes(5));

        var roundTripped = await repository.GetAsync<ThemeSetting>("ui.theme", CancellationToken.None);
        roundTripped!.Mode.Should().Be("light");
    }

    [Fact]
    public async Task DeleteAsync_removes_the_row_and_reports_unknown_keys()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var repository = new SettingsRepository(context);
        await repository.SetAsync("ui.theme", new ThemeSetting { Mode = "dark" }, CancellationToken.None);

        (await repository.DeleteAsync("ui.theme", CancellationToken.None)).Should().BeTrue();
        (await repository.DeleteAsync("ui.theme", CancellationToken.None)).Should().BeFalse();
        context.Settings.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_returns_default_for_a_missing_key()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var repository = new SettingsRepository(context);

        var value = await repository.GetAsync<ThemeSetting>("ui.missing", CancellationToken.None);

        value.Should().BeNull();
    }
}
