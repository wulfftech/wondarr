using System.Text.Json;
using System.Text.Json.Serialization;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Organizer;
using FluentAssertions;
using Xunit;

namespace Wondarr.Core.Tests.Organizer;

public class AlbumPolicyEngineTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "album-policy.json");

    // Not JsonSerializerDefaults.Web: it prepends a camelCase string-enum converter that would claim
    // VersionFlags and the PascalCase fixture names before these converters get a look in.
    private static readonly JsonSerializerOptions FixtureOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new VersionFlagsJsonConverter(), new JsonStringEnumConverter() },
    };

    private static readonly IReadOnlyList<FixtureCase> Cases = LoadFixture();

    /// <summary>The fixture's case names, so the theory data stays serializable and the runs stay readable.</summary>
    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();

        foreach (var testCase in Cases)
        {
            data.Add(testCase.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Matches_the_golden_assignments_of_a_fixture_case(string name)
    {
        var testCase = Cases.Single(candidate => candidate.Name == name);

        var actual = NewEngine().Assign(testCase.Input);

        actual.Should().HaveCount(testCase.Expected.Count);

        for (var index = 0; index < testCase.Expected.Count; index++)
        {
            AssertAssignment(actual[index], testCase.Expected[index], name);
        }
    }

    [Fact]
    public void Keeps_a_two_song_album_out_of_real_albums_when_the_minimum_is_three()
    {
        var input = new AlbumPolicyInput
        {
            Policy = AlbumPolicy.FewestAlbums,
            MinTracksPerRealAlbum = 3,
            LibraryName = "Music",
            Songs =
            [
                Song("a1", "a", "Artist A", "2000-05-01", Option("k1", "Album K", 1)),
                Song("a2", "a", "Artist A", "2000-06-01", Option("k1", "Album K", 2)),
            ],
        };

        var actual = NewEngine().Assign(input);

        actual.Should().HaveCount(2);
        actual.Should().OnlyContain(assignment => assignment.Kind == AlbumContextKind.PseudoSingles);
        actual[0].AlbumKey.Should().Be("00000000-0000-0000-0000-000000000001");
        actual[0].AlbumTitle.Should().Be("Singles");
        actual[0].TrackNo.Should().Be(1);
        actual[1].TrackNo.Should().Be(2);

        // The pseudo-album keeps one date: the earliest year of the songs that land in it.
        actual[0].Date.Should().Be("2000");
    }

    [Fact]
    public void Breaks_a_greedy_tie_on_key_ordinal_when_coverage_type_and_date_are_equal()
    {
        var input = new AlbumPolicyInput
        {
            Policy = AlbumPolicy.FewestAlbums,
            MinTracksPerRealAlbum = 2,
            LibraryName = "Music",
            Songs =
            [
                Song(
                    "t1",
                    "z",
                    "Z",
                    "2000-01-01",
                    Option("zz", "Zeta", 1, date: "2000-01-01"),
                    Option("aa", "Alpha", 5, date: "2000-01-01")),
                Song(
                    "t2",
                    "z",
                    "Z",
                    "2000-01-01",
                    Option("zz", "Zeta", 2, date: "2000-01-01"),
                    Option("aa", "Alpha", 6, date: "2000-01-01")),
            ],
        };

        var actual = NewEngine().Assign(input);

        actual.Should().OnlyContain(assignment => assignment.AlbumKey == "aa");
        actual[0].AlbumTitle.Should().Be("Alpha");
        actual[0].TrackNo.Should().Be(5);
        actual[1].TrackNo.Should().Be(6);
    }

    [Fact]
    public void Joins_an_existing_album_by_exact_key_and_takes_the_tag_fields_of_that_option()
    {
        var input = new AlbumPolicyInput
        {
            Policy = AlbumPolicy.FewestAlbums,
            MinTracksPerRealAlbum = 2,
            LibraryName = "Music",
            ExistingAlbums =
            [
                new ExistingAlbum
                {
                    AlbumKey = "k1",
                    Kind = AlbumContextKind.Album,
                    ArtistKey = "a",
                    AlbumTitle = "Original",
                    AlbumArtist = "Artist A",
                    Date = "2001-01-01",
                    MbReleaseId = "k1",
                    MbReleaseGroupId = "g1",
                    TrackCount = 5,
                    MaxTrackNo = 5,
                    IsVariousArtists = false,
                },
            ],
            Songs =
            [
                new SongToPlace
                {
                    Ref = "s1",
                    ArtistKey = "a",
                    ArtistName = "Artist A",
                    OriginalDate = "2000-01-01",
                    Options =
                    [
                        new ReleaseOption
                        {
                            Key = "k1",
                            MbReleaseId = "k1b",
                            MbReleaseGroupId = "g9",
                            Title = "Original (Deluxe)",
                            AlbumArtist = "Artist A",
                            PrimaryType = "Album",
                            Date = "2002-02-02",
                            ReleaseGroupFirstDate = "2000-01-01",
                            TrackNo = 7,
                            TotalTracks = 12,
                        },
                        new ReleaseOption
                        {
                            Key = "k2",
                            MbReleaseId = "k2",
                            MbReleaseGroupId = "g1",
                            Title = "Reissue",
                            AlbumArtist = "Artist A",
                            PrimaryType = "Album",
                            Date = "2003-03-03",
                            TrackNo = 99,
                            TotalTracks = 4,
                        },
                    ],
                },
            ],
        };

        var actual = NewEngine().Assign(input);

        var assignment = actual.Should().ContainSingle().Subject;

        // The exact key wins over the release-group match: title, date and ids come from the existing
        // album, the track numbers from the option it matched through.
        assignment.Kind.Should().Be(AlbumContextKind.Album);
        assignment.AlbumKey.Should().Be("k1");
        assignment.AlbumTitle.Should().Be("Original");
        assignment.AlbumArtist.Should().Be("Artist A");
        assignment.Date.Should().Be("2001-01-01");
        assignment.MbReleaseId.Should().Be("k1");
        assignment.MbReleaseGroupId.Should().Be("g1");
        assignment.TrackNo.Should().Be(7);
        assignment.DiscNo.Should().Be(1);
        assignment.TotalTracks.Should().Be(12);
        assignment.OriginalDate.Should().Be("2000-01-01");
        assignment.IsVariousArtists.Should().BeFalse();
    }

    private static void AssertAssignment(AlbumAssignment actual, AlbumAssignment expected, string caseName)
    {
        actual.SongRef.Should().Be(expected.SongRef, "'{0}'", caseName);
        actual.Kind.Should().Be(expected.Kind, "'{0}'", caseName);
        actual.AlbumTitle.Should().Be(expected.AlbumTitle, "'{0}'", caseName);
        actual.AlbumArtist.Should().Be(expected.AlbumArtist, "'{0}'", caseName);
        actual.AlbumKey.Should().Be(expected.AlbumKey, "'{0}'", caseName);
        actual.MbReleaseId.Should().Be(expected.MbReleaseId, "'{0}'", caseName);
        actual.MbReleaseGroupId.Should().Be(expected.MbReleaseGroupId, "'{0}'", caseName);
        actual.TrackNo.Should().Be(expected.TrackNo, "'{0}'", caseName);
        actual.DiscNo.Should().Be(expected.DiscNo, "'{0}'", caseName);
        actual.TotalTracks.Should().Be(expected.TotalTracks, "'{0}'", caseName);
        actual.Date.Should().Be(expected.Date, "'{0}'", caseName);
        actual.OriginalDate.Should().Be(expected.OriginalDate, "'{0}'", caseName);
        actual.IsVariousArtists.Should().Be(expected.IsVariousArtists, "'{0}'", caseName);
    }

    /// <summary>An engine whose synthetic keys count up from <c>…0001</c>, as the defaulted fixture fields expect.</summary>
    private static AlbumPolicyEngine NewEngine()
    {
        var next = 0;

        return new AlbumPolicyEngine(() => new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)Interlocked.Increment(ref next)));
    }

    private static SongToPlace Song(string reference, string artistKey, string artistName, string? originalDate, params ReleaseOption[] options) => new()
    {
        Ref = reference,
        ArtistKey = artistKey,
        ArtistName = artistName,
        OriginalDate = originalDate,
        Options = options,
    };

    private static ReleaseOption Option(string key, string title, int trackNo, string date = "2000-01-01") => new()
    {
        Key = key,
        MbReleaseId = key,
        MbReleaseGroupId = key + "g",
        Title = title,
        AlbumArtist = "Album Artist",
        PrimaryType = "Album",
        Date = date,
        ReleaseGroupFirstDate = date,
        TrackNo = trackNo,
        TotalTracks = 10,
    };

    private static List<FixtureCase> LoadFixture()
    {
        using var stream = File.OpenRead(FixturePath);

        return JsonSerializer.Deserialize<List<FixtureCase>>(stream, FixtureOptions) ?? [];
    }

    private sealed record FixtureCase(string Name, AlbumPolicyInput Input, IReadOnlyList<AlbumAssignment> Expected);
}
