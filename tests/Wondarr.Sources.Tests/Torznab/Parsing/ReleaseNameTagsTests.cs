using FluentAssertions;
using Wondarr.Core.Metadata;
using Wondarr.Sources.Torznab.Parsing;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Parsing;

/// <summary>The scene group and the version words a release name carries (M-42a).</summary>
public sealed class ReleaseNameTagsTests
{
    [Theory]
    [InlineData("Norah.Jones-Come.Away.with.Me-CD-FLAC-2002-FADA", "FADA")]
    [InlineData("Norah.Jones-Come.Away.With.Me-JAPANESE.RETAIL-2002-pH", "pH")]
    [InlineData("Imagine Dragons-Smoke And Mirrors-Deluxe Edition-2CD-FLAC-2015-JLM", "JLM")]
    [InlineData("Norah.Jones-Come.Away.with.Me-CD-FLAC-2002-FADA.nzb", "FADA")]
    [InlineData("Norah.Jones-Come.Away.with.Me-CD-FLAC-2002-FADA.torrent", "FADA")]
    [InlineData("Artist-Album-GRP.rar", "GRP")]
    [InlineData("Artist-Album-iNT", "iNT")]
    public void Reads_the_scene_group_a_release_name_ends_with(string title, string group)
    {
        ReleaseNameTags.GroupOf(title).Should().Be(group);
    }

    [Theory]
    [InlineData("Prince-Purple_Rain-1984-FLAC")]
    [InlineData("Pink_Floyd-The_Wall-2CD-FLAC-1979")]
    [InlineData("Daft.Punk.Discography.1997-2013.FLAC")]
    [InlineData("Adele-Hello")]
    [InlineData("Artist-Album-Live")]
    [InlineData("Artist-Album-Remix")]
    [InlineData("Artist-Album-Demo")]
    [InlineData("Foo.Fighters-Everlong-SINGLE-FLAC-1997-LIVE")]
    [InlineData("Artist-Album-1997")]
    [InlineData("Artist-Album-V0")]
    [InlineData("Artist-Album-320")]
    [InlineData("Artist-Album-24BIT")]
    [InlineData("Artist-Album-16BIT")]
    [InlineData("Artist-Album-WEB")]
    [InlineData("Artist-Album-2CD")]
    [InlineData("Artist-Album-VINYL")]
    [InlineData("Artist-Album-LOSSLESS")]
    [InlineData("Artist-Album-MP3")]
    [InlineData("Artist-Album-GR.P")]
    [InlineData("Daft Punk - Discovery (2001) [FLAC]")]
    [InlineData("Nine Inch Nails - The Slip (2008) [FLAC]")]
    [InlineData("nine-inch-nails-the-slip_202607 [FLAC]")]
    [InlineData("")]
    public void Finds_no_group_where_the_last_field_is_not_one(string title)
    {
        ReleaseNameTags.GroupOf(title).Should().BeNull();
    }

    [Theory]
    [InlineData("Foo.Fighters-Live.at.Wembley-2CD-FLAC-2008-GRP", VersionFlags.Live)]
    [InlineData("Foo.Fighters-Everlong-SINGLE-FLAC-1997-LIVE", VersionFlags.Live)]
    [InlineData("Artist-Album-Live", VersionFlags.Live)]
    [InlineData("Daft Punk - Alive 2007 (Live) [FLAC]", VersionFlags.Live)]
    [InlineData("Artist-Unplugged-CD-FLAC-1999-GRP", VersionFlags.Live | VersionFlags.Acoustic)]
    [InlineData("Artist-Live.In.Concert-CD-FLAC-1999-GRP", VersionFlags.Live)]
    [InlineData("Artist-Bootleg.Sessions-CD-FLAC-2001-GRP", VersionFlags.Live)]
    [InlineData("Artist-Acoustic.Sessions-WEB-2010-GRP", VersionFlags.Acoustic)]
    [InlineData("Artist-The.Remixes-WEB-2010-GRP", VersionFlags.Remix)]
    [InlineData("Artist-Instrumentals-WEB-2010-GRP", VersionFlags.Instrumental)]
    [InlineData("Artist-Demos-WEB-2010-GRP", VersionFlags.Demo)]
    [InlineData("Artist-Karaoke-WEB-2010-GRP", VersionFlags.Karaoke)]
    public void Reads_the_version_a_release_name_gives_every_track(string title, VersionFlags expected)
    {
        ReleaseNameTags.VersionFlagsOf(title).Should().Be(expected);
    }

    [Theory]
    [InlineData("Paul McCartney - Live and Let Die [FLAC]", "Live and Let Die", null, VersionFlags.None, VersionFlags.None)]
    [InlineData("Oasis - Live Forever [FLAC]", "Live Forever", "Definitely Maybe", VersionFlags.None, VersionFlags.None)]
    [InlineData("Hole-Live_Through_This-CD-FLAC-1994-GRP", "Doll Parts", "Live Through This", VersionFlags.None, VersionFlags.None)]
    [InlineData("Taylor Swift - Long Live [FLAC]", "Long Live", "Long Live", VersionFlags.None, VersionFlags.None)]
    [InlineData("Christina Aguilera - Stripped [FLAC]", "Beautiful", "Stripped", VersionFlags.None, VersionFlags.None)]
    [InlineData("Oasis - Live at Knebworth [FLAC]", "Live Forever", "Definitely Maybe", VersionFlags.None, VersionFlags.Live)]
    [InlineData("Foo Fighters - Everlong (Live) [FLAC]", "Everlong", "The Colour and the Shape", VersionFlags.None, VersionFlags.Live)]
    [InlineData("Foo.Fighters-Everlong-SINGLE-FLAC-1997-LIVE", "Everlong", "The Colour and the Shape", VersionFlags.None, VersionFlags.Live)]
    [InlineData("Foo.Fighters-Live.at.Wembley-2CD-FLAC-2008-GRP", "Everlong", "The Colour and the Shape", VersionFlags.None, VersionFlags.Live)]
    [InlineData("Foo.Fighters-Live.at.Wembley-2CD-FLAC-2008-GRP", "Everlong (Live)", "Live at Wembley", VersionFlags.Live, VersionFlags.Live)]
    [InlineData("Foo.Fighters-Live.at.Wembley-2CD-FLAC-2008-GRP", "Everlong", "Live at Wembley", VersionFlags.None, VersionFlags.None)]
    public void A_release_named_after_the_song_or_its_album_keeps_its_words_out_of_the_version(
        string title,
        string song,
        string? album,
        VersionFlags songFlags,
        VersionFlags expected)
    {
        var version = ReleaseNameTags.VersionOf(title, song, album, songFlags);

        version.Release.Should().Be(expected);
        version.Path.Should().HaveFlag(expected);
    }

    [Theory]
    [InlineData("Norah.Jones-Come.Away.with.Me-CD-FLAC-2002-FADA")]
    [InlineData("Norah.Jones-Come.Away.with.Me-CD-FLAC-2002-FADA.nzb")]
    [InlineData("Daft Punk - Discovery (2001) [FLAC]")]
    [InlineData("Live-Throwing.Copper-CD-FLAC-1994-GRP")]
    [InlineData("Artist-Extended.Play-WEB-2010-GRP")]
    [InlineData("Artist - Album (Extended Edition) [FLAC]")]
    [InlineData("Artist - Album (Remastered 2011) [FLAC]")]
    public void Finds_no_version_where_the_name_gives_none(string title)
    {
        ReleaseNameTags.VersionFlagsOf(title).Should().Be(VersionFlags.None);
    }
}
