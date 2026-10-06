using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;
using Wondarr.Sources.YouTube;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// The mapper over the results the recorded responses parse into: every result becomes a candidate
/// keyed by its video id, and nothing here decides whether the result may be grabbed.
/// </summary>
public class YouTubeCandidateMapperTests
{
    [Fact]
    public void Maps_an_art_track_into_a_candidate()
    {
        var result = new InnertubeResult(
            "4D7u5KF7SP8",
            "Get Lucky (feat. Pharrell Williams and Nile Rodgers)",
            ["Daft Punk", "Pharrell Williams", "Nile Rodgers"],
            "Random Access Memories",
            370_000,
            InnertubeVideoTypes.ArtTrack,
            IsExplicit: false);

        var candidate = YouTubeCandidateMapper.Map(result, "daft punk get lucky");

        candidate.SourceType.Should().Be(SourceTypes.YouTube);
        candidate.BlocklistKey.Should().Be("youtube:4D7u5KF7SP8");
        candidate.DisplayName.Should().Be("Get Lucky (feat. Pharrell Williams and Nile Rodgers)");
        candidate.RemotePath.Should().Be("4D7u5KF7SP8");
        candidate.Provider.Should().Be("Daft Punk");
        candidate.Parsed.Title.Should().Be("Get Lucky");
        candidate.Parsed.Artist.Should().Be("Daft Punk");
        candidate.Parsed.Album.Should().Be("Random Access Memories");
        candidate.Parsed.VersionFlags.Should().Be(VersionFlags.None);
        candidate.DurationMs.Should().Be(370_000);
        candidate.Extension.Should().BeNull();
        candidate.QualityId.Should().Be(YouTubeCandidateMapper.Opus160QualityId);
        candidate.Availability.FreeUploadSlot.Should().BeTrue();
        candidate.Query.Should().Be("daft punk get lucky");
    }

    [Fact]
    public void Strips_the_version_hints_a_title_carries()
    {
        var result = new InnertubeResult(
            "Rgrt_8mXrK8",
            "Get Lucky (Radio Edit - feat. Pharrell Williams and Nile Rodgers)",
            ["Daft Punk"],
            null,
            249_000,
            InnertubeVideoTypes.ArtTrack,
            IsExplicit: false);

        var candidate = YouTubeCandidateMapper.Map(result, "daft punk get lucky");

        candidate.Parsed.Title.Should().Be("Get Lucky");
        candidate.Parsed.VersionFlags.Should().Be(VersionFlags.RadioEdit);
        // The parser keeps the whole bracketed group as one hint, brackets and all.
        candidate.Parsed.VersionHints.Should().Equal("(Radio Edit - feat. Pharrell Williams and Nile Rodgers)");
    }

    [Fact]
    public void Maps_a_user_upload_the_same_way()
    {
        var result = new InnertubeResult(
            "CCHdMIEGaaM",
            "Daft Punk - Get Lucky (Official Video) feat. Pharrell Williams and Nile Rodgers",
            ["convar HUN"],
            null,
            248_000,
            InnertubeVideoTypes.UserUpload,
            IsExplicit: false);

        var candidate = YouTubeCandidateMapper.Map(result, "daft punk get lucky");

        candidate.BlocklistKey.Should().Be("youtube:CCHdMIEGaaM");
        candidate.Provider.Should().Be("convar HUN");
        candidate.Parsed.Artist.Should().Be("convar HUN");
        candidate.QualityId.Should().Be(YouTubeCandidateMapper.Opus160QualityId);
    }

    [Fact]
    public void Maps_the_card_and_the_shelf_once_per_video()
    {
        var card = new InnertubeResult(
            "HzdD8kbDzZA", "Take on Me", ["a-ha"], null, 226_000, InnertubeVideoTypes.ArtTrack, IsExplicit: false);
        var shelfItem = new InnertubeResult(
            "HzdD8kbDzZA", "Take on Me", ["a-ha"], "Hunting High and Low", 226_000, InnertubeVideoTypes.ArtTrack, IsExplicit: false);
        var other = new InnertubeResult(
            "Z4GMUlCBgd0", "The Night The Lights Went Out In Georgia (Official Music Video)",
            ["Reba McEntire"], null, null, InnertubeVideoTypes.OfficialVideo, IsExplicit: false);

        var candidates = YouTubeCandidateMapper.Map([card, shelfItem, other], "USWB19901214");

        candidates.Should().HaveCount(2);
        candidates[0].BlocklistKey.Should().Be("youtube:HzdD8kbDzZA");
        candidates[0].Parsed.Album.Should().BeNull();
        candidates[1].BlocklistKey.Should().Be("youtube:Z4GMUlCBgd0");
        candidates.Select(candidate => candidate.Query).Should().OnlyContain(query => query == "USWB19901214");
    }

    [Fact]
    public void Classifies_the_music_video_types()
    {
        InnertubeVideoTypes.KindOf(InnertubeVideoTypes.ArtTrack).Should().Be(InnertubeVideoKind.ArtTrack);
        InnertubeVideoTypes.KindOf(InnertubeVideoTypes.OfficialVideo).Should().Be(InnertubeVideoKind.OfficialVideo);
        InnertubeVideoTypes.KindOf(InnertubeVideoTypes.UserUpload).Should().Be(InnertubeVideoKind.UserUpload);
        InnertubeVideoTypes.KindOf("MUSIC_VIDEO_TYPE_PRIVATELY_OWNED_TRACK").Should().Be(InnertubeVideoKind.UserUpload);
        InnertubeVideoTypes.KindOf(null).Should().Be(InnertubeVideoKind.UserUpload);
    }
}
