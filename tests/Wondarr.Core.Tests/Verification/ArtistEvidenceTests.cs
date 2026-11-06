using FluentAssertions;
using Wondarr.Core.Tagging;
using Wondarr.Core.Verification;
using Xunit;

namespace Wondarr.Core.Tests.Verification;

/// <summary>
/// Whether a file AcoustID does not know names the song's artist. The cases are production's imports of
/// 2026-11-02 to 2026-11-06 that passed on their length alone, most of them someone else's song.
/// </summary>
public sealed class ArtistEvidenceTests
{
    private static readonly string[] SaddleClub = ["The Saddle Club"];

    [Theory]
    [InlineData("The Saddle Club")]
    [InlineData("Saddle Club")]
    [InlineData("Saddle Club, The")]
    [InlineData("The Saddle Club feat. Lara Jean Marshall")]
    [InlineData("THE SADDLE CLUB")]
    public void An_artist_tag_with_the_whole_name_names_the_artist(string tag) =>
        ArtistEvidence.Judge(Tags(tag), "03 - Wonderland.mp3", SaddleClub).Kind.Should().Be(ArtistEvidenceKind.Named);

    [Theory]
    [InlineData("Diskoflex")]
    [InlineData("The Miles Away Club")]
    [InlineData("Saddle Club Cast and Crew Band")]
    public void An_artist_tag_with_someone_else_is_another_artist(string tag)
    {
        var result = ArtistEvidence.Judge(Tags(tag), "Money (Can't Buy) (Original Mix).mp3", SaddleClub);

        result.Kind.Should().Be(ArtistEvidenceKind.Other);
        result.Source.Should().Be("artist tag");
        result.Artist.Should().Be(tag);
    }

    [Fact]
    public void A_word_of_a_longer_name_is_not_the_artist()
    {
        // "Storm" by Lisa came in as this file.
        var result = ArtistEvidence.Judge(
            Tags("Al Storm & Orbit1 Feat Lisa Abbott"),
            "Falling Through (Nu Foundation Remix) - Al Storm & Orbit1 Feat Lisa Abbott.mp3",
            ["Lisa"]);

        result.Kind.Should().Be(ArtistEvidenceKind.Other);
    }

    [Fact]
    public void A_name_joined_by_and_or_an_ampersand_is_the_whole_credit() =>
        ArtistEvidence.Judge(Tags("Simon and Garfunkel"), "x.flac", ["Simon & Garfunkel"]).Kind
            .Should().Be(ArtistEvidenceKind.Named);

    [Fact]
    public void One_artist_of_a_shared_credit_names_the_artist() =>
        ArtistEvidence.Judge(Tags("Calvin Harris & Dua Lipa"), "x.flac", ["Dua Lipa"]).Kind
            .Should().Be(ArtistEvidenceKind.Named);

    [Fact]
    public void A_compilation_album_artist_does_not_stand_in_for_the_track_artist()
    {
        var result = ArtistEvidence.Judge(Tags("Artur", "Various Artists"), "01 Artur - Wonderland.mp3", SaddleClub);

        result.Kind.Should().Be(ArtistEvidenceKind.Other);
        result.Artist.Should().Be("Artur");
    }

    [Fact]
    public void The_album_artist_tag_alone_can_name_the_artist() =>
        ArtistEvidence.Judge(Tags(null, "The Saddle Club"), "x.mp3", SaddleClub).Kind.Should().Be(ArtistEvidenceKind.Named);

    [Theory]
    [InlineData("oz2004_069 - The Saddle Club - On Top Of The World.mp3")]
    [InlineData("03 - The Saddle Club - Here I Am.flac")]
    [InlineData("oz2003_037 - Saddle Club - Wonderland.mp3")]
    [InlineData("17 Saddle Club, The - Wonderland.mp3")]
    public void Without_tags_the_file_name_can_name_the_artist(string fileName) =>
        ArtistEvidence.Judge(null, fileName, SaddleClub).Kind.Should().Be(ArtistEvidenceKind.Named);

    [Theory]
    [InlineData("01 Artur - Wonderland.mp3", "Artur")]
    [InlineData("09 Laine August - Be My Friend.mp3", "Laine August")]
    [InlineData("15 Reasonable Facsimile - Every Day.flac", "Reasonable Facsimile")]
    [InlineData("06.The Admirers - Out On A Limb(1990).mp3", "The Admirers")]
    public void Without_tags_a_file_name_with_another_artist_is_another_artist(string fileName, string artist)
    {
        var result = ArtistEvidence.Judge(null, fileName, SaddleClub);

        result.Kind.Should().Be(ArtistEvidenceKind.Other);
        result.Source.Should().Be("file name");
        result.Artist.Should().Be(artist);
    }

    [Theory]
    [InlineData("14. don't ask me.mp3")]
    [InlineData("01 There's A Boy.mp3")]
    public void Without_tags_a_file_name_with_no_artist_says_nothing(string fileName) =>
        ArtistEvidence.Judge(null, fileName, SaddleClub).Kind.Should().Be(ArtistEvidenceKind.Unknown);

    [Theory]
    [InlineData("Various Artists")]
    [InlineData("VA")]
    [InlineData("Unknown Artist")]
    [InlineData("  ")]
    public void A_tag_that_names_nobody_falls_back_to_the_file_name(string tag) =>
        ArtistEvidence.Judge(Tags(tag), "03 - The Saddle Club - Here I Am.flac", SaddleClub).Kind
            .Should().Be(ArtistEvidenceKind.Named);

    [Fact]
    public void A_song_without_artists_says_nothing() =>
        ArtistEvidence.Judge(Tags("Diskoflex"), "x.mp3", []).Kind.Should().Be(ArtistEvidenceKind.Unknown);

    private static FileTags Tags(string? artist, string? albumArtist = null) =>
        new(null, artist, albumArtist, null, null, null, null, null, null, null, null, null, null, null, null);
}
