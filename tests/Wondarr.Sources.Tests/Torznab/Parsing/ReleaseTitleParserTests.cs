using FluentAssertions;
using Wondarr.Sources.Torznab.Parsing;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Parsing;

/// <summary>
/// Golden tests over release titles: scene names, "Artist - Album (Year)" forms, discographies
/// and the version tags Wondarr reads out of them.
/// </summary>
public class ReleaseTitleParserTests
{
    [Theory]
    [InlineData("Artist-Album-WEB-2019-GROUP", "Artist", "Album", 2019, "WEB")]
    [InlineData("Dani_Sbert-Togheter-WEB-2017-FURY", "Dani Sbert", "Togheter", 2017, "WEB")]
    [InlineData("Imagine Dragons-Smoke And Mirrors-Deluxe Edition-2CD-FLAC-2015-JLM", "Imagine Dragons", "Smoke And Mirrors", 2015, "CD,Deluxe")]
    [InlineData("Massive Attack-Mezzanine-CD-FLAC-1998-FATHEAD", "Massive Attack", "Mezzanine", 1998, "CD")]
    [InlineData("Daft Punk - Discovery (2001) [FLAC]", "Daft Punk", "Discovery", 2001, "")]
    [InlineData("Artist - Album (2010) [FLAC 24bit-96kHz]", "Artist", "Album", 2010, "")]
    [InlineData("Radiohead - Kid A (2000) [MP3 320]", "Radiohead", "Kid A", 2000, "")]
    [InlineData("Artist - Album {Deluxe Edition}", "Artist", "Album", null, "Deluxe")]
    [InlineData("Various Artists - Now Thats What I Call Music (2021) FLAC", "Various Artists", "Now Thats What I Call Music", 2021, "")]
    [InlineData("Artist - Album - 2010 [Something]", "Artist", "Album", 2010, "")]
    [InlineData("Artist-Album (2010)", "Artist", "Album", 2010, "")]
    [InlineData("Artist-Album [Edition]", "Artist", "Album", null, "")]
    [InlineData("Artist-Album-something-2010", "Artist", "Album", 2010, "")]
    [InlineData("Artist - Album 2010", "Artist", "Album", 2010, "")]
    [InlineData("Artist - 2010 - Album", "Artist", "Album", 2010, "")]
    [InlineData("Artist - Album (2010) [Remastered]", "Artist", "Album", 2010, "Remastered")]
    [InlineData("Artist - Album (2010) [Vinyl Rip 24bit FLAC]", "Artist", "Album", 2010, "Vinyl")]
    [InlineData("Artist - Album (2010) [Limited Edition]", "Artist", "Album", 2010, "Limited")]
    [InlineData("Artist - Album (2010) [Bonus Tracks]", "Artist", "Album", 2010, "Bonus")]
    [InlineData("Artist - Album (2010) [Explicit]", "Artist", "Album", 2010, "Explicit")]
    [InlineData("Artist - Album (2010) [Live]", "Artist", "Album", 2010, "Live")]
    [InlineData("Artist - Album (2019) [WEB]", "Artist", "Album", 2019, "WEB")]
    [InlineData("Artist - Discography", "Artist", "Discography", null, "")]
    [InlineData("Artist - Discography 1990-2020", "Artist", "Discography", null, "")]
    [InlineData("(Electronic) [Lossless] Artist - Discography 1990-2020", "Artist", "Discography", null, "")]
    [InlineData("Artist - Album (1899)", "Artist", "Album", null, "")]
    [InlineData("Artist - Album [FLAC].torrent", "Artist", "Album", null, "")]
    public void Parses_artist_album_year_and_tags(
        string title, string artist, string album, int? year, string tags)
    {
        var parsed = ReleaseTitleParser.Parse(title);

        parsed.Should().NotBeNull();
        parsed!.Artist.Should().Be(artist);
        parsed.Album.Should().Be(album);
        parsed.Year.Should().Be(year);
        parsed.Tags.Should().BeEquivalentTo(tags.Length == 0 ? [] : tags.Split(','));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("4f6e2a9b8c1d3e5f7a2b")]
    [InlineData("Best of 2021")]
    [InlineData("password protected yEnc upload")]
    public void Returns_null_when_no_artist_and_album_can_be_found(string title)
    {
        ReleaseTitleParser.Parse(title).Should().BeNull();
    }
}
