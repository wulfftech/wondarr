using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wondarr.Core.Discover;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata.LastFm;
using Wondarr.Core.Metadata.ListenBrainz;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Lyrics;
using Wondarr.Core.Tests.Media;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Core.Tests.Songs;
using Xunit;

namespace Wondarr.Core.Tests.Discover;

/// <summary>Discover end to end over a real database and scripted sources: merge, filter, cap, mix sampling, caching, dismissals.</summary>
public sealed class DiscoverServiceTests : IDisposable
{
    private const string TeardropMbid = "f3bba4cd-8018-468b-902e-bc8f029593e5";

    private readonly SqliteTestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task The_same_recording_from_two_sources_is_one_suggestion_and_the_library_seed_and_dismissed_are_left_out()
    {
        await using var context = await OpenAsync();
        var seed = await AddSongAsync(context, "Angel", "Massive Attack", mbid: "11111111-1111-1111-1111-111111111111");
        await AddSongAsync(context, "Owned Already", "Portishead", mbid: "22222222-2222-2222-2222-222222222222");

        var listenBrainz = new ScriptedSource("listenbrainz", 1.0, _ => Answer(
            new RecommendationCandidate("Teardrop", "Massive Attack", "Mezzanine", TeardropMbid),
            new RecommendationCandidate("Angel", "Massive Attack", MbRecordingId: "11111111-1111-1111-1111-111111111111"),
            new RecommendationCandidate("Owned Already", "Portishead", MbRecordingId: "22222222-2222-2222-2222-222222222222"),
            new RecommendationCandidate("Dismiss Me", "Tricky"),
            new RecommendationCandidate("Glory Box", "Portishead")));

        var lastFm = new ScriptedSource("lastfm", 1.0, _ => Answer(
            new RecommendationCandidate("Teardrop", "Massive Attack", MbRecordingId: TeardropMbid),
            new RecommendationCandidate("Dismiss Me", "Tricky")));

        var service = Service(context, listenBrainz, lastFm);
        await service.DismissAsync(new DiscoverDismissalInput(null, "Tricky", "Dismiss Me"), CancellationToken.None);

        var result = await service.GetRadioAsync(seed.Id, 50, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Mode.Should().Be("radio");
        result.MixSeed.Should().BeNull();
        result.Suggestions.Select(item => item.Title).Should().Equal("Teardrop", "Glory Box");

        var teardrop = result.Suggestions[0];
        teardrop.Sources.Should().Equal("listenbrainz", "lastfm");
        teardrop.MbRecordingId.Should().Be(TeardropMbid);
        teardrop.Album.Should().Be("Mezzanine");
        teardrop.Score.Should().BeGreaterThan(result.Suggestions[1].Score);

        result.Sources.Select(source => (source.Name, source.Status)).Should().Equal(
            ("listenbrainz", "answered"),
            ("lastfm", "answered"));
    }

    [Fact]
    public async Task The_limit_truncates_after_the_filters_and_reports_the_total()
    {
        await using var context = await OpenAsync();
        var seed = await AddSongAsync(context, "Seed", "Seed Artist");

        var source = new ScriptedSource("listenbrainz", 1.0, _ => Answer(
            [.. Enumerable.Range(0, 10).Select(index => new RecommendationCandidate($"Song {index}", $"Artist {index}"))]));

        var result = await Service(context, source).GetRadioAsync(seed.Id, 4, CancellationToken.None);

        result!.Limit.Should().Be(4);
        result.TotalBeforeLimit.Should().Be(10);
        result.Suggestions.Select(item => item.Title).Should().Equal("Song 0", "Song 1", "Song 2", "Song 3");
    }

    [Fact]
    public async Task A_radio_for_a_song_that_does_not_exist_is_null()
    {
        await using var context = await OpenAsync();

        var result = await Service(context, new ScriptedSource("listenbrainz", 1.0, _ => Answer()))
            .GetRadioAsync(4242, 50, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task One_artist_holds_at_most_the_configured_number_of_suggestions()
    {
        await using var context = await OpenAsync();
        var seed = await AddSongAsync(context, "Seed", "Seed Artist");

        var source = new ScriptedSource("listenbrainz", 1.0, _ => Answer(
            new RecommendationCandidate("One", "Crowd"),
            new RecommendationCandidate("Two", "Crowd"),
            new RecommendationCandidate("Three", "Crowd"),
            new RecommendationCandidate("Four", "Crowd"),
            new RecommendationCandidate("Solo", "Loner")));

        var result = await Service(context, source).GetRadioAsync(seed.Id, 50, CancellationToken.None);

        result!.Suggestions.Select(item => item.Title).Should().Equal("One", "Two", "Three", "Solo");
    }

    [Fact]
    public async Task A_mix_is_the_same_sample_for_the_same_seed_and_the_seed_is_echoed()
    {
        await using var context = await OpenAsync();
        for (var index = 0; index < 20; index++)
        {
            await AddSongAsync(context, $"Song {index}", $"Artist {index}");
        }

        var source = new ScriptedSource("listenbrainz", 1.0, _ => Answer());
        var service = Service(context, source);

        var first = await service.GetMixAsync(1234, 50, CancellationToken.None);
        var again = await service.GetMixAsync(1234, 50, CancellationToken.None);

        first.Mode.Should().Be("mix");
        first.MixSeed.Should().Be(1234);
        first.Seeds.Should().HaveCount(5);
        again.Seeds.Select(seed => seed.SongId).Should().Equal(first.Seeds.Select(seed => seed.SongId));

        var others = new List<IReadOnlyList<long>>();
        foreach (var other in new[] { 1, 2, 3, 4, 5, 6 })
        {
            var result = await service.GetMixAsync(other, 50, CancellationToken.None);
            others.Add([.. result.Seeds.Select(seed => seed.SongId).Order()]);
        }

        others.Should().Contain(
            sample => !sample.SequenceEqual(first.Seeds.Select(seed => seed.SongId).Order()),
            "a different seed draws a different sample");

        var fresh = await service.GetMixAsync(null, 50, CancellationToken.None);
        fresh.MixSeed.Should().NotBeNull("an omitted seed draws a new sample and says which");
    }

    [Fact]
    public async Task The_sample_is_drawn_from_the_whole_library_without_repeats()
    {
        await using var context = await OpenAsync();
        for (var index = 0; index < 3; index++)
        {
            await AddSongAsync(context, $"Song {index}", $"Artist {index}");
        }

        var result = await Service(context, new ScriptedSource("listenbrainz", 1.0, _ => Answer()))
            .GetMixAsync(7, 50, CancellationToken.None);

        result.Seeds.Should().HaveCount(3, "a library smaller than the sample size is used whole");
        result.Seeds.Select(seed => seed.SongId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_listenbrainz_rejection_leaves_the_other_sources_and_logs_one_warning()
    {
        await using var context = await OpenAsync();
        var first = await AddSongAsync(context, "First", "Artist One", mbid: "33333333-3333-3333-3333-333333333333");
        var second = await AddSongAsync(context, "Second", "Artist Two", mbid: "44444444-4444-4444-4444-444444444444");

        var handler = StubHttpMessageHandler.Scripted(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"code":400,"error":"algorithm: value is not a valid enumeration member; permitted: a, b"}""",
                Encoding.UTF8,
                "application/json"),
        });

        var labs = new ListenBrainzLabsClient(new HttpClient(handler) { BaseAddress = new Uri("https://labs.example/") });
        var logger = new RecordingLogger<ListenBrainzRecommendationSource>();
        var warnings = new RecommendationWarnings();
        var options = new TestOptionsMonitor<DiscoverOptions>(new DiscoverOptions { ListenBrainzAlgorithm = "retired_algorithm" });
        var listenBrainz = new ListenBrainzRecommendationSource(labs, options, warnings, logger);

        var lastFm = new ScriptedSource("lastfm", 1.0, _ => Answer(new RecommendationCandidate("Elsewhere", "Somebody")));

        var service = Service(context, listenBrainz, lastFm);

        var one = await service.GetRadioAsync(first.Id, 50, CancellationToken.None);
        var two = await service.GetRadioAsync(second.Id, 50, CancellationToken.None);

        one!.Suggestions.Select(item => item.Title).Should().Equal("Elsewhere");
        one.Sources.Select(source => (source.Name, source.Status)).Should().Equal(
            ("listenbrainz", "failed"),
            ("lastfm", "answered"));
        two!.Suggestions.Should().ContainSingle();

        handler.Requests.Should().HaveCount(2, "each seed asked once");
        handler.Requests[0].Query.Should().Contain("algorithm=retired_algorithm");

        var warningLines = logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ToList();
        warningLines.Should().ContainSingle("the process warns once, not on every request");
        warningLines[0].Message.Should().Contain("retired_algorithm");
    }

    [Fact]
    public async Task A_source_that_throws_is_reported_failed_and_the_request_still_answers()
    {
        await using var context = await OpenAsync();
        var seed = await AddSongAsync(context, "Seed", "Seed Artist");

        var broken = new ScriptedSource("lastfm", 1.0, _ => throw new InvalidOperationException("boom"));
        var working = new ScriptedSource("deezer", 0.6, _ => Answer(new RecommendationCandidate("Fine", "Somebody")));

        var result = await Service(context, broken, working).GetRadioAsync(seed.Id, 50, CancellationToken.None);

        result!.Suggestions.Should().ContainSingle();
        result.Sources.Select(source => (source.Name, source.Status)).Should().Equal(
            ("lastfm", "failed"),
            ("deezer", "answered"));
    }

    [Fact]
    public async Task A_result_is_cached_per_seed_but_a_dismissal_shows_at_once()
    {
        await using var context = await OpenAsync();
        var seed = await AddSongAsync(context, "Seed", "Seed Artist");

        var source = new ScriptedSource("listenbrainz", 1.0, _ => Answer(
            new RecommendationCandidate("Keep", "Somebody"),
            new RecommendationCandidate("Drop", "Somebody Else")));

        var service = Service(context, source);

        var before = await service.GetRadioAsync(seed.Id, 50, CancellationToken.None);
        var dismissal = await service.DismissAsync(new DiscoverDismissalInput(null, "Somebody Else", "Drop"), CancellationToken.None);
        var after = await service.GetRadioAsync(seed.Id, 50, CancellationToken.None);

        source.Calls.Should().Be(1, "the second request is served from the cache");
        before!.Suggestions.Should().HaveCount(2);
        after!.Suggestions.Select(item => item.Title).Should().Equal("Keep");

        (await service.UndoDismissalAsync(dismissal.Id, CancellationToken.None)).Should().BeTrue();
        (await service.GetRadioAsync(seed.Id, 50, CancellationToken.None))!.Suggestions.Should().HaveCount(2);
        (await service.UndoDismissalAsync(dismissal.Id, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_request_in_which_no_source_answered_is_not_cached()
    {
        await using var context = await OpenAsync();
        var seed = await AddSongAsync(context, "Seed", "Seed Artist");

        var source = new ScriptedSource("listenbrainz", 1.0, _ => RecommendationSourceResult.Failed);
        var service = Service(context, source);

        await service.GetRadioAsync(seed.Id, 50, CancellationToken.None);
        await service.GetRadioAsync(seed.Id, 50, CancellationToken.None);

        source.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Dismissing_stores_normalised_keys_once_and_rejects_nonsense()
    {
        await using var context = await OpenAsync();
        var service = Service(context);

        var one = await service.DismissAsync(
            new DiscoverDismissalInput(TeardropMbid.ToUpperInvariant(), "The Massive Attack", "Teardrop!"),
            CancellationToken.None);
        var two = await service.DismissAsync(
            new DiscoverDismissalInput(null, "massive attack", "teardrop"),
            CancellationToken.None);

        one.MbRecordingId.Should().Be(TeardropMbid);
        one.Artist.Should().Be("The Massive Attack");
        one.Title.Should().Be("Teardrop!");
        one.ArtistKey.Should().Be("massive attack");
        one.TitleKey.Should().Be("teardrop");
        two.Id.Should().Be(one.Id, "the same song is not dismissed twice");
        (await service.ListDismissalsAsync(CancellationToken.None)).Should().ContainSingle();

        var noArtist = () => service.DismissAsync(new DiscoverDismissalInput(null, " ", "Title"), CancellationToken.None);
        await noArtist.Should().ThrowAsync<DiscoverValidationException>();

        var badMbid = () => service.DismissAsync(new DiscoverDismissalInput("nope", "A", "B"), CancellationToken.None);
        await badMbid.Should().ThrowAsync<DiscoverValidationException>();
    }

    [Fact]
    public async Task Last_fm_is_skipped_without_a_key_and_asks_nothing()
    {
        var client = Substitute.For<ILastFmClient>();
        client.IsConfigured.Returns(false);

        var result = await new LastFmRecommendationSource(client)
            .GetAsync(new RecommendationSeed(1, "Teardrop", "Massive Attack", TeardropMbid, null), CancellationToken.None);

        result.Status.Should().Be(RecommendationSourceStatus.Skipped);
        await client.DidNotReceiveWithAnyArgs().GetSimilarAsync(null, string.Empty, string.Empty, CancellationToken.None);
    }

    [Fact]
    public async Task Last_fm_answers_with_what_it_lists_when_a_key_is_set()
    {
        var client = Substitute.For<ILastFmClient>();
        client.IsConfigured.Returns(true);
        client.GetSimilarAsync(TeardropMbid, "Massive Attack", "Teardrop", Arg.Any<CancellationToken>())
            .Returns(new LastFmResult<IReadOnlyList<LastFmSimilarTrack>>(
                LastFmStatus.Ok,
                [new LastFmSimilarTrack("Portishead", "Roads", null, 0.9, null)],
                null));

        var result = await new LastFmRecommendationSource(client)
            .GetAsync(new RecommendationSeed(1, "Teardrop", "Massive Attack", TeardropMbid, null), CancellationToken.None);

        result.Status.Should().Be(RecommendationSourceStatus.Answered);
        result.Candidates.Should().ContainSingle().Which.Title.Should().Be("Roads");
    }

    private static RecommendationSourceResult Answer(params RecommendationCandidate[] candidates) =>
        RecommendationSourceResult.Answered(candidates);

    private async Task<WondarrDbContext> OpenAsync()
    {
        await _database.MigrateAsync(TimeProvider.System);

        return _database.CreateContext(TimeProvider.System);
    }

    private static async Task<Song> AddSongAsync(
        WondarrDbContext context,
        string title,
        string artist,
        string? mbid = null,
        long? deezerId = null)
    {
        var song = await SongSeed.AddAsync(context, title, artist);
        song.MbRecordingId = mbid;
        song.DeezerId = deezerId;
        await context.SaveChangesAsync();

        return song;
    }

    private static DiscoverService Service(WondarrDbContext context, params IRecommendationSource[] sources) =>
        new(
            context,
            sources,
            new MemoryCache(new MemoryCacheOptions()),
            new TestOptionsMonitor<DiscoverOptions>(new DiscoverOptions()),
            NullLogger<DiscoverService>.Instance);

    private sealed class ScriptedSource(
        string name,
        double weight,
        Func<RecommendationSeed, RecommendationSourceResult> answer) : IRecommendationSource
    {
        public int Calls { get; private set; }

        public string Name => name;

        public double Weight => weight;

        public Task<RecommendationSourceResult> GetAsync(RecommendationSeed seed, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(answer(seed));
        }
    }
}
