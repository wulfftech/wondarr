using System.Globalization;
using Compilarr.Core.Identity;
using FluentAssertions;
using Xunit;

namespace Compilarr.Sources.Tests.Identity;

/// <summary>
/// The three ranking faults a live run over the gate songs turned up, one scenario each, all over
/// inline JSON: a reference length that has to be respected, a Deezer search that hides the album
/// version behind other artists, and a recording whose only release is a bootleg.
/// </summary>
public sealed class IdentityResolverRankingTests
{
    /// <summary>The artist every inline MusicBrainz credit points at.</summary>
    private const string ArtistId = "e5b1a3b0-0000-4000-8000-000000000001";

    private const string PurpleRainId = "a11a11a1-0000-4000-8000-000000000001";
    private const string PurpleRainShortId = "a11a11a1-0000-4000-8000-000000000002";
    private const string PurpleRainIsrc = "USWB10000123";
    private const long PurpleRainDeezerId = 900100;

    private const string RobynId = "b22b22b2-0000-4000-8000-000000000001";
    private const string RobynIsrc = "USRC11000123";

    private const string BootlegId = "c33c33c3-0000-4000-8000-000000000001";
    private const string AbbeyRoadId = "c33c33c3-0000-4000-8000-000000000002";

    [Fact]
    public async Task Purple_rain_takes_the_album_version_the_duration_bounded_search_finds()
    {
        var harness = IdentityHarness.Create();
        MapPurpleRainDeezer(harness, seconds: 524);
        harness.MusicBrainz.Map(
            "/ws/2/recording",
            MbSearch(MbHit(PurpleRainId, "Purple Rain", "Prince", 522000, "1984-06-25")),
            "dur:[514000 TO 534000]");
        MapRecording(harness, PurpleRainId, "Purple Rain", "Prince", 522000, "1984-06-25");
        MapReleases(harness, PurpleRainId, MbBrowse(1, MbRelease("f44f44f4-0000-4000-8000-000000000001", "Purple Rain")));

        var result = await harness.Resolver.ResolveAsync("Prince - Purple Rain");

        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(PurpleRainId);
        result.Identity.DurationMs.Should().Be(522000);

        // Deezer's own reference is right (524 s) but its ISRC is not in MusicBrainz, so the search runs
        // bounded; the 272 s edit is nowhere in it, and the unbounded query is never asked.
        harness.MusicBrainzPaths.Should().Contain($"/ws/2/isrc/{PurpleRainIsrc}");
        RecordingSearches(harness).Should().ContainSingle().Which.Should().Contain("dur:[514000 TO 534000]");
    }

    [Fact]
    public async Task Purple_rain_falls_back_to_the_unbounded_search_and_still_rejects_the_short_edit()
    {
        var harness = IdentityHarness.Create();
        MapPurpleRainDeezer(harness, seconds: 524);
        harness.MusicBrainz.Map("/ws/2/recording", MbSearch(), "dur:[514000 TO 534000]");
        harness.MusicBrainz.Map(
            "/ws/2/recording",
            MbSearch(
                MbHit(PurpleRainShortId, "Purple Rain", "Prince", 272000, "1984-06-25"),
                MbHit(PurpleRainId, "Purple Rain", "Prince", 523000, "1984-06-25")),
            "artist:\"Prince\"");
        MapRecording(harness, PurpleRainId, "Purple Rain", "Prince", 523000, "1984-06-25");
        MapReleases(harness, PurpleRainId, MbBrowse(1, MbRelease("f44f44f4-0000-4000-8000-000000000001", "Purple Rain")));

        var result = await harness.Resolver.ResolveAsync("Prince - Purple Rain");

        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(PurpleRainId);
        result.Identity.DurationMs.Should().Be(523000);

        // Both searches ran: the bounded one found nothing, and the 272 s edit is 252 s off the reference.
        RecordingSearches(harness).Should().HaveCount(2);
    }

    [Fact]
    public async Task Purple_rain_stays_on_deezer_when_only_the_short_edit_exists()
    {
        var harness = IdentityHarness.Create();
        MapPurpleRainDeezer(harness, seconds: 524);
        harness.MusicBrainz.Map(
            "/ws/2/recording",
            MbSearch(MbHit(PurpleRainShortId, "Purple Rain", "Prince", 272000, "1984-06-25")),
            "artist:\"Prince\"");

        var result = await harness.Resolver.ResolveAsync("Prince - Purple Rain");

        result.Status.Should().Be(ResolveStatus.ResolvedDeezerOnly);
        result.Identity!.MbRecordingId.Should().BeNull();
        result.Identity.DeezerId.Should().Be(PurpleRainDeezerId);
        result.Identity.DurationMs.Should().Be(524000);
    }

