using FluentAssertions;
using Wondarr.Core.Discover;
using Xunit;

namespace Wondarr.Core.Tests.Discover;

/// <summary>The pure part of Discover: merging the sources' lists, then filtering and capping them.</summary>
public sealed class SuggestionMergerTests
{
    private const string Mbid = "f3bba4cd-8018-468b-902e-bc8f029593e5";

    [Fact]
    public void The_same_recording_from_two_sources_is_one_suggestion_that_lists_both()
    {
        var runs = new[]
        {
            Run("listenbrainz", 1.0, new RecommendationCandidate("Teardrop", "Massive Attack", "Mezzanine", Mbid)),
            Run("lastfm", 1.0, new RecommendationCandidate("Teardrop (Remastered)", "Massive Attack", MbRecordingId: Mbid)),
        };

        var merged = SuggestionMerger.Merge(runs);

        var only = merged.Should().ContainSingle().Subject;
        only.Sources.Should().Equal("listenbrainz", "lastfm");
        only.Title.Should().Be("Teardrop", "the first source's spelling wins");
        only.Album.Should().Be("Mezzanine");
        only.Score.Should().BeApproximately(2.0, 1e-9, "a lone first-ranked candidate scores the full weight from each source");
    }

    [Fact]
    public void A_candidate_without_an_mbid_merges_on_normalised_artist_and_title()
    {
        var runs = new[]
        {
            Run("listenbrainz", 1.0, new RecommendationCandidate("Teardrop", "Massive Attack", MbRecordingId: Mbid)),
            Run("deezer", 0.6, new RecommendationCandidate("TEARDROP", "massive attack", DeezerId: 99, DurationMs: 330_000)),
        };

        var merged = SuggestionMerger.Merge(runs);

        var only = merged.Should().ContainSingle().Subject;
        only.MbRecordingId.Should().Be(Mbid);
        only.DeezerId.Should().Be(99, "what only the later source knew is kept");
        only.DurationMs.Should().Be(330_000);
        only.Score.Should().BeApproximately(1.6, 1e-9);
    }

    [Fact]
    public void An_isrc_merges_two_candidates_whose_names_differ()
    {
        var runs = new[]
        {
            Run("a", 1.0, new RecommendationCandidate("Song One", "Artist", Isrc: "gbaaa1234567")),
            Run("b", 1.0, new RecommendationCandidate("Totally Different Title", "Artist", Isrc: "GBAAA1234567")),
        };

        SuggestionMerger.Merge(runs).Should().ContainSingle();
    }

    [Fact]
    public void A_rank_becomes_a_weighted_score_between_zero_and_the_weight()
    {
        var run = new SourceRun(
            "deezer",
            0.6,
            RecommendationSourceStatus.Answered,
            [
                [
                    new RecommendationCandidate("One", "A"),
                    new RecommendationCandidate("Two", "B"),
                    new RecommendationCandidate("Three", "C"),
                    new RecommendationCandidate("Four", "D"),
                ],
            ]);

        var merged = SuggestionMerger.Merge([run]);

        double[] expected = [0.6, 0.45, 0.3, 0.15];
        for (var index = 0; index < expected.Length; index++)
        {
            merged[index].Score.Should().BeApproximately(expected[index], 1e-9);
        }

        merged.Select(item => item.Title).Should().Equal("One", "Two", "Three", "Four");
    }

    [Fact]
    public void A_song_in_the_library_the_seed_and_a_dismissed_song_are_dropped()
    {
        var merged = SuggestionMerger.Merge(
        [
            Run(
                "listenbrainz",
                1.0,
                new RecommendationCandidate("Owned By Mbid", "Artist One", MbRecordingId: Mbid),
                new RecommendationCandidate("Owned By Name", "The Artist Two"),
                new RecommendationCandidate("Owned By Deezer", "Artist Three", DeezerId: 7),
                new RecommendationCandidate("Owned By Isrc", "Artist Four", Isrc: "usabc1234567"),
                new RecommendationCandidate("The Seed", "Seed Artist"),
                new RecommendationCandidate("Dismissed By Name", "Artist Five"),
                new RecommendationCandidate("Dismissed By Mbid", "Artist Six", MbRecordingId: "11111111-1111-1111-1111-111111111111"),
                new RecommendationCandidate("Keeper", "Artist Seven")),
        ]);

        List<LibraryRow> library =
        [
            new(Mbid, null, [], "Something Else", "Someone", "Someone"),
            new(null, null, [], "owned by name", "Artist Two", "Artist Two"),
            new(null, 7, [], "Elsewhere", "Nobody", "Nobody"),
            new(null, null, ["USABC1234567"], "Elsewhere Again", "Nobody Else", "Nobody Else"),
        ];

        RecommendationSeed[] seeds = [new(1, "The Seed", "Seed Artist", null, null)];

        DiscoverDismissal[] dismissals =
        [
            new() { ArtistKey = "artist five", TitleKey = "dismissed by name" },
            new() { MbRecordingId = "11111111-1111-1111-1111-111111111111", ArtistKey = "x", TitleKey = "y" },
        ];

        var kept = SuggestionMerger.Filter(merged, library, seeds, dismissals, maxPerArtist: 3);

        kept.Select(item => item.Title).Should().Equal("Keeper");
    }

    [Fact]
    public void An_artist_keeps_only_its_best_suggestions()
    {
        var merged = SuggestionMerger.Merge(
        [
            Run(
                "listenbrainz",
                1.0,
                new RecommendationCandidate("A1", "Same Artist"),
                new RecommendationCandidate("A2", "Same Artist"),
                new RecommendationCandidate("Other", "Different Artist"),
                new RecommendationCandidate("A3", "The Same Artist"),
                new RecommendationCandidate("A4", "Same Artist")),
        ]);

        var kept = SuggestionMerger.Filter(merged, [], [], [], maxPerArtist: 3);

        kept.Select(item => item.Title).Should().Equal("A1", "A2", "Other", "A3");
    }

    private static SourceRun Run(string name, double weight, params RecommendationCandidate[] candidates) =>
        new(name, weight, RecommendationSourceStatus.Answered, [candidates]);
}
