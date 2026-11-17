using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Wondarr.Core.Discover;
using Wondarr.Core.Metadata.Deezer;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// <c>/api/v1/discover</c> over the real service and database, with the three sources replaced by a script
/// so that no network is touched. Previews by artist and title are covered at the end.
/// </summary>
public sealed class DiscoverApiTests
{
    private const string Endpoint = "/api/v1/discover";
    private const string SeedMbid = "e97f805a-ab48-4c52-855e-07049142113d";
    private const string PreviewUrl = "https://cdns-preview-d.dzcdn.net/stream/teardrop.mp3?hdnea=exp=1";

    [Fact]
    public async Task A_radio_is_seeded_by_the_song_and_lists_ranked_suggestions_and_sources()
    {
        using var factory = Factory();
        using var client = SongApiTests.Authenticated(factory);
        var songId = await SongApiTests.SeedSongAsync(factory, "Get Lucky", SeedMbid, monitored: true);

        using var response = await client.GetAsync(new Uri($"{Endpoint}?songId={songId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await SongApiTests.ReadJsonAsync(response);
        body.GetProperty("mode").GetString().Should().Be("radio");
        body.GetProperty("mixSeed").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("seeds")[0].GetProperty("songId").GetInt64().Should().Be(songId);
        body.GetProperty("sources")[0].GetProperty("name").GetString().Should().Be("listenbrainz");
        body.GetProperty("sources")[0].GetProperty("status").GetString().Should().Be("answered");

        var first = body.GetProperty("suggestions")[0];
        first.GetProperty("title").GetString().Should().Be("Teardrop");
        first.GetProperty("artist").GetString().Should().Be("Massive Attack");
        first.GetProperty("mbRecordingId").GetString().Should().Be("f3bba4cd-8018-468b-902e-bc8f029593e5");
        first.GetProperty("sources")[0].GetString().Should().Be("listenbrainz");
        first.GetProperty("score").GetDouble().Should().BeGreaterThan(0);
        body.GetProperty("suggestions").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task A_radio_for_an_unknown_song_is_not_found_and_both_seeds_at_once_is_a_bad_request()
    {
        using var factory = Factory();
        using var client = SongApiTests.Authenticated(factory);

        using var unknown = await client.GetAsync(new Uri($"{Endpoint}?songId=4242", UriKind.Relative));
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var both = await client.GetAsync(new Uri($"{Endpoint}?songId=1&mixSeed=2", UriKind.Relative));
        both.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_mix_echoes_its_seed_and_the_same_seed_gives_the_same_sample()
    {
        using var factory = Factory();
        using var client = SongApiTests.Authenticated(factory);

        for (var index = 0; index < 12; index++)
        {
            await SongApiTests.SeedSongAsync(factory, $"Song {index}", mbRecordingId: null, monitored: true);
        }

        using var fresh = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));
        fresh.StatusCode.Should().Be(HttpStatusCode.OK);

        var freshBody = await SongApiTests.ReadJsonAsync(fresh);
        freshBody.GetProperty("mode").GetString().Should().Be("mix");
        freshBody.GetProperty("mixSeed").GetInt32().Should().BeGreaterThan(0, "an omitted seed draws a new one and says which");
        freshBody.GetProperty("seeds").GetArrayLength().Should().Be(5);

        var seed = freshBody.GetProperty("mixSeed").GetInt32();

        using var again = await client.GetAsync(new Uri($"{Endpoint}?mixSeed={seed}", UriKind.Relative));
        var againBody = await SongApiTests.ReadJsonAsync(again);

        againBody.GetProperty("mixSeed").GetInt32().Should().Be(seed);
        againBody.GetProperty("seeds").GetRawText().Should().Be(freshBody.GetProperty("seeds").GetRawText());
    }

    [Fact]
    public async Task A_dismissal_hides_a_suggestion_and_undoing_it_brings_it_back()
    {
        using var factory = Factory();
        using var client = SongApiTests.Authenticated(factory);
        var songId = await SongApiTests.SeedSongAsync(factory, "Get Lucky", SeedMbid, monitored: true);

        using var created = await client.PostAsync(
            new Uri($"{Endpoint}/dismissals", UriKind.Relative),
            SongApiTests.Json("""{"mbRecordingId":"f3bba4cd-8018-468b-902e-bc8f029593e5","artist":"Massive Attack","title":"Teardrop"}"""));

        created.StatusCode.Should().Be(HttpStatusCode.OK);

        var dismissal = await SongApiTests.ReadJsonAsync(created);
        var id = dismissal.GetProperty("id").GetInt64();
        dismissal.GetProperty("artistKey").GetString().Should().Be("massive attack");
        dismissal.GetProperty("titleKey").GetString().Should().Be("teardrop");

        var hidden = await SuggestedTitlesAsync(client, songId);
        hidden.Should().Equal("Glory Box");

        using var list = await client.GetAsync(new Uri($"{Endpoint}/dismissals", UriKind.Relative));
        var listed = await SongApiTests.ReadJsonAsync(list);
        listed.GetArrayLength().Should().Be(1);
        listed[0].GetProperty("id").GetInt64().Should().Be(id);

        using var undone = await client.DeleteAsync(new Uri($"{Endpoint}/dismissals/{id}", UriKind.Relative));
        undone.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await SuggestedTitlesAsync(client, songId)).Should().Equal("Teardrop", "Glory Box");

        using var gone = await client.DeleteAsync(new Uri($"{Endpoint}/dismissals/{id}", UriKind.Relative));
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var empty = await client.GetAsync(new Uri($"{Endpoint}/dismissals", UriKind.Relative));
        (await SongApiTests.ReadJsonAsync(empty)).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_dismissal_without_an_artist_or_with_a_bad_mbid_is_a_bad_request()
    {
        using var factory = Factory();
        using var client = SongApiTests.Authenticated(factory);

        using var noArtist = await client.PostAsync(
            new Uri($"{Endpoint}/dismissals", UriKind.Relative),
            SongApiTests.Json("""{"artist":"","title":"Teardrop"}"""));
        noArtist.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var badMbid = await client.PostAsync(
            new Uri($"{Endpoint}/dismissals", UriKind.Relative),
            SongApiTests.Json("""{"mbRecordingId":"nope","artist":"A","title":"B"}"""));
        badMbid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Discover_requires_the_api_key()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_preview_by_artist_and_title_searches_deezer_and_returns_a_fresh_url()
    {
        var deezer = Substitute.For<IDeezerClient>();
        deezer.SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DeezerSearchResult
            {
                Data =
                [
                    Track(1, "Teardrop (Live)", "Massive Attack"),
                    Track(2, "Teardrop", "Some Cover Band"),
                    Track(3, "TEARDROP", "massive attack"),
                    Track(4, "Teardrop", "Massive Attack"),
                ],
            });
        deezer.GetFreshPreviewUrlAsync(3, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(PreviewUrl));

        using var factory = SongApiTests.FakeProviders(deezer: deezer);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.GetAsync(
            new Uri("/api/v1/preview?artist=Massive%20Attack&title=Teardrop", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue("the signed URL is never stored");
        (await SongApiTests.ReadJsonAsync(response)).GetProperty("url").GetString().Should().Be(PreviewUrl);

        await deezer.Received(1).SearchTracksAsync("Massive Attack Teardrop", Arg.Any<int>(), Arg.Any<CancellationToken>());
        await deezer.Received(1).GetFreshPreviewUrlAsync(3, Arg.Any<CancellationToken>());
        await deezer.DidNotReceive().GetFreshPreviewUrlAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_preview_by_name_with_no_matching_result_or_with_half_a_name_is_not_found_or_bad()
    {
        var deezer = Substitute.For<IDeezerClient>();
        deezer.SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DeezerSearchResult { Data = [Track(1, "Something Else", "Massive Attack")] });

        using var factory = SongApiTests.FakeProviders(deezer: deezer);
        using var client = SongApiTests.Authenticated(factory);

        using var noMatch = await client.GetAsync(
            new Uri("/api/v1/preview?artist=Massive%20Attack&title=Teardrop", UriKind.Relative));
        noMatch.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var halfAName = await client.GetAsync(new Uri("/api/v1/preview?artist=Massive%20Attack", UriKind.Relative));
        halfAName.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await deezer.DidNotReceive().GetFreshPreviewUrlAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_deezer_id_wins_over_a_name_and_no_search_is_made()
    {
        var deezer = Substitute.For<IDeezerClient>();
        deezer.GetFreshPreviewUrlAsync(77, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(PreviewUrl));

        using var factory = SongApiTests.FakeProviders(deezer: deezer);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.GetAsync(
            new Uri("/api/v1/preview?deezerId=77&artist=Massive%20Attack&title=Teardrop", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await deezer.DidNotReceive().SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private static DeezerTrack Track(long id, string title, string artist) =>
        new() { Id = id, Title = title, Artist = new DeezerArtist { Id = 1, Name = artist } };

    private static async Task<List<string>> SuggestedTitlesAsync(HttpClient client, long songId)
    {
        using var response = await client.GetAsync(new Uri($"{Endpoint}?songId={songId}", UriKind.Relative));
        var body = await SongApiTests.ReadJsonAsync(response);

        return [.. body.GetProperty("suggestions").EnumerateArray().Select(item => item.GetProperty("title").GetString()!)];
    }

    /// <summary>An app whose only recommendation source is a script, so the real services run without a network.</summary>
    private static WondarrAppFactory Factory() =>
        SongApiTests.FakeProviders(configure: services =>
        {
            services.RemoveAll<IRecommendationSource>();
            services.AddScoped<IRecommendationSource, ScriptedSource>();
        });

    private sealed class ScriptedSource : IRecommendationSource
    {
        public string Name => "listenbrainz";

        public double Weight => 1.0;

        public Task<RecommendationSourceResult> GetAsync(RecommendationSeed seed, CancellationToken cancellationToken) =>
            Task.FromResult(RecommendationSourceResult.Answered(
            [
                new RecommendationCandidate(
                    "Teardrop",
                    "Massive Attack",
                    "Mezzanine",
                    "f3bba4cd-8018-468b-902e-bc8f029593e5"),
                new RecommendationCandidate("Glory Box", "Portishead"),
            ]));
    }
}
