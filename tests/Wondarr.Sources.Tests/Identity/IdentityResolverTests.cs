using System.Text.Json;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Sources.Tests.Metadata;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Identity;

/// <summary>
/// The identity resolver over the real clients and the responses recorded on 2026-09-28 (the task's
/// acceptance criteria, one test each).
/// </summary>
public sealed class IdentityResolverTests
{
    private const string GetLuckyId = "833f00e1-781f-4edd-90e4-e52712618862";
    private const string DolbyAtmosMixId = "44a2fd4f-fbfe-4822-b277-2994596b1156";
    private const string BohemianRhapsodyId = "b1a9c0e9-d987-4042-ae91-78d6a3267d69";
    private const string BohemianRhapsodyIsrc = "GBUM71029604";
    private const long BohemianRhapsodyDeezerId = 3541552051;
    private const long LiveAidDeezerId = 4091936771;

    /// <summary>A Deezer track MusicBrainz has never heard of, for the synthetic Deezer-only cases.</summary>
    private const string DeezerOnlyTrack =
        """
        {"id":900123,"title":"Only Deezer (Single Version)","title_short":"Only Deezer","title_version":"(Single Version)",
         "isrc":"QZAAA1900001","duration":181,"explicit_lyrics":false,"track_position":1,"disk_number":1,"release_date":"2019-03-01",
         "artist":{"id":7007,"name":"Nobody"},
         "contributors":[{"id":7007,"name":"Nobody","role":"Main"}],
         "album":{"id":550055,"title":"Only Deezer","cover_xl":"https://cdn.example/deezer-only.jpg"}}
        """;

    /// <summary>The same track, as a one-hit search page.</summary>
    private static readonly string DeezerOnlySearch = $$"""{"data":[{{DeezerOnlyTrack}}],"total":1}""";

    /// <summary>The album that track sits on.</summary>
    private const string DeezerOnlyAlbum =
        """
        {"id":550055,"title":"Only Deezer","record_type":"single","release_date":"2019-03-01","nb_tracks":1,
         "cover_xl":"https://cdn.example/deezer-only.jpg","artist":{"id":7007,"name":"Nobody"}}
        """;

    [Fact]
    public async Task Resolves_an_artist_and_title_line_through_the_isrc_bridge()
    {
        var harness = IdentityHarness.Create();
        MapGetLucky(harness);

        var result = await harness.Resolver.ResolveAsync("Daft Punk - Get Lucky");

        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Reason.Should().BeNull();

        var identity = result.Identity!;
        identity.Source.Should().Be("musicbrainz");
        identity.MbRecordingId.Should().Be(GetLuckyId);
        identity.DurationMs.Should().Be(369000);
        identity.Isrcs.Should().Contain("USQX91300108");
        identity.ArtistCredit.Should().Be("Daft Punk feat. Pharrell Williams & Nile Rodgers");
        identity.Artists.Select(artist => $"{artist.Name}|{artist.Role}|{artist.Position}").Should().Equal(
            "Daft Punk|Main|0",
            "Pharrell Williams|Featured|1",
            "Nile Rodgers|Featured|2");

        identity.ReleaseOptions.Should().HaveCountGreaterThanOrEqualTo(20);
        identity.ReleaseOptions.Should().Contain(option =>
            option.Title == "Random Access Memories" && option.PrimaryType == "Album" && option.SecondaryTypes.Count == 0);
        identity.ReleaseOptions.Should().OnlyContain(option => option.MbReleaseId == option.Key);
        identity.ReleaseOptions.Should().Contain(option => option.IsVariousArtists);

        // The radio edit Deezer answers with first is a different version, so its ISRC is never asked for.
        harness.DeezerRequests.Should().ContainSingle(request => request.Contains("daft punk get lucky", StringComparison.OrdinalIgnoreCase));
        harness.MusicBrainzRequests.Should().NotContain(request => request.Contains("USQX91300809", StringComparison.Ordinal));
        harness.MusicBrainzPaths.Should().NotContain("/ws/2/recording");

        // The Dolby Atmos mix carries MusicBrainz' own disambiguation and never wins.
        identity.MbRecordingId.Should().NotBe(DolbyAtmosMixId);
    }

