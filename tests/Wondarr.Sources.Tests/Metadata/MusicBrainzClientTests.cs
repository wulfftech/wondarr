using System.Net;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.MusicBrainz;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Metadata;

/// <summary>
/// Contract tests for <see cref="MusicBrainzClient"/> against the responses recorded on 2026-09-28
/// (see <c>docs/research/research_metadata_plex.md</c> §1.2.1).
/// </summary>
public sealed class MusicBrainzClientTests
{
    private const string BohemianRhapsodyId = "b1a9c0e9-d987-4042-ae91-78d6a3267d69";
    private const string NightAtTheOperaId = "6defd963-fe91-4550-b18e-82c685603c2b";

    [Fact]
    public async Task Search_parses_scores_lengths_and_artist_credits()
    {
        var (client, _, _) = CreateClient(_ => MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("search-get-lucky.json")));

        var result = await client.SearchRecordingsAsync(
            MusicBrainzQuery.RecordingByArtistAndTitle("Daft Punk", "Get Lucky"));

        result.Recordings.Count.Should().BeGreaterThanOrEqualTo(8);

        var first = result.Recordings[0];
        first.Score.Should().Be(100);
        first.Length.Should().Be(242213);
        MbArtistCredit.Format(first.ArtistCredit).Should().Be("Daft Punk");