    [Fact]
    public async Task The_title_only_retry_finds_the_album_version_deezer_buries_behind_other_artists()
    {
        var harness = IdentityHarness.Create();
        harness.Deezer.Map(
            "/search",
            SearchPage(
                DeezerHit(910001, "Dancing On My Own", "Calum Scott", 218, "GBAAA0000001"),
                DeezerHit(910002, "Dancing On My Own", "Pentatonix", 218, "GBAAA0000002"),
                DeezerHit(910003, "Dancing On My Own", "Robyn Black", 218, "GBAAA0000003")),
            "robyn dancing on my own");
        harness.Deezer.Map(
            "/search",
            SearchPage(
                DeezerHit(910003, "Dancing On My Own", "Robyn Black", 218, "GBAAA0000003"),
                DeezerHit(910004, "Dancing On My Own", "Robyn", 287, RobynIsrc)),
            "dancing on my own");
        harness.MusicBrainz.Map(
            $"/ws/2/isrc/{RobynIsrc}",
            MbIsrc(RobynIsrc, MbHit(RobynId, "Dancing On My Own", "Robyn", 287000, "2010-06-01")));
        MapRecording(harness, RobynId, "Dancing On My Own", "Robyn", 287000, "2010-06-01");
        MapReleases(harness, RobynId, MbBrowse(1, MbRelease("f44f44f4-0000-4000-8000-000000000002", "Body Talk")));

        var result = await harness.Resolver.ResolveAsync("Robyn - Dancing On My Own");

        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(RobynId);
        result.Identity.DurationMs.Should().Be(287000);

        // The artist-title search answered with other artists only, so the title alone was searched again.
        harness.DeezerRequests.Should().Contain(request => request.Contains("q=robyn dancing on my own", StringComparison.OrdinalIgnoreCase));
        harness.DeezerRequests.Should().Contain(request => request.Contains("q=dancing on my own&limit=50", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_recording_whose_only_release_is_a_bootleg_is_passed_over()
    {
        var harness = IdentityHarness.Create();

        // Nothing on Deezer, so the MusicBrainz search runs and answers with the bootleg first: it
        // carries a length, the official recording does not, so it scores higher.
        harness.MusicBrainz.Map(
            "/ws/2/recording",
            MbSearch(
                MbHit(BootlegId, "Here Comes the Sun", "The Beatles", 182000, "1990-01-01"),
                MbHit(AbbeyRoadId, "Here Comes the Sun", "The Beatles", null, "1969-09-26")),
            "artist:\"The Beatles\"");

        MapRecording(harness, BootlegId, "Here Comes the Sun", "The Beatles", 182000, "1990-01-01");
        MapReleases(harness, BootlegId, MbBrowse(0));

        MapRecording(harness, AbbeyRoadId, "Here Comes the Sun", "The Beatles", 185000, "1969-09-26");
        MapReleases(
            harness,
            AbbeyRoadId,
            MbBrowse(1, MbRelease("f44f44f4-0000-4000-8000-000000000003", "Abbey Road")));

        var result = await harness.Resolver.ResolveAsync("The Beatles - Here Comes the Sun");

        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(AbbeyRoadId);
        result.Identity.ReleaseOptions.Should().ContainSingle().Which.Title.Should().Be("Abbey Road");

        harness.MusicBrainzPaths.Should().Contain($"/ws/2/recording/{BootlegId}");
    }

    [Fact]
    public async Task A_recording_with_no_official_release_is_still_returned_when_it_is_all_there_is()
    {
        var harness = IdentityHarness.Create();
        harness.MusicBrainz.Map(
            "/ws/2/recording",
            MbSearch(MbHit(BootlegId, "Here Comes the Sun", "The Beatles", 182000, "1990-01-01")),
            "artist:\"The Beatles\"");
        MapRecording(harness, BootlegId, "Here Comes the Sun", "The Beatles", 182000, "1990-01-01");
        MapReleases(harness, BootlegId, MbBrowse(0));

        var result = await harness.Resolver.ResolveAsync("The Beatles - Here Comes the Sun");

        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(BootlegId);
        result.Identity.ReleaseOptions.Should().BeEmpty();
    }

    /// <summary>The two Purple Rain routes: the Deezer search hit and the album behind it.</summary>
    private static void MapPurpleRainDeezer(IdentityHarness harness, int seconds)
    {
        harness.Deezer.Map(
            "/search",
            SearchPage(DeezerHit(PurpleRainDeezerId, "Purple Rain", "Prince", seconds, PurpleRainIsrc)),
            "prince purple rain");
        harness.Deezer.Map("/album/900101", DeezerAlbum("Purple Rain", "album", 9));
    }

    /// <summary>The recording lookup a resolve makes once it has an MBID.</summary>
    private static void MapRecording(
        IdentityHarness harness,
        string id,
        string title,
        string artist,
        int? lengthMs,
        string firstReleaseDate) =>
        harness.MusicBrainz.Map(
            $"/ws/2/recording/{id}",
            MbRecordingBody(id, title, artist, lengthMs, firstReleaseDate));

    /// <summary>The official-release browse of one recording.</summary>
    private static void MapReleases(IdentityHarness harness, string recordingId, string body) =>
        harness.MusicBrainz.Map("/ws/2/release", body, $"recording={recordingId}");

    /// <summary>Every MusicBrainz <em>search</em> request, which is what a "was it asked" assertion reads.</summary>
    private static List<string> RecordingSearches(IdentityHarness harness) =>
        [.. harness.MusicBrainzRequests.Where(request => request.Contains("/ws/2/recording?query=", StringComparison.Ordinal))];

    /// <summary>A Deezer search page of the given hits.</summary>
    private static string SearchPage(params string[] hits) =>
        $$$"""{"data":[{{{string.Join(",", hits)}}}],"total":{{{Int(hits.Length)}}}}""";

    /// <summary>One Deezer search hit.</summary>
    private static string DeezerHit(long id, string title, string artist, int durationSeconds, string isrc) =>
        $$$"""
        {"id":{{{id}}},"title":"{{{title}}}","title_short":"{{{title}}}",
         "duration":{{{Int(durationSeconds)}}},"isrc":"{{{isrc}}}","explicit_lyrics":false,
         "artist":{"id":5001,"name":"{{{artist}}}"},
         "contributors":[{"id":5001,"name":"{{{artist}}}","role":"Main"}],
         "album":{"id":{{{id + 1}}},"title":"{{{title}}}","cover_xl":"https://cdn.example/{{{id}}}.jpg"}}
        """;

    /// <summary>The album behind a Deezer search hit.</summary>
    private static string DeezerAlbum(string title, string recordType, int tracks) =>
        $$$"""
        {"id":900101,"title":"{{{title}}}","record_type":"{{{recordType}}}","release_date":"1984-06-25",
         "nb_tracks":{{{Int(tracks)}}},"cover_xl":"https://cdn.example/900100.jpg",
         "artist":{"id":5001,"name":"Prince"}}
        """;

    /// <summary>A MusicBrainz recording search page, count and all.</summary>
    private static string MbSearch(params string[] recordings) =>
        $$$"""{"count":{{{Int(recordings.Length)}}},"offset":0,"recordings":[{{{string.Join(",", recordings)}}}]}""";

    /// <summary>One recording of a MusicBrainz search page. A null length is what a search often answers.</summary>
    private static string MbHit(string id, string title, string artist, int? lengthMs, string firstReleaseDate) =>
        $$$"""
        {"id":"{{{id}}}","title":"{{{title}}}","length":{{{Length(lengthMs)}}},"video":null,"disambiguation":"",
         "first-release-date":"{{{firstReleaseDate}}}",
         "artist-credit":[{"name":"{{{artist}}}","joinphrase":"","artist":{"id":"{{{ArtistId}}}","name":"{{{artist}}}","sort-name":"{{{artist}}}"}}]}
        """;

    /// <summary>A MusicBrainz recording lookup body.</summary>
    private static string MbRecordingBody(string id, string title, string artist, int? lengthMs, string firstReleaseDate) =>
        $$$"""
        {"id":"{{{id}}}","title":"{{{title}}}","length":{{{Length(lengthMs)}}},"video":false,"disambiguation":"",
         "first-release-date":"{{{firstReleaseDate}}}","isrcs":[],
         "artist-credit":[{"name":"{{{artist}}}","joinphrase":"","artist":{"id":"{{{ArtistId}}}","name":"{{{artist}}}","sort-name":"{{{artist}}}"}}]}
        """;

    /// <summary>An ISRC lookup body carrying one recording.</summary>
    private static string MbIsrc(string isrc, string recording) =>
        $$$"""{"isrc":"{{{isrc}}}","recordings":[{{{recording}}}]}""";

    /// <summary>A release browse page of the given releases.</summary>
    private static string MbBrowse(int count, params string[] releases) =>
        $$$"""{"release-count":{{{Int(count)}}},"release-offset":0,"releases":[{{{string.Join(",", releases)}}}]}""";

    /// <summary>One official release of a browse page.</summary>
    private static string MbRelease(string id, string title) =>
        $$$"""
        {"id":"{{{id}}}","title":"{{{title}}}","status":"Official","date":"1984-06-25",
         "artist-credit":[{"name":"Prince","joinphrase":"","artist":{"id":"{{{ArtistId}}}","name":"Prince"}}],
         "release-group":{"id":"{{{id}}}","title":"{{{title}}}","primary-type":"Album","secondary-types":[],
         "first-release-date":"1984-06-25"},
         "media":[{"position":1,"format":"CD","track-count":9}]}
        """;

    /// <summary>A missing length, written the way MusicBrainz writes it.</summary>
    private static string Length(int? lengthMs) =>
        lengthMs is null ? "null" : Int(lengthMs.Value);

    /// <summary>A number, always with the digits the JSON in this file is written with.</summary>
    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