    [Fact]
    public async Task Resolves_a_studio_recording_from_an_artist_title_line_and_from_free_text()
    {
        var harness = IdentityHarness.Create();
        MapBohemianRhapsody(harness);

        var byArtistAndTitle = await harness.Resolver.ResolveAsync("Queen - Bohemian Rhapsody");
        var byFreeText = await harness.Resolver.ResolveAsync("queen bohemian rhapsody");

        foreach (var result in new[] { byArtistAndTitle, byFreeText })
        {
            result.Status.Should().Be(ResolveStatus.Resolved);
            result.Identity!.MbRecordingId.Should().Be(BohemianRhapsodyId);
            result.Identity.DurationMs.Should().Be(355106);
            result.Identity.Artists.Should().ContainSingle().Which.Name.Should().Be("Queen");
        }
    }

    [Fact]
    public async Task Resolves_a_live_version_to_deezer_when_musicbrainz_has_no_live_aid_recording()
    {
        var harness = IdentityHarness.Create();
        harness.Deezer.Map("/search", DeezerFixtures.Read("search-plain-bohemian-rhapsody.json"), "bohemian rhapsody");
        harness.Deezer.Map($"/track/{LiveAidDeezerId}", SearchHit("search-plain-bohemian-rhapsody.json", LiveAidDeezerId));

        var result = await harness.Resolver.ResolveAsync("Queen - Bohemian Rhapsody (Live Aid)");

        result.Status.Should().Be(ResolveStatus.ResolvedDeezerOnly);

        var identity = result.Identity!;
        identity.Source.Should().Be("deezer");
        identity.DeezerId.Should().Be(LiveAidDeezerId);
        identity.MbRecordingId.Should().BeNull();
        identity.Flags.Should().HaveFlag(VersionFlags.Live);
        identity.Title.Should().Be("Bohemian Rhapsody (Live Aid)");
        identity.DurationMs.Should().Be(148000);

        // The live ISRC and the artist-title search are both unknown to MusicBrainz.
        harness.MusicBrainzPaths.Should().Contain($"/ws/2/isrc/GBUM71805979");
        harness.MusicBrainzPaths.Should().Contain("/ws/2/recording");
    }

    [Theory]
    [InlineData("b1a9c0e9-d987-4042-ae91-78d6a3267d69")]
    [InlineData("https://musicbrainz.org/recording/b1a9c0e9-d987-4042-ae91-78d6a3267d69")]
    [InlineData("GBUM71029604")]
    public async Task Resolves_a_recording_id_a_link_and_an_isrc_to_the_same_recording(string raw)
    {
        var harness = IdentityHarness.Create();
        MapBohemianRhapsody(harness);

        var result = await harness.Resolver.ResolveAsync(raw);

        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(BohemianRhapsodyId);
        result.Identity.DeezerId.Should().BeNull();
    }

    [Fact]
    public async Task Resolves_a_deezer_link_to_the_recording_and_keeps_the_deezer_id()
    {
        var harness = IdentityHarness.Create();
        MapBohemianRhapsody(harness);
        harness.Deezer.Map(
            $"/track/{BohemianRhapsodyDeezerId}",
            DeezerFixtures.Read("track-isrc-GBUM71029604.json"));

        var result = await harness.Resolver.ResolveAsync($"https://www.deezer.com/en/track/{BohemianRhapsodyDeezerId}");

        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(BohemianRhapsodyId);
        result.Identity.DeezerId.Should().Be(BohemianRhapsodyDeezerId);
    }

    [Fact]
    public async Task Resolves_a_deezer_only_song_with_its_album_and_a_stable_album_key()
    {
        var harness = IdentityHarness.Create();
        harness.Deezer.Map("/search", DeezerOnlySearch, "nobody only deezer");
        harness.Deezer.Map("/track/900123", DeezerOnlyTrack);
        harness.Deezer.Map("/album/550055", DeezerOnlyAlbum);

        var result = await harness.Resolver.ResolveAsync("Nobody - Only Deezer");

        result.Status.Should().Be(ResolveStatus.ResolvedDeezerOnly);

        var identity = result.Identity!;
        identity.Source.Should().Be("deezer");
        identity.DeezerId.Should().Be(900123);
        identity.Title.Should().Be("Only Deezer (Single Version)");
        identity.DurationMs.Should().Be(181000);
        identity.Isrcs.Should().Equal("QZAAA1900001");
        identity.CoverUrl.Should().Be("https://cdn.example/deezer-only.jpg");
        identity.ReleaseOptions.Should().ContainSingle();

        var option = identity.ReleaseOptions[0];
        option.Title.Should().Be("Only Deezer");
        option.MbReleaseId.Should().BeNull();
        option.PrimaryType.Should().Be("Single");
        option.TrackNo.Should().Be(1);
        option.DiscNo.Should().Be(1);
        option.TotalTracks.Should().Be(1);
        Guid.TryParse(option.Key, out _).Should().BeTrue();

        // The same album always gets the same key.
        var again = await harness.Resolver.GetIdentityAsync(null, 900123);
        again!.ReleaseOptions[0].Key.Should().Be(option.Key);
    }

