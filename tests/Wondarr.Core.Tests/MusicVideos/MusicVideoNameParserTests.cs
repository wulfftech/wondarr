using System.Text;
using FluentAssertions;
using Wondarr.Core.MusicVideos;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>Reading an artist and a title out of a video's path, and the <c>.nfo</c> beside it.</summary>
public sealed class MusicVideoNameParserTests
{
    private const string Template = "{Artist}/{Artist} - {Title}";

    [Fact]
    public void The_configured_template_is_turned_into_a_pattern_and_read_first()
    {
        var names = MusicVideoNameParser.Parse("Massive Attack/Massive Attack - Teardrop.mkv", Template);

        names[0].Should().Be(new MusicVideoParsedName("Massive Attack", "Teardrop", FromTemplate: true));
    }

    [Fact]
    public void A_folder_that_differs_from_the_file_names_artist_is_not_the_template()
    {
        var names = MusicVideoNameParser.Parse("Compilations/Massive Attack - Teardrop.mkv", Template);

        names.Should().NotContain(name => name.FromTemplate, "the same {Artist} has to read the same text twice");
        names.Should().Contain(new MusicVideoParsedName("Massive Attack", "Teardrop", FromTemplate: false));
    }

    [Fact]
    public void The_flat_form_is_read_when_the_file_is_not_in_an_artist_folder()
    {
        var names = MusicVideoNameParser.Parse("Massive Attack - Teardrop.mp4", Template);

        names.Should().ContainSingle().Which.Should().Be(new MusicVideoParsedName("Massive Attack", "Teardrop", FromTemplate: false));
    }

    [Theory]
    [InlineData("Artist - Title.mp4")]
    [InlineData("Artist - Title.avi")]
    [InlineData("Artist - Title.m4v")]
    [InlineData("Artist - Title.webm")]
    public void The_extension_is_not_part_of_the_title(string path)
    {
        MusicVideoNameParser.Parse(path, Template).Should().Contain(new MusicVideoParsedName("Artist", "Title", FromTemplate: false));
    }

    [Fact]
    public void An_en_dash_separates_the_artist_from_the_title_too()
    {
        MusicVideoNameParser.Parse("Massive Attack – Teardrop.mkv", Template)
            .Should().Contain(new MusicVideoParsedName("Massive Attack", "Teardrop", FromTemplate: false));
    }

    [Fact]
    public void A_decoration_stays_on_the_title_for_the_matcher_to_strip()
    {
        var name = MusicVideoNameParser.Parse("Massive Attack - Teardrop (Official Video).mkv", Template).Single();

        name.Title.Should().Be("Teardrop (Official Video)");
        MusicVideoTitleMatcher.Strip(name.Title).Title.Should().Be("Teardrop");
    }

    [Fact]
    public void A_title_with_a_dash_in_it_is_offered_both_ways()
    {
        var names = MusicVideoNameParser.Parse("A - B - C.mkv", Template);

        names.Should().Contain(new MusicVideoParsedName("A", "B - C", FromTemplate: false));
        names.Should().Contain(new MusicVideoParsedName("A - B", "C", FromTemplate: false));
    }

    [Fact]
    public void A_template_with_the_album_and_year_is_read_from_the_end_of_the_path()
    {
        var names = MusicVideoNameParser.Parse("Rock/Massive Attack/Mezzanine (1998)/Teardrop.mkv", "{Artist}/{Album} ({Year})/{Title}");

        names[0].Should().Be(new MusicVideoParsedName("Massive Attack", "Teardrop", FromTemplate: true));
    }

    [Fact]
    public void A_name_with_no_artist_in_it_is_read_as_nothing()
    {
        MusicVideoNameParser.Parse("Teardrop.mkv", Template).Should().BeEmpty();
    }

    [Fact]
    public void A_nfo_gives_the_title_and_every_artist()
    {
        var nfo = MusicVideoNfo.Parse(Encoding.UTF8.GetBytes(MusicVideoScanTestKit.Nfo("Teardrop", "Massive Attack", "Elizabeth Fraser")));

        nfo.Should().Be(new MusicVideoNfoData("Teardrop", "Massive Attack & Elizabeth Fraser"));
    }

    [Fact]
    public void Anything_that_is_not_a_musicvideo_document_is_ignored()
    {
        MusicVideoNfo.Parse(Encoding.UTF8.GetBytes("<movie><title>Teardrop</title></movie>")).Should().BeNull();
        MusicVideoNfo.Parse(Encoding.UTF8.GetBytes("not xml at all")).Should().BeNull();
    }

    [Fact]
    public void A_nfo_with_a_dtd_is_refused_rather_than_expanded()
    {
        const string bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE musicvideo [<!ENTITY x "boom"><!ENTITY y "&x;&x;&x;&x;">]>
            <musicvideo><title>&y;</title><artist>A</artist></musicvideo>
            """;

        MusicVideoNfo.Parse(Encoding.UTF8.GetBytes(bomb)).Should().BeNull();
    }

    [Fact]
    public void A_nfo_over_the_size_cap_is_not_read()
    {
        var huge = new byte[MusicVideoNfo.MaxBytes + 1];
        Array.Fill(huge, (byte)' ');

        MusicVideoNfo.Parse(huge).Should().BeNull();
    }
}