        result.Recordings
            .Should()
            .Contain(recording => recording.Disambiguation == "Music Factory DJ Beats version with 8-bar intro and outro");
    }

    [Fact]
    public async Task Search_sends_the_escaped_query_and_asks_for_json()
    {
        var (client, handler, _) = CreateClient(_ => MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("search-get-lucky.json")));

        await client.SearchRecordingsAsync(MusicBrainzQuery.RecordingByArtistAndTitle("Daft Punk", "Get Lucky"));

        var uri = handler.Requests.Should().ContainSingle().Subject;
        uri.AbsolutePath.Should().Be("/ws/2/recording");

        var decoded = Uri.UnescapeDataString(uri.Query);
        decoded.Should().Contain("query=recording:\"Get Lucky\" AND artist:\"Daft Punk\"");
        decoded.Should().Contain("limit=25");
        decoded.Should().Contain("fmt=json");
    }

    [Fact]
    public void Escape_backslash_escapes_every_lucene_special()
    {
        MusicBrainzQuery.Escape("AC/DC").Should().Be(@"AC\/DC");
        MusicBrainzQuery.Escape("Who's + Next?").Should().Be(@"Who's \+ Next\?");
        MusicBrainzQuery.Escape("back\\slash").Should().Be(@"back\\slash");
    }

    [Fact]
    public void RecordingByArtistAndTitle_escapes_both_terms()
    {
        MusicBrainzQuery.RecordingByArtistAndTitle("AC/DC", "Back in Black")
            .Should().Be("recording:\"Back in Black\" AND artist:\"AC\\/DC\"");
    }

    [Fact]
    public void RecordingByArtistAndTitle_escapes_a_quote_in_the_title()
    {
        MusicBrainzQuery.RecordingByArtistAndTitle("The Cure", "Love \"Song\"")
            .Should().Be("recording:\"Love \\\"Song\\\"\" AND artist:\"The Cure\"");
    }

    [Fact]
    public async Task Lookup_lower_cases_the_id_and_reads_length_and_isrcs()
    {
        var (client, handler, _) = CreateClient(_ => MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("recording-bohemian-rhapsody.json")));

        var recording = await client.GetRecordingAsync("B1A9C0E9-D987-4042-AE91-78D6A3267D69");

        recording.Should().NotBeNull();
        recording!.Id.Should().Be(BohemianRhapsodyId);
        recording.Length.Should().Be(355106);
        recording.Isrcs.Should().HaveCount(7);

        var uri = handler.Requests.Should().ContainSingle().Subject;
        uri.AbsolutePath.Should().Be($"/ws/2/recording/{BohemianRhapsodyId}");
        uri.Query.Should().Contain("inc=artist-credits+isrcs");
    }

    [Fact]
    public async Task Lookup_of_an_unknown_id_is_negative_cached()
    {
        var (client, handler, cache) = CreateClient(_ => MusicBrainzFixtures.NotFound());

        (await client.GetRecordingAsync(BohemianRhapsodyId)).Should().BeNull();
        (await client.GetRecordingAsync(BohemianRhapsodyId)).Should().BeNull();

        handler.Requests.Should().HaveCount(1);
        cache.SetCount.Should().Be(1);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    public async Task Lookup_rejects_ids_that_are_not_musicbrainz_ids(string id)
    {
        var (client, handler, _) = CreateClient(_ => throw new InvalidOperationException("no request expected"));

        await FluentActions
            .Awaiting(() => client.GetRecordingAsync(id))
            .Should().ThrowAsync<ArgumentException>();

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Isrc_lookup_upper_cases_the_code_and_finds_the_recording()
    {
        var (client, handler, _) = CreateClient(_ => MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("isrc-GBUM71029604.json")));

        var recordings = await client.GetRecordingsByIsrcAsync("gbum71029604");

        recordings.Should().ContainSingle();
        recordings[0].Id.Should().Be(BohemianRhapsodyId);
        MbArtistCredit.Format(recordings[0].ArtistCredit).Should().Be("Queen");

        handler.Requests.Should().ContainSingle().Subject.AbsolutePath.Should().Be("/ws/2/isrc/GBUM71029604");
    }

    [Fact]
    public async Task Isrc_lookup_of_an_unknown_code_is_empty()
    {
        var (client, handler, _) = CreateClient(_ => MusicBrainzFixtures.NotFound());

        (await client.GetRecordingsByIsrcAsync("GBUM71029604")).Should().BeEmpty();
        (await client.GetRecordingsByIsrcAsync("GBUM71029604")).Should().BeEmpty();

        handler.Requests.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("XX")]
    [InlineData("GBUM7102960")]
    [InlineData("GBUM7102960X")]
    public async Task Isrc_lookup_rejects_a_malformed_code(string isrc)
    {
        var (client, handler, _) = CreateClient(_ => throw new InvalidOperationException("no request expected"));

        await FluentActions
            .Awaiting(() => client.GetRecordingsByIsrcAsync(isrc))
            .Should().ThrowAsync<ArgumentException>();

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Browse_walks_the_offsets_and_reads_release_groups()
    {
        var (client, handler, _) = CreateClient(_ => MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("browse-releases-bohemian-rhapsody-p0.json")));

        var releases = await client.GetReleasesForRecordingAsync(BohemianRhapsodyId, maxPages: 3);

        // The recorded page says 319 releases; three pages of 100 stop before offset 300.
        handler.Requests.Select(request => request.Query).Should().SatisfyRespectively(
            query => query.Should().Contain("offset=0"),
            query => query.Should().Contain("offset=100"),
            query => query.Should().Contain("offset=200"));

        handler.Requests[0].Query.Should().Contain("status=official");
        handler.Requests[0].Query.Should().Contain("inc=release-groups+media+artist-credits");

        releases.Should().HaveCount(300);

        var first = releases[0];
        first.ReleaseGroup.Should().NotBeNull();
        first.ReleaseGroup!.PrimaryType.Should().Be("Album");
        first.ReleaseGroup.SecondaryTypes.Should().Contain("Compilation");
        first.Media.Should().NotBeEmpty();
        first.Media[0].TrackCount.Should().BeGreaterThan(0);

        // A release browse does not return tracks; that is why the release lookup exists.
        first.Media[0].Tracks.Should().BeEmpty();
    }

    [Fact]
    public async Task Release_lookup_finds_the_track_that_plays_the_recording()
    {
        var (client, handler, _) = CreateClient(_ => MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("release-a-night-at-the-opera.json")));

        var release = await client.GetReleaseAsync("6DEFD963-FE91-4550-B18E-82C685603C2B");

        release.Should().NotBeNull();
        release!.Id.Should().Be(NightAtTheOperaId);

        var track = release.Media
            .SelectMany(medium => medium.Tracks.Select(t => (Medium: medium, Track: t)))
            .Single(pair => pair.Track.Recording?.Id == BohemianRhapsodyId);

        track.Medium.Position.Should().Be(1);
        track.Track.Position.Should().Be(11);
        track.Track.Number.Should().Be("B4");

        handler.Requests.Should().ContainSingle().Subject.AbsolutePath.Should().Be($"/ws/2/release/{NightAtTheOperaId}");
    }

    [Fact]
    public async Task Release_lookup_of_an_unknown_id_is_null()
    {
        var (client, _, _) = CreateClient(_ => MusicBrainzFixtures.NotFound());

        (await client.GetReleaseAsync(NightAtTheOperaId)).Should().BeNull();
    }

    [Fact]
    public async Task A_repeated_search_is_served_from_the_cache()
    {
        var (client, handler, _) = CreateClient(_ => MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("search-get-lucky.json")));

        var query = MusicBrainzQuery.RecordingByArtistAndTitle("Daft Punk", "Get Lucky");

        var first = await client.SearchRecordingsAsync(query);
        var second = await client.SearchRecordingsAsync(query);

        handler.Requests.Should().ContainSingle();
        second.Recordings.Should().HaveCount(first.Recordings.Count);
        second.Recordings[0].Id.Should().Be(first.Recordings[0].Id);
    }

    [Fact]
    public async Task A_server_error_surfaces_as_a_metadata_provider_exception()
    {
        var (client, _, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var exception = await FluentActions
            .Awaiting(() => client.GetRecordingAsync(BohemianRhapsodyId))
            .Should().ThrowAsync<MetadataProviderException>();

        exception.Which.Provider.Should().Be("musicbrainz");
        exception.Which.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    private static (MusicBrainzClient Client, FixtureHttpMessageHandler Handler, InMemoryMetadataCache Cache) CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FixtureHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://musicbrainz.org/ws/2/") };
        var cache = new InMemoryMetadataCache();

        return (new MusicBrainzClient(http, cache), handler, cache);
    }
}