    [Fact]
    public async Task An_unknown_song_is_unresolved_with_a_reason_naming_the_input()
    {
        var harness = IdentityHarness.Create();

        var result = await harness.Resolver.ResolveAsync("Nobody - Nothing");

        result.Status.Should().Be(ResolveStatus.Unresolved);
        result.Identity.Should().BeNull();
        result.Reason.Should().Contain("Nobody - Nothing");
        result.Candidates.Should().HaveCountLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task A_spotify_link_is_unsupported_and_costs_no_request()
    {
        var harness = IdentityHarness.Create();

        var result = await harness.Resolver.ResolveAsync("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT");

        result.Status.Should().Be(ResolveStatus.Unsupported);
        result.Reason.Should().Contain("Spotify");
        harness.Deezer.Requests.Should().BeEmpty();
        harness.MusicBrainz.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_ranks_the_bridged_recording_first_and_lists_every_mbid_once()
    {
        var harness = IdentityHarness.Create();
        MapGetLucky(harness);

        var candidates = await harness.Resolver.SearchAsync("Daft Punk - Get Lucky", 10);

        candidates.Should().NotBeEmpty();
        candidates[0].MbRecordingId.Should().Be(GetLuckyId);
        candidates[0].ViaIsrc.Should().BeTrue();

        // The ISRC lookup does not echo ISRCs; the Deezer album version that led there supplies both,
        // so the add dialog can play a preview for the top result.
        candidates[0].DeezerId.Should().Be(67238735);
        candidates[0].Isrcs.Should().Contain("USQX91300108");
        candidates[0].Source.Should().Be("musicbrainz");

        candidates.Select(candidate => candidate.Score).Should().BeInDescendingOrder();
        candidates
            .Where(candidate => candidate.MbRecordingId is not null)
            .Select(candidate => candidate.MbRecordingId)
            .Should().OnlyHaveUniqueItems();
        candidates.Should().HaveCountLessThanOrEqualTo(10);
    }

    /// <summary>The Get Lucky routes: the Deezer search, the ISRC bridge and the MusicBrainz lookup.</summary>
    private static void MapGetLucky(IdentityHarness harness)
    {
        harness.Deezer.Map("/search", DeezerFixtures.Read("search-plain-get-lucky.json"), "daft punk get lucky");
        harness.MusicBrainz.Map("/ws/2/isrc/USQX91300108", MusicBrainzFixtures.Read("isrc-USQX91300108.json"));
        harness.MusicBrainz.Map($"/ws/2/recording/{GetLuckyId}", MusicBrainzFixtures.Read("recording-get-lucky.json"));
        harness.MusicBrainz.Map("/ws/2/release", MusicBrainzFixtures.Read("browse-releases-get-lucky-p0.json"), "offset=0");
        harness.MusicBrainz.Map(
            "/ws/2/recording",
            MusicBrainzFixtures.Read("search-get-lucky.json"),
            "daft punk");
    }

    /// <summary>The Bohemian Rhapsody routes: the Deezer search, the ISRC bridge and the MusicBrainz lookup.</summary>
    private static void MapBohemianRhapsody(IdentityHarness harness)
    {
        harness.Deezer.Map("/search", DeezerFixtures.Read("search-plain-bohemian-rhapsody.json"), "queen bohemian rhapsody");
        harness.MusicBrainz.Map($"/ws/2/isrc/{BohemianRhapsodyIsrc}", MusicBrainzFixtures.Read("isrc-GBUM71029604.json"));
        harness.MusicBrainz.Map($"/ws/2/recording/{BohemianRhapsodyId}", MusicBrainzFixtures.Read("recording-bohemian-rhapsody.json"));
        harness.MusicBrainz.Map(
            "/ws/2/release",
            MusicBrainzFixtures.Read("browse-releases-bohemian-rhapsody-p0.json"),
            "offset=0");
    }

    /// <summary>One Deezer search hit, lifted verbatim out of a recorded search page.</summary>
    private static string SearchHit(string fixture, long trackId)
    {
        using var document = JsonDocument.Parse(DeezerFixtures.Read(fixture));

        foreach (var element in document.RootElement.GetProperty("data").EnumerateArray())
        {
            if (element.GetProperty("id").GetInt64() == trackId)
            {
                return element.GetRawText();
            }
        }

        throw new InvalidOperationException($"The fixture {fixture} holds no hit with the id {trackId}.");
    }
}
