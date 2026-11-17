using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Discover;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Media;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Core.Tests.Songs;
using Xunit;

namespace Wondarr.Core.Tests.Discover;

/// <summary>How Discover behaves when sources are slow or failing: stop early, a deadline, short caching, cache keys.</summary>
public sealed class DiscoverResilienceTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task A_source_that_fails_on_the_first_seed_is_not_asked_about_the_rest()
    {
        await using var context = await OpenAsync();
        await AddSongsAsync(context, 8);

        var broken = new Source("lastfm", _ => RecommendationSourceResult.Failed);
        var working = new Source("deezer", _ => Answer(new RecommendationCandidate("Fine", "Somebody")));

        var result = await Service(context, sources: [broken, working]).GetMixAsync(1, 50, CancellationToken.None);

        result.Seeds.Should().HaveCount(5);
        broken.Calls.Should().Be(1);
        working.Calls.Should().Be(5);
        result.Sources.Select(source => (source.Name, source.Status)).Should().Equal(("lastfm", "failed"), ("deezer", "answered"));
    }

    [Fact]
    public async Task A_source_that_never_answers_ends_at_the_deadline_and_the_others_still_count()
    {
        await using var context = await OpenAsync();
        await AddSongsAsync(context, 3);

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        var hanging = new Source("listenbrainz", answer: null);
        var working = new Source("deezer", _ => Answer(new RecommendationCandidate("Fine", "Somebody")));

        var pending = Service(context, time: time, sources: [hanging, working]).GetMixAsync(1, 50, CancellationToken.None);

        while (!pending.IsCompleted)
        {
            time.Advance(DiscoverService.RequestDeadline);
            await Task.Delay(10);
        }

        var result = await pending;

        result.Sources.Select(source => (source.Name, source.Status)).Should().Equal(("listenbrainz", "failed"), ("deezer", "answered"));
        result.Suggestions.Select(item => item.Title).Should().Equal("Fine");
    }

    [Fact]
    public async Task The_callers_own_cancellation_still_propagates()
    {
        await using var context = await OpenAsync();
        await AddSongsAsync(context, 3);

        using var cancel = new CancellationTokenSource();
        var hanging = new Source("listenbrainz", answer: null);

        var pending = Service(context, sources: [hanging]).GetMixAsync(1, 50, cancel.Token);
        await cancel.CancelAsync();

        await FluentActions.Awaiting(() => pending).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_partial_failure_is_cached_for_five_minutes_a_clean_run_for_the_configured_hours_and_a_total_failure_not_at_all()
    {
        await using var context = await OpenAsync();
        await AddSongsAsync(context, 1);
        var songId = (await context.Songs.FirstAsync()).Id;

        var partial = await CacheLifetimeAsync(context, songId, new Source("a", _ => Answer(new RecommendationCandidate("T", "A"))), new Source("b", _ => RecommendationSourceResult.Failed));
        partial.Should().Be(DiscoverService.PartialCacheTime);

        var clean = await CacheLifetimeAsync(context, songId, new Source("a", _ => Answer(new RecommendationCandidate("T", "A"))));
        clean.Should().Be(TimeSpan.FromHours(6));

        var failed = await CacheLifetimeAsync(context, songId, new Source("b", _ => RecommendationSourceResult.Failed));
        failed.Should().BeNull();
    }

    [Fact]
    public async Task A_different_algorithm_or_seed_identity_is_a_different_cache_entry()
    {
        await using var context = await OpenAsync();
        await AddSongsAsync(context, 1);
        var song = await context.Songs.FirstAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var source = new Source("a", _ => Answer());

        await Service(context, cache, sources: [source]).GetRadioAsync(song.Id, 50, CancellationToken.None);
        await Service(context, cache, sources: [source]).GetRadioAsync(song.Id, 50, CancellationToken.None);
        source.Calls.Should().Be(1);

        await Service(context, cache, new DiscoverOptions { ListenBrainzAlgorithm = "another" }, sources: [source])
            .GetRadioAsync(song.Id, 50, CancellationToken.None);
        source.Calls.Should().Be(2, "a new algorithm name must not be served the old answer");

        song.DeezerId = 99;
        await context.SaveChangesAsync();
        await Service(context, cache, sources: [source]).GetRadioAsync(song.Id, 50, CancellationToken.None);
        source.Calls.Should().Be(3, "a seed that gained an id is a different seed");
    }

    [Fact]
    public async Task The_database_keeps_one_dismissal_per_recording_and_per_artist_and_title()
    {
        await using var context = await OpenAsync();
        context.DiscoverDismissals.Add(Row("11111111-1111-1111-1111-111111111111", "a", "t"));
        await context.SaveChangesAsync();

        await using var sameMbid = _database.CreateContext(TimeProvider.System);
        sameMbid.DiscoverDismissals.Add(Row("11111111-1111-1111-1111-111111111111", "b", "u"));
        await sameMbid.Invoking(db => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();

        await using var sameText = _database.CreateContext(TimeProvider.System);
        sameText.DiscoverDismissals.Add(Row(null, "a", "t"));
        await sameText.Invoking(db => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();

        await using var twoWithoutMbid = _database.CreateContext(TimeProvider.System);
        twoWithoutMbid.DiscoverDismissals.AddRange(Row(null, "c", "v"), Row(null, "d", "w"));
        (await twoWithoutMbid.SaveChangesAsync()).Should().Be(2, "rows without an MBID do not collide with each other");
    }

    [Fact]
    public void The_import_list_client_reads_listenbrainz_under_the_configured_base_url_plus_1()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Metadata:ListenBrainzBaseUrl"] = "http://h/lb" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWondarrMetadata(configuration);

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(Wondarr.Core.Metadata.ServiceCollectionExtensions.ListenBrainzClientName);

        client.BaseAddress.Should().Be(new Uri("http://h/lb/1/"));
    }

    private static DiscoverDismissal Row(string? mbid, string artist, string title) =>
        new() { MbRecordingId = mbid, Artist = artist, Title = title, ArtistKey = artist, TitleKey = title };

    private static RecommendationSourceResult Answer(params RecommendationCandidate[] candidates) =>
        RecommendationSourceResult.Answered(candidates);

    private static async Task<TimeSpan?> CacheLifetimeAsync(WondarrDbContext context, long songId, params IRecommendationSource[] sources)
    {
        var entry = Substitute.For<ICacheEntry>();
        var cache = Substitute.For<IMemoryCache>();
        cache.CreateEntry(Arg.Any<object>()).Returns(entry);

        await Service(context, cache, sources: sources).GetRadioAsync(songId, 50, CancellationToken.None);

        return cache.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(IMemoryCache.CreateEntry))
            ? entry.AbsoluteExpirationRelativeToNow
            : null;
    }

    private async Task<WondarrDbContext> OpenAsync()
    {
        await _database.MigrateAsync(TimeProvider.System);

        return _database.CreateContext(TimeProvider.System);
    }

    private static async Task AddSongsAsync(WondarrDbContext context, int count)
    {
        for (var index = 0; index < count; index++)
        {
            await SongSeed.AddAsync(context, $"Song {index}", $"Artist {index}");
        }
    }

    private static DiscoverService Service(
        WondarrDbContext context,
        IMemoryCache? cache = null,
        DiscoverOptions? options = null,
        TimeProvider? time = null,
        params IRecommendationSource[] sources) =>
        new(
            context,
            sources,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            new TestOptionsMonitor<DiscoverOptions>(options ?? new DiscoverOptions()),
            NullLogger<DiscoverService>.Instance,
            time);

    /// <summary>A scripted source; with no script it waits for ever, honouring only cancellation.</summary>
    private sealed class Source(string name, Func<RecommendationSeed, RecommendationSourceResult>? answer) : IRecommendationSource
    {
        public int Calls { get; private set; }

        public string Name => name;

        public double Weight => 1.0;

        public async Task<RecommendationSourceResult> GetAsync(RecommendationSeed seed, CancellationToken cancellationToken)
        {
            Calls++;

            if (answer is null)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return answer!(seed);
        }
    }
}
