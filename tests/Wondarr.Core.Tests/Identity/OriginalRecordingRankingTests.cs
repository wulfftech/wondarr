using Wondarr.Core.Identity;
using Wondarr.Core.Metadata.MusicBrainz;
using FluentAssertions;
using Xunit;

namespace Wondarr.Core.Tests.Identity;

/// <summary>
/// The original-recording ranking (MATCHING_ENGINE.md §6.3): which Official Album a recording is on,
/// and how same-titled recordings are ordered.
/// </summary>
public sealed class OriginalRecordingRankingTests
{
    [Fact]
    public void An_official_album_gives_its_date()
    {
        var recording = Recording(Release("2000-05-30", "Official", "Album"));

        OriginalRecordingRanking.OfficialAlbumDate(recording, queryAskedLive: false).Should().Be("2000-05-30");
    }

    [Theory]
    [InlineData("Compilation")]
    [InlineData("Soundtrack")]
    [InlineData("Live")]
    public void A_compilation_soundtrack_or_live_album_is_not_an_official_album(string secondaryType)
    {
        var recording = Recording(Release("2000-05-30", "Official", "Album", secondaryType));

        OriginalRecordingRanking.OfficialAlbumDate(recording, queryAskedLive: false).Should().BeNull();
    }

    [Fact]
    public void A_live_album_counts_when_the_query_asked_for_live()
    {
        var recording = Recording(Release("2000-05-30", "Official", "Album", "Live"));

        OriginalRecordingRanking.OfficialAlbumDate(recording, queryAskedLive: true).Should().Be("2000-05-30");
    }

    [Theory]
    [InlineData("Single")]
    [InlineData("EP")]
    public void A_single_or_an_ep_is_not_an_album(string primaryType)
    {
        var recording = Recording(Release("2000-05-30", "Official", primaryType));

        OriginalRecordingRanking.OfficialAlbumDate(recording, queryAskedLive: false).Should().BeNull();
    }

    [Fact]
    public void A_single_a_promo_or_a_compilation_before_the_album_does_not_hide_it()
    {
        var recording = Recording(
            Release("1999-08-24", "Official", "Single"),
            Release("1999-09-01", "Official", "Album", "Compilation"),
            Release("2000-02-01", "Promotion", "Album"),
            Release(null, "Official", "Album"),
            Release("2000-05-30", "Official", "Album"));

        OriginalRecordingRanking.OfficialAlbumDate(recording, queryAskedLive: false).Should().Be("2000-05-30");
    }

    [Fact]
    public void A_year_only_compilation_does_not_make_the_same_year_album_a_later_one()
    {
        var album = Recording(Release("2000-05-30", "Official", "Album"), Release("2000", "Official", "Album", "Compilation"));
        var compilationOnly = Recording(Release("2000", "Official", "Album", "Compilation"));

        OriginalRecordingRanking.OfficialAlbumDate(album, queryAskedLive: false).Should().Be("2000-05-30");
        OriginalRecordingRanking.OfficialAlbumDate(compilationOnly, queryAskedLive: false).Should().BeNull();
    }

    [Fact]
    public void A_recording_with_no_releases_is_on_no_album()
    {
        OriginalRecordingRanking.OfficialAlbumDate(Recording(), queryAskedLive: false).Should().BeNull();
    }

    [Theory]
    [InlineData("1999", "2000-05-30", true)]
    [InlineData("2000-05-30", "2000-09-01", true)]
    [InlineData("2000-09-01", "2000-05-30", false)]
    [InlineData("2000", "2000-05-30", false)]
    [InlineData("2000-05-30", "2000", false)]
    [InlineData("2000-05-30", "2000-05-30", false)]
    public void Compares_dates_by_their_common_prefix(string date, string other, bool earlier)
    {
        OriginalRecordingRanking.IsEarlier(date, other).Should().Be(earlier);
    }

    [Fact]
    public void Ranks_the_original_before_the_earlier_and_better_scored_namesakes()
    {
        var key = OriginalRecordingRanking.GroupKey("Nine Days", "Story of a Girl");
        var order = OriginalRecordingRanking.Order(
        [
            new OriginalRecordingRanking.Entry(100, key, null, "1999", 197_600),
            new OriginalRecordingRanking.Entry(100, key, null, "2000", 197_600),
            new OriginalRecordingRanking.Entry(92.5, key, "2000-05-30", "2000-05-30", 189_000),
        ]);

        order.Should().Equal(2, 0, 1);
    }

    [Fact]
    public void A_single_before_the_album_does_not_stop_the_album_recording_being_the_original()
    {
        var key = OriginalRecordingRanking.GroupKey("Nine Days", "Story of a Girl");
        var order = OriginalRecordingRanking.Order(
        [
            new OriginalRecordingRanking.Entry(100, key, null, "1999-10", 189_000),
            new OriginalRecordingRanking.Entry(95, key, "2000-05-30", "1999-10", 189_000),
        ]);

        order.Should().Equal(1, 0);
    }

    [Fact]
    public void A_later_re_recording_on_a_later_album_is_not_the_original()
    {
        var key = OriginalRecordingRanking.GroupKey("Nine Days", "Story of a Girl");
        var order = OriginalRecordingRanking.Order(
        [
            new OriginalRecordingRanking.Entry(100, key, "2010-01-01", "2010", 190_000),
            new OriginalRecordingRanking.Entry(95, key, "2000-05-30", "2000", 189_000),
        ]);

        order.Should().Equal(1, 0);
    }

    [Fact]
    public void A_year_only_date_ties_with_a_full_date_of_the_same_year()
    {
        var key = OriginalRecordingRanking.GroupKey("Nine Days", "Story of a Girl");
        var order = OriginalRecordingRanking.Order(
        [
            new OriginalRecordingRanking.Entry(90, key, "2000-05-30", "2000", 189_000),
            new OriginalRecordingRanking.Entry(100, key, "2000", "2000", 189_000),
        ]);

        // Neither is earlier, so both are original and the score decides.
        order.Should().Equal(1, 0);
    }

    [Fact]
    public void Breaks_a_tie_by_date_and_then_by_the_length_nearest_the_most_common_one()
    {
        var key = OriginalRecordingRanking.GroupKey("Nine Days", "Story of a Girl");
        var order = OriginalRecordingRanking.Order(
        [
            new OriginalRecordingRanking.Entry(100, key, "2000", "2001", 189_000),
            new OriginalRecordingRanking.Entry(100, key, "2000", "2000", 150_000),
            new OriginalRecordingRanking.Entry(100, key, "2000", "2000", 197_000),
            new OriginalRecordingRanking.Entry(100, key, "2000", "2000", 198_500),
            new OriginalRecordingRanking.Entry(100, key, "2000", "2000", 197_600),
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
            new OriginalRecordingRanking.Entry(100, story, null, "2007", 197_600),
            new OriginalRecordingRanking.Entry(98, absolutely, null, "2000", 189_000),
            new OriginalRecordingRanking.Entry(90, story, "2000-05-30", "2000", 189_000),
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
