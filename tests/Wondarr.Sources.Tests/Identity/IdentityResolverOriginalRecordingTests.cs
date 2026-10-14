using System.Globalization;
using System.Net;
using Wondarr.Core.Identity;
using Wondarr.Sources.Tests.Metadata;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Identity;

/// <summary>
/// M-34: the lookup's choice of recording (the original album one before its compilation namesakes)
/// and iTunes as the third length reference when Deezer has no hit.
/// </summary>
public sealed class IdentityResolverOriginalRecordingTests
{
    private const string ArtistId = "a9a9a9a9-0000-4000-8000-000000000001";

    /// <summary>The original "Story of a Girl" recording of <c>search-nine-days-story-of-a-girl.json</c>.</summary>
    private const string NineDaysOriginalId = "d0d0d0d0-0000-4000-8000-000000000004";

    private const string RobynEditId = "b22b22b2-0000-4000-8000-000000000001";
    private const string RobynAlbumId = "b22b22b2-0000-4000-8000-000000000002";
    private const string RobynIsrc = "USRC11000123";

    [Fact]
    public async Task A_plain_artist_and_title_add_resolves_to_the_original_album_recording()
    {
        var harness = IdentityHarness.Create();
        harness.MusicBrainz.Map("/ws/2/recording", MusicBrainzFixtures.Read("search-nine-days-story-of-a-girl.json"), "artist:\"Nine Days\"");
        MapRecording(harness, NineDaysOriginalId, "Story of a Girl", "Nine Days", 189000, "2000-05-30");
        MapReleases(harness, NineDaysOriginalId, MbBrowse(MbRelease("e0e0e0e0-0000-4000-8000-000000000005", "The Madding Crowd", "2000-05-30", "Album")));

        var result = await harness.Resolver.ResolveAsync("Nine Days - Story of a Girl");

        // Three compilation recordings (2007, 1999 and 2000) are 197.6 s, two of them dated before the album.
        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(NineDaysOriginalId);
        result.Identity.DurationMs.Should().Be(189000);
        result.Identity.ReleaseOptions.Should().Contain(option => option.Title == "The Madding Crowd");
    }

    [Fact]
    public async Task The_lookup_lists_the_original_album_recording_first()
    {
        var harness = IdentityHarness.Create();
        harness.MusicBrainz.Map("/ws/2/recording", MusicBrainzFixtures.Read("search-nine-days-story-of-a-girl.json"), "artist:\"Nine Days\"");

        var found = await harness.Resolver.SearchAsync("Nine Days - Story of a Girl", 10);

        found.Select(candidate => candidate.MbRecordingId).Should().Equal(
            NineDaysOriginalId,
            "d0d0d0d0-0000-4000-8000-000000000002",
            "d0d0d0d0-0000-4000-8000-000000000003",
            "d0d0d0d0-0000-4000-8000-000000000001");
        found.Select(candidate => candidate.OfficialAlbumDate).Should().Equal("2000-05-30", null, null, null);
    }

