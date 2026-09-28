using Compilarr.Core.Metadata;
using Compilarr.Core.Persistence;
using Compilarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Compilarr.Core.Tests.Metadata;

/// <summary>
/// The cache against real SQLite: the upsert has to survive concurrent writers, and expiry has to
/// come from <see cref="TimeProvider"/> rather than the wall clock.
/// </summary>
public sealed class MetadataCacheTests
{
    private const string Provider = "musicbrainz";
    private const string Key = "recording/b1a9c0e9-d987-4042-ae91-78d6a3267d69?inc=artist-credits+isrcs&fmt=json";

    [Fact]
    public async Task Set_then_get_returns_the_payload()
    {
        using var database = new SqliteTestDatabase();
        var time = Start();
        await database.MigrateAsync(time);
        await using var provider = BuildProvider(database.FilePath, time);

        var cache = provider.GetRequiredService<IMetadataCache>();

        await cache.SetAsync(Provider, Key, "{\"id\":\"x\"}", TimeSpan.FromDays(1), CancellationToken.None);

        (await cache.GetAsync(Provider, Key, CancellationToken.None)).Should().Be("{\"id\":\"x\"}");
        (await cache.GetAsync(Provider, "another-key", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Set_replaces_the_payload_and_keeps_a_single_row()
    {
        using var database = new SqliteTestDatabase();
        var time = Start();
        await database.MigrateAsync(time);
        await using var provider = BuildProvider(database.FilePath, time);

        var cache = provider.GetRequiredService<IMetadataCache>();

        await cache.SetAsync(Provider, Key, "first", TimeSpan.FromDays(1), CancellationToken.None);
        await cache.SetAsync(Provider, Key, "second", TimeSpan.FromDays(1), CancellationToken.None);

        (await cache.GetAsync(Provider, Key, CancellationToken.None)).Should().Be("second");
        (await database.ReadStringsAsync("SELECT payload FROM metadata_cache")).Should().Equal("second");
    }

    [Fact]
    public async Task An_expired_entry_reads_as_a_miss()
    {
        using var database = new SqliteTestDatabase();
        var time = Start();
        await database.MigrateAsync(time);
        await using var provider = BuildProvider(database.FilePath, time);

        var cache = provider.GetRequiredService<IMetadataCache>();

        await cache.SetAsync(Provider, Key, "payload", TimeSpan.FromMinutes(1), CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(2));

        (await cache.GetAsync(Provider, Key, CancellationToken.None)).Should().BeNull();
        (await database.ReadStringsAsync("SELECT payload FROM metadata_cache")).Should().Equal("payload");
    }

    [Fact]
    public async Task Purge_deletes_only_the_expired_rows()
    {
        using var database = new SqliteTestDatabase();
        var time = Start();
        await database.MigrateAsync(time);
        await using var provider = BuildProvider(database.FilePath, time);

        var cache = provider.GetRequiredService<IMetadataCache>();

        await cache.SetAsync(Provider, "fresh", "a", TimeSpan.FromHours(1), CancellationToken.None);
        await cache.SetAsync(Provider, "stale", "b", TimeSpan.FromMinutes(1), CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(2));

        (await cache.PurgeExpiredAsync(CancellationToken.None)).Should().Be(1);
        (await database.ReadStringsAsync("SELECT key FROM metadata_cache")).Should().Equal("fresh");
    }

    [Fact]
    public async Task Twenty_concurrent_writers_of_one_key_leave_exactly_one_row()
    {
        using var database = new SqliteTestDatabase();
        var time = Start();
        await database.MigrateAsync(time);
        await using var provider = BuildProvider(database.FilePath, time);

        var cache = provider.GetRequiredService<IMetadataCache>();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => cache.SetAsync(
            Provider,
            Key,
            $"payload-{index}",
            TimeSpan.FromDays(1),
            CancellationToken.None)));

        (await database.ReadStringsAsync("SELECT payload FROM metadata_cache"))
            .Should().ContainSingle()
            .Which.Should().StartWith("payload-");
    }

    private static FakeTimeProvider Start() =>
        new(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));

    private static ServiceProvider BuildProvider(string filePath, TimeProvider time)
    {
        var services = new ServiceCollection();

        services.AddSingleton(time);
        services.AddLogging();
        services.AddDbContext<CompilarrDbContext>(options => options
            .UseSqlite($"Data Source={filePath}")
            .UseSnakeCaseNamingConvention());
        services.AddSingleton<IMetadataCache, MetadataCache>();

        return services.BuildServiceProvider();
    }
}