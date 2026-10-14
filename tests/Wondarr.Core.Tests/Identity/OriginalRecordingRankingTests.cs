using Wondarr.Core.Identity;
using Wondarr.Core.Metadata.MusicBrainz;
using FluentAssertions;
using Xunit;

namespace Wondarr.Core.Tests.Identity;

/// <summary>
/// The original-recording ranking (MATCHING_ENGINE.md §6.3): which recording counts as the original
/// album one, and how same-titled recordings are ordered.
/// </summary>
public sealed class OriginalRecordingRankingTests
{
    [Fact]
    public void An_official_album_as_the_earliest_release_is_the_original()
    {
        var recording = Recording(Release("2000-05-30", "Official", "Album"));

        OriginalRecordingRanking.IsOriginalAlbum(recording, queryAskedLive: false).Should().BeTrue();
    }

    [Theory]
    [InlineData("Compilation")]
    [InlineData("Soundtrack")]
    [InlineData("Live")]
    public void A_compilation_soundtrack_or_live_album_is_not_the_original(string secondaryType)
    {
        var recording = Recording(Release("2000-05-30", "Official", "Album", secondaryType));

        OriginalRecordingRanking.IsOriginalAlbum(recording, queryAskedLive: false).Should().BeFalse();
    }

    [Fact]
    public void A_live_album_is_the_original_when_the_query_asked_for_live()
    {
        var recording = Recording(Release("2000-05-30", "Official", "Album", "Live"));

        OriginalRecordingRanking.IsOriginalAlbum(recording, queryAskedLive: true).Should().BeTrue();
    }

    [Fact]
    public void A_single_is_not_an_album()
    {
        var recording = Recording(Release("2000-05-30", "Official", "Single"));

        OriginalRecordingRanking.IsOriginalAlbum(recording, queryAskedLive: false).Should().BeFalse();
    }

    [Fact]
    public void An_earlier_compilation_means_the_album_is_not_the_earliest_release()
    {
        var recording = Recording(
            Release("1999-08-24", "Official", "Album", "Compilation"),
            Release("2000-05-30", "Official", "Album"));

        OriginalRecordingRanking.IsOriginalAlbum(recording, queryAskedLive: false).Should().BeFalse();
    }

    [Fact]
    public void A_promo_single_or_an_undated_release_does_not_hide_the_album()
    {
        var recording = Recording(
            Release("2000-02-01", "Promotion", "Single"),
            Release(null, "Official", "Album", "Compilation"),
            Release("2000-05-30", "Official", "Album"));

        OriginalRecordingRanking.IsOriginalAlbum(recording, queryAskedLive: false).Should().BeTrue();
    }

    [Fact]
    public void A_recording_with_no_releases_is_not_the_original()
    {
        OriginalRecordingRanking.IsOriginalAlbum(Recording(), queryAskedLive: false).Should().BeFalse();
    }

    [Fact]
    public void Ranks_the_original_before_the_earlier_and_better_scored_namesakes()
    {
        var key = OriginalRecordingRanking.GroupKey("Nine Days", "Story of a Girl");
        var order = OriginalRecordingRanking.Order(
        [
            new OriginalRecordingRanking.Entry(100, key, false, "1999", 197_600),
            new OriginalRecordingRanking.Entry(100, key, false, "2000", 197_600),
            new OriginalRecordingRanking.Entry(92.5, key, true, "2000-05-30", 189_000),
        ]);

        order.Should().Equal(2, 0, 1);
    }

    [Fact]
    public void Breaks_a_tie_by_date_and_then_by_the_length_nearest_the_most_common_one()
    {
        var key = OriginalRecordingRanking.GroupKey("Nine Days", "Story of a Girl");
        var order = OriginalRecordingRanking.Order(
        [
            new OriginalRecordingRanking.Entry(100, key, true, "2001", 189_000),
            new OriginalRecordingRanking.Entry(100, key, true, "2000", 150_000),
            new OriginalRecordingRanking.Entry(100, key, true, "2000", 197_000),
            new OriginalRecordingRanking.Entry(100, key, true, "2000", 198_500),
            new OriginalRecordingRanking.Entry(100, key, true, "2000", 197_600),
        ]);

        // The 2001 one is last on its date alone. Among the 2000 ones the most common length is 197.0 s
        // (three lengths within two seconds of it), so 197.0 beats 197.6, which beats 198.5 and 150.
        order.Should().Equal(2, 4, 3, 1, 0);
    }

    [Fact]
    public void Ranks_recordings_with_other_titles_by_score_and_keeps_each_group_together()
    {
        var story = OriginalRecordingRanking.GroupKey("Nine Days", "Story of a Girl");
        var absolutely = OriginalRecordingRanking.GroupKey("Nine Days", "Absolutely (Story of a Girl)");
        var order = OriginalRecordingRanking.Order(
        [
            new OriginalRecordingRanking.Entry(100, story, false, "2007", 197_600),
            new OriginalRecordingRanking.Entry(98, absolutely, false, "2000", 189_000),
            new OriginalRecordingRanking.Entry(90, story, true, "2000", 189_000),
        ]);

        order.Should().Equal(2, 0, 1);
    }

    [Fact]
    public void Folds_the_artist_and_the_title_when_grouping()
    {
        OriginalRecordingRanking.GroupKey("The Beatles", "Hey Jude")
            .Should().Be(OriginalRecordingRanking.GroupKey("Beatles", "hey  jude"));
    }

    [Theory]
    [InlineData(new[] { 197_600, 197_000, 189_000, 198_000 }, 197_000)]
    [InlineData(new[] { 189_000 }, 189_000)]
    [InlineData(new[] { 100_000, 200_000 }, 100_000)]
    public void Finds_the_most_common_length_within_two_seconds(int[] lengths, int expected)
    {
        OriginalRecordingRanking.MostCommonLength(lengths).Should().Be(expected);
    }

    [Fact]
    public void Has_no_common_length_without_lengths()
    {
        OriginalRecordingRanking.MostCommonLength([]).Should().BeNull();
    }

    private static MbRecording Recording(params MbRelease[] releases) => new()
    {
        Id = "00000000-0000-4000-8000-000000000001",
        Title = "Story of a Girl",
        Releases = releases,
    };

    private static MbRelease Release(string? date, string status, string primaryType, params string[] secondaryTypes) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Title = "Some release",
        Status = status,
        Date = date,
        ReleaseGroup = new MbReleaseGroup { Id = Guid.NewGuid().ToString(), PrimaryType = primaryType, SecondaryTypes = secondaryTypes },
    };
}