    [Fact]
    public async Task The_isrc_bridge_reads_a_few_release_lists_to_find_the_original_and_stops_at_five()
    {
        var harness = IdentityHarness.Create();
        var ids = ArrangeSevenIsrcRecordings(harness, failingRelease: null);

        var result = await harness.Resolver.ResolveAsync("Nine Days - Story of a Girl");

        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(ids[2]);

        var releaseRequests = harness.MusicBrainzRequests.Where(request => request.Contains("/ws/2/release?recording=", StringComparison.Ordinal)).ToList();
        releaseRequests.Should().HaveCount(5);
        releaseRequests.Should().NotContain(request => request.Contains(ids[5], StringComparison.Ordinal) || request.Contains(ids[6], StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_release_list_that_cannot_be_read_leaves_the_order_the_dates_gave_and_does_not_fail_the_lookup()
    {
        var harness = IdentityHarness.Create();
        var ids = ArrangeSevenIsrcRecordings(harness, failingRelease: 1);

        var result = await harness.Resolver.ResolveAsync("Nine Days - Story of a Girl");

        // The second list fails with a 503: the ranking falls back to the earliest first release.
        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(ids[0]);
    }

    /// <summary>
    /// Seven recordings share the ISRC and the title; the ISRC lookup lists no releases. The third by
    /// date is the album one, and the five earliest by date are the ones whose releases are read.
    /// </summary>
    private static string[] ArrangeSevenIsrcRecordings(IdentityHarness harness, int? failingRelease)
    {
        harness.Deezer.Map(
            "/search",
            SearchPage(DeezerHit(920001, "Story of a Girl", "Nine Days", 198, "USEP10000001")),
            "nine days story of a girl");
        harness.Deezer.Map("/album/920002", DeezerAlbum(920002));

        // Listed latest first, so the order they are read in is not the order the ISRC lookup gave.
        var ids = Enumerable.Range(1, 7).Select(number => $"c1c1c1c1-0000-4000-8000-00000000000{number}").ToArray();
        var years = new[] { "1996", "1997", "1998", "1999", "2000", "2001", "2002" };
        harness.MusicBrainz.Map(
            "/ws/2/isrc/USEP10000001",
            MbIsrc("USEP10000001", ids.Select((id, index) => MbHit(id, "Story of a Girl", "Nine Days", index == 2 ? 189000 : 197600, years[index])).Reverse()));

        for (var index = 0; index < ids.Length; index++)
        {
            var original = index == 2;
            MapRecording(harness, ids[index], "Story of a Girl", "Nine Days", original ? 189000 : 197600, years[index]);

            if (index == failingRelease)
            {
                harness.MusicBrainz.Map("/ws/2/release", """{"error":"busy"}""", $"recording={ids[index]}", HttpStatusCode.ServiceUnavailable);
                continue;
            }

            MapReleases(
                harness,
                ids[index],
                MbBrowse(MbRelease($"e1e1e1e1-0000-4000-8000-00000000000{index + 1}", original ? "The Madding Crowd" : "Hits Volume 1", years[index], "Album", original ? null : "Compilation")));
        }

        return ids;
    }

    [Fact]
    public async Task A_lone_iTunes_length_no_musicbrainz_recording_agrees_with_is_not_taken()
    {
        var harness = SpreadHarness();
        harness.ITunes.Map(
            "/search",
            ITunesPage(ITunesHit("Story of a Girl", "Nine Days", 232000, "The Madding Crowd")),
            "nine days story of a girl");

        var result = await harness.Resolver.ResolveAsync("Nine Days - Story of a Girl");

        // 232 s is within a quarter of the median (190 s) but 40 s from the nearest recording: the lookup
        // is exactly what it was without iTunes, and the bounded search is never made.
        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(SpreadIds[0]);
        result.Identity.DurationMs.Should().Be(189000);
        harness.ITunesRequests.Should().ContainSingle();
        RecordingSearches(harness).Should().ContainSingle();
    }

    [Fact]
    public async Task A_lone_iTunes_length_far_above_every_musicbrainz_length_is_not_taken()
    {
        var harness = SpreadHarness();
        harness.ITunes.Map(
            "/search",
            ITunesPage(ITunesHit("Story of a Girl", "Nine Days", 640000, "The Madding Crowd (Deluxe)")),
            "nine days story of a girl");

        var result = await harness.Resolver.ResolveAsync("Nine Days - Story of a Girl");

        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(SpreadIds[0]);
        RecordingSearches(harness).Should().ContainSingle();
    }

    [Fact]
    public async Task An_original_whose_length_is_off_the_reference_does_not_sort_first()
    {
        var harness = IdentityHarness.Create();
        harness.Deezer.Map(
            "/search",
            SearchPage(DeezerHit(930001, "Story of a Girl", "Nine Days", 198, "USEP20000001")),
            "nine days story of a girl");
        harness.Deezer.Map("/album/930002", DeezerAlbum(930002));
        harness.MusicBrainz.Map(
            "/ws/2/recording",
            MbSearch(
                MbHitOnAlbum("d2d2d2d2-0000-4000-8000-000000000001", 197600, "2007-03-06", "Rock Hits", "Compilation"),
                MbHitOnAlbum("d2d2d2d2-0000-4000-8000-000000000002", 167000, "2000-05-30", "The Madding Crowd", null)),
            "artist:\"Nine Days\"");

        var found = await harness.Resolver.SearchAsync("Nine Days - Story of a Girl", 10);

        // The album recording is 31 s off Deezer's length: it is not a candidate for "original", so the
        // one that fits comes first.
        var musicBrainz = found.Where(candidate => candidate.MbRecordingId is not null).ToList();
        musicBrainz[0].MbRecordingId.Should().Be("d2d2d2d2-0000-4000-8000-000000000001");
        musicBrainz.Single(candidate => candidate.MbRecordingId == "d2d2d2d2-0000-4000-8000-000000000002").OfficialAlbumDate.Should().BeNull();
    }

    [Fact]
    public async Task Without_a_deezer_hit_iTunes_gives_the_album_length_and_the_edit_is_dropped()
    {
        var harness = RobynHarness();
        harness.ITunes.Map(
            "/search",
            ITunesPage(
                ITunesHit("Dancing On My Own", "Robyn", 218000, "Dancing On My Own - Single"),
                ITunesHit("Dancing On My Own", "Robyn", 287000, "Body Talk"),
                ITunesHit("Dancing On My Own", "Calum Scott", 261000, "Only Human")),
            "robyn dancing on my own");

        var result = await harness.Resolver.ResolveAsync("Robyn - Dancing On My Own");

        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(RobynAlbumId);
        result.Identity.DurationMs.Should().Be(287000);

        // The unbounded search first, iTunes once, then the search bounded to ten seconds around 287 s.
        harness.ITunesRequests.Should().ContainSingle();
        RecordingSearches(harness).Should().HaveCount(2);
        RecordingSearches(harness)[1].Should().Contain("dur:[277000 TO 297000]");
    }

    [Fact]
    public async Task Without_a_deezer_hit_and_without_iTunes_the_lookup_is_as_it_was()
    {
        var harness = RobynHarness();

        var result = await harness.Resolver.ResolveAsync("Robyn - Dancing On My Own");

        // Nothing says how long the song is, so the earlier-dated edit wins the tie as before.
        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(RobynEditId);
        harness.ITunesRequests.Should().ContainSingle();
        RecordingSearches(harness).Should().ContainSingle();
    }

    [Fact]
    public async Task An_iTunes_length_far_above_the_musicbrainz_median_is_not_taken()
    {
        var harness = RobynHarness();
        harness.ITunes.Map(
            "/search",
            ITunesPage(
                ITunesHit("Dancing On My Own", "Robyn", 640000, "Body Talk (Deluxe)"),
                ITunesHit("Dancing On My Own", "Robyn", 287000, "Body Talk")),
            "robyn dancing on my own");

        var result = await harness.Resolver.ResolveAsync("Robyn - Dancing On My Own");

        // The median of the two MusicBrainz lengths is 252.5 s; 640 s is not within a quarter of it, 287 s is the nearest.
        result.Identity!.MbRecordingId.Should().Be(RobynAlbumId);
        RecordingSearches(harness)[1].Should().Contain("dur:[277000 TO 297000]");
    }

    [Fact]
    public async Task A_single_is_passed_over_for_an_album_hit_when_both_match()
    {
        var harness = RobynHarness();
        harness.ITunes.Map(
            "/search",
            ITunesPage(
                ITunesHit("Dancing On My Own", "Robyn", 300000, "Dancing On My Own - Single"),
                ITunesHit("Dancing On My Own", "Robyn", 287000, "Body Talk")),
            "robyn dancing on my own");

        var result = await harness.Resolver.ResolveAsync("Robyn - Dancing On My Own");

        // The single's 300 s is the longest, but the album's 287 s is the one that counts.
        result.Identity!.MbRecordingId.Should().Be(RobynAlbumId);
        RecordingSearches(harness)[1].Should().Contain("dur:[277000 TO 297000]");
    }

    [Fact]
    public async Task A_title_or_artist_that_is_not_the_songs_gives_no_length()
    {
        var harness = RobynHarness();
        harness.ITunes.Map(
            "/search",
            ITunesPage(
                ITunesHit("Dancing On My Own (Live)", "Robyn", 287000, "Live Album"),
                ITunesHit("Dancing On My Own", "Robyn & Friends Tribute", 287000, "Tribute")),
            "robyn dancing on my own");

        var result = await harness.Resolver.ResolveAsync("Robyn - Dancing On My Own");

        result.Identity!.MbRecordingId.Should().Be(RobynEditId);
        RecordingSearches(harness).Should().ContainSingle();
    }

    [Fact]
    public async Task A_busy_iTunes_leaves_the_lookup_as_it_was()
    {
        var harness = RobynHarness();
        harness.ITunes.Map("/search", """{"errorMessage":"rate limited"}""", status: HttpStatusCode.Forbidden);

        var result = await harness.Resolver.ResolveAsync("Robyn - Dancing On My Own");

        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(RobynEditId);
    }

    [Fact]
    public async Task iTunes_is_not_asked_when_deezer_has_a_hit()
    {
        var harness = RobynHarness();
        harness.Deezer.Map(
            "/search",
            SearchPage(DeezerHit(910004, "Dancing On My Own", "Robyn", 287, RobynIsrc)),
            "robyn dancing on my own");
        harness.Deezer.Map("/album/910005", DeezerAlbum(910005));
        harness.MusicBrainz.Map(
            $"/ws/2/isrc/{RobynIsrc}",
            MbIsrc(RobynIsrc, [MbHit(RobynAlbumId, "Dancing On My Own", "Robyn", 287000, "2010-06-01")]));

        var result = await harness.Resolver.ResolveAsync("Robyn - Dancing On My Own");

        result.Identity!.MbRecordingId.Should().Be(RobynAlbumId);
        harness.ITunesRequests.Should().BeEmpty();
    }

    private static readonly string[] SpreadIds =
    [
        "e3e3e3e3-0000-4000-8000-000000000001",
        "e3e3e3e3-0000-4000-8000-000000000002",
        "e3e3e3e3-0000-4000-8000-000000000003",
    ];

    /// <summary>MusicBrainz knows three Nine Days recordings of 189, 188 and 192 s; Deezer and iTunes know nothing.</summary>
    private static IdentityHarness SpreadHarness()
    {
        var harness = IdentityHarness.Create();
        harness.MusicBrainz.Map(
            "/ws/2/recording",
            MbSearch(
                MbHit(SpreadIds[0], "Story of a Girl", "Nine Days", 189000, "2000-05-30"),
                MbHit(SpreadIds[1], "Story of a Girl", "Nine Days", 188000, "2007-03-06"),
                MbHit(SpreadIds[2], "Story of a Girl", "Nine Days", 192000, "2009-01-01")),
            "artist:\"Nine Days\"");
        MapRecording(harness, SpreadIds[0], "Story of a Girl", "Nine Days", 189000, "2000-05-30");
        MapReleases(harness, SpreadIds[0], MbBrowse(MbRelease("f6f6f6f6-0000-4000-8000-000000000001", "The Madding Crowd", "2000-05-30", "Album")));

        return harness;
    }

    /// <summary>MusicBrainz knows a 218 s edit (dated earlier) and the 287 s album recording; Deezer and iTunes know nothing.</summary>
    private static IdentityHarness RobynHarness()
    {
        var harness = IdentityHarness.Create();
        harness.MusicBrainz.Map(
            "/ws/2/recording",
            MbSearch(
                MbHit(RobynEditId, "Dancing On My Own", "Robyn", 218000, "2010-05-01"),
                MbHit(RobynAlbumId, "Dancing On My Own", "Robyn", 287000, "2010-06-01")),
            "artist:\"Robyn\"");
        MapRecording(harness, RobynEditId, "Dancing On My Own", "Robyn", 218000, "2010-05-01");
        MapRecording(harness, RobynAlbumId, "Dancing On My Own", "Robyn", 287000, "2010-06-01");
        MapReleases(harness, RobynEditId, MbBrowse(MbRelease("f5f5f5f5-0000-4000-8000-000000000001", "Dancing On My Own", "2010-05-01", "Single")));
        MapReleases(harness, RobynAlbumId, MbBrowse(MbRelease("f5f5f5f5-0000-4000-8000-000000000002", "Body Talk", "2010-06-01", "Album")));

        return harness;
    }

    private static void MapRecording(IdentityHarness harness, string id, string title, string artist, int? lengthMs, string firstReleaseDate) =>
        harness.MusicBrainz.Map(
            $"/ws/2/recording/{id}",
            $$$"""
            {"id":"{{{id}}}","title":"{{{title}}}","length":{{{Length(lengthMs)}}},"video":false,"disambiguation":"",
             "first-release-date":"{{{firstReleaseDate}}}","isrcs":[],
             "artist-credit":[{"name":"{{{artist}}}","joinphrase":"","artist":{"id":"{{{ArtistId}}}","name":"{{{artist}}}","sort-name":"{{{artist}}}"}}]}
            """);

    private static void MapReleases(IdentityHarness harness, string recordingId, string body) =>
        harness.MusicBrainz.Map("/ws/2/release", body, $"recording={recordingId}");

    private static List<string> RecordingSearches(IdentityHarness harness) =>
        [.. harness.MusicBrainzRequests.Where(request => request.Contains("/ws/2/recording?query=", StringComparison.Ordinal))];

    private static string MbSearch(params string[] recordings) =>
        $$$"""{"count":{{{recordings.Length}}},"offset":0,"recordings":[{{{string.Join(",", recordings)}}}]}""";

    private static string MbIsrc(string isrc, IEnumerable<string> recordings) =>
        $$$"""{"isrc":"{{{isrc}}}","recordings":[{{{string.Join(",", recordings)}}}]}""";

    private static string MbHit(string id, string title, string artist, int? lengthMs, string firstReleaseDate) =>
        $$$"""
        {"id":"{{{id}}}","title":"{{{title}}}","length":{{{Length(lengthMs)}}},"video":null,"disambiguation":"",
         "first-release-date":"{{{firstReleaseDate}}}",
         "artist-credit":[{"name":"{{{artist}}}","joinphrase":"","artist":{"id":"{{{ArtistId}}}","name":"{{{artist}}}","sort-name":"{{{artist}}}"}}]}
        """;

    private static string MbHitOnAlbum(string id, int lengthMs, string firstReleaseDate, string album, string? secondaryType) =>
        $$$"""
        {"id":"{{{id}}}","title":"Story of a Girl","length":{{{lengthMs}}},"video":null,"disambiguation":"",
         "first-release-date":"{{{firstReleaseDate}}}",
         "artist-credit":[{"name":"Nine Days","joinphrase":"","artist":{"id":"{{{ArtistId}}}","name":"Nine Days","sort-name":"Nine Days"}}],
         "releases":[{"id":"{{{id}}}","status":"Official","title":"{{{album}}}","date":"{{{firstReleaseDate}}}",
          "release-group":{"id":"{{{id}}}","title":"{{{album}}}","primary-type":"Album","secondary-types":[{{{SecondaryTypesJson(secondaryType)}}}]}}]}
        """;

    private static string MbBrowse(params string[] releases) =>
        $$$"""{"release-count":{{{releases.Length}}},"release-offset":0,"releases":[{{{string.Join(",", releases)}}}]}""";

    private static string MbRelease(string id, string title, string date, string primaryType, string? secondaryType = null) =>
        $$$"""
        {"id":"{{{id}}}","title":"{{{title}}}","status":"Official","date":"{{{date}}}",
         "artist-credit":[{"name":"Nine Days","joinphrase":"","artist":{"id":"{{{ArtistId}}}","name":"Nine Days"}}],
         "release-group":{"id":"{{{id}}}","title":"{{{title}}}","primary-type":"{{{primaryType}}}",
          "secondary-types":[{{{SecondaryTypesJson(secondaryType)}}}],"first-release-date":"{{{date}}}"},
         "media":[{"position":1,"format":"CD","track-count":12}]}
        """;

    private static string SecondaryTypesJson(string? secondaryType) =>
        secondaryType is null ? string.Empty : $"\"{secondaryType}\"";

    private static string SearchPage(params string[] hits) =>
        $$$"""{"data":[{{{string.Join(",", hits)}}}],"total":{{{hits.Length}}}}""";

    private static string DeezerHit(long id, string title, string artist, int durationSeconds, string isrc) =>
        $$$"""
        {"id":{{{id}}},"title":"{{{title}}}","title_short":"{{{title}}}",
         "duration":{{{durationSeconds}}},"isrc":"{{{isrc}}}","explicit_lyrics":false,
         "artist":{"id":5001,"name":"{{{artist}}}"},
         "contributors":[{"id":5001,"name":"{{{artist}}}","role":"Main"}],
         "album":{"id":{{{id + 1}}},"title":"{{{title}}}","cover_xl":"https://cdn.example/{{{id}}}.jpg"}}
        """;

    private static string DeezerAlbum(long id) =>
        $$$"""
        {"id":{{{id}}},"title":"Album","record_type":"album","release_date":"2010-06-01",
         "nb_tracks":9,"cover_xl":"https://cdn.example/album.jpg","artist":{"id":5001,"name":"Artist"}}
        """;

    private static string ITunesPage(params string[] hits) =>
        $$$"""{"resultCount":{{{hits.Length}}},"results":[{{{string.Join(",", hits)}}}]}""";

    private static string ITunesHit(string title, string artist, int lengthMs, string collection) =>
        $$$"""
        {"wrapperType":"track","kind":"song","trackId":{{{lengthMs}}},
         "artistName":"{{{artist}}}","collectionName":"{{{collection}}}","trackName":"{{{title}}}",
         "trackTimeMillis":{{{lengthMs}}},"releaseDate":"2010-06-01T07:00:00Z"}
        """;

    private static string Length(int? lengthMs) =>
        lengthMs is null ? "null" : lengthMs.Value.ToString(CultureInfo.InvariantCulture);
}
