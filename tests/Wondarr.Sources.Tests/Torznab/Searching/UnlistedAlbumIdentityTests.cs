using FluentAssertions;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Sources.Torznab.Searching;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Searching;

/// <summary>
/// A post whose file list is unknown is accepted on its name alone: it starts with the song's artist and names
/// the song's album. These build the candidate as a search does (the release parser and the version reading of
/// <c>ContainerCandidates</c>) and judge it with the engine, for the names a dotted scene release can have.
/// </summary>
public sealed class UnlistedAlbumIdentityTests
{
    private const string HdTracks = "Norah.Jones.-.Come.Away.With.Me.24bit.192Khz.hdtracks.FLAC";
    private const string ComeAway = "Come Away with Me";

    private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_dotted_name_with_the_album_and_its_tags_in_one_field_names_the_songs_album()
    {
        var candidate = Build(HdTracks, Wanted(songAlbum: ComeAway));

        candidate.Parsed.Album.Should().BeNull("the one hyphen leaves the album and its tags in one field");

        var decision = Judge(NorahContext(), candidate);

        decision.Rejections.Should().BeEmpty();
        decision.Accepted.Should().BeTrue();
    }

    [Fact]
    public void A_song_filed_under_singles_accepts_the_same_name_through_the_album_the_search_targeted()
    {
        var candidate = Build(HdTracks, Wanted(targetAlbum: ComeAway));

        var decision = Judge(NorahContext() with { AlbumTitle = "Singles", AlbumKind = AlbumContextKind.PseudoSingles }, candidate);

        decision.Rejections.Should().BeEmpty();
        decision.Accepted.Should().BeTrue();
    }

    [Fact]
    public void The_same_name_is_not_the_album_of_a_song_on_another_album()
    {
        var candidate = Build(HdTracks, Wanted(songAlbum: "Feels Like Home"));

        var decision = Judge(NorahContext() with { AlbumTitle = "Feels Like Home" }, candidate);

        decision.Accepted.Should().BeFalse();
        decision.Rejections.Select(rejection => rejection.Reason).Should().Contain(RejectionReason.TitleMismatch);
    }

    [Fact]
    public void A_live_album_found_by_its_words_is_still_a_live_album()
    {
        // The words are the studio album's, and the name says it is live: every track on it is a live recording.
        var candidate = Build("Norah.Jones.-.Come.Away.With.Me.Live.2003.FLAC", Wanted(songAlbum: ComeAway));

        candidate.Parsed.VersionFlags.Should().HaveFlag(VersionFlags.Live);

        var decision = Judge(NorahContext(), candidate);

        decision.Accepted.Should().BeFalse();
        decision.Rejections.Select(rejection => rejection.Reason).Should().Contain(RejectionReason.VersionMismatch);
        decision.Rejections.Select(rejection => rejection.Reason).Should().NotContain(RejectionReason.TitleMismatch);
    }

    [Theory]
    [InlineData("Love", "X.-.Love.Songs.Collection.FLAC")]
    [InlineData("Love", "X.-.Songs.Of.Love.And.Hate.FLAC")]
    public void A_short_album_is_not_found_inside_a_longer_field(string album, string title)
    {
        var candidate = Build(title, Wanted(songAlbum: album) with { Title = "Johnny Hit and Run Pauline", SongArtist = "X" });

        var decision = Judge(NorahContext() with { SongTitle = "Johnny Hit and Run Pauline", MainArtists = ["X"], AlbumTitle = album }, candidate);

        decision.Accepted.Should().BeFalse();
        decision.Rejections.Select(rejection => rejection.Reason).Should().Contain(RejectionReason.TitleMismatch);
    }

    [Fact]
    public void An_album_named_like_the_artist_is_not_found_in_the_artist()
    {
        var candidate = Build("Boston.-.Third.Stage.FLAC", Wanted(songAlbum: "Boston") with { Title = "More Than a Feeling", SongArtist = "Boston" });

        var decision = Judge(NorahContext() with { SongTitle = "More Than a Feeling", MainArtists = ["Boston"], AlbumTitle = "Boston" }, candidate);

        decision.Accepted.Should().BeFalse();
        decision.Rejections.Select(rejection => rejection.Reason).Should().Contain(RejectionReason.TitleMismatch);
    }

    private static ContainerMatchRequest Wanted(string? songAlbum = null, string? targetAlbum = null) =>
        new("Seven Years", VersionFlags.None, 2, 145_000, 1, SongArtist: "Norah Jones", SongAlbum: songAlbum, TargetAlbum: targetAlbum);

    /// <summary>A usenet post whose file list is unknown (a RAR set), as a search turns it into a candidate.</summary>
    private static Candidate Build(string title, ContainerMatchRequest wanted)
    {
        var release = new IndexerRelease(
            title,
            "guid-1",
            "https://usenet.example/get/1",
            null,
            null,
            400_000_000,
            null,
            null,
            null,
            12,
            [3010],
            null,
            null,
            DownloadProtocol.Usenet,
            1,
            "Indexer",
            null);

        return ContainerCandidates.Build(DownloadProtocol.Usenet, new ContainerListing(release, null, null, 400_000_000), wanted, title, Now)!;
    }

    private static DecisionContext NorahContext() => new()
    {
        SongTitle = "Seven Years",
        MainArtists = ["Norah Jones"],
        SongDurationMs = 145_000,
        AlbumTitle = ComeAway,
        AlbumKind = AlbumContextKind.Album,
        TrackNo = 2,
        Profile = SeedData.QualityProfiles.Single(profile => profile.Id == SeedData.LosslessProfileId),
        Qualities = SeedData.Qualities.ToDictionary(quality => quality.Id, quality => quality),
    };

    private static CandidateDecision Judge(DecisionContext context, Candidate candidate) =>
        new DecisionEngine().Evaluate(context, [candidate]).Single();
}
