using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.Organizer;

/// <summary>R-046: every preset with an album folder carries the release year in it (LIBRARY_OUTPUT §7.1).</summary>
public class AlbumFolderYearTests
{
    private const string OldPlexampTemplate = "{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}";

    [Fact]
    public void Every_preset_with_an_album_folder_renders_the_year()
    {
        var checkedPresets = 0;

        foreach (var (layout, template) in NamingTemplate.PresetTemplates)
        {
            if (!template.Contains("{Album Title}", StringComparison.Ordinal))
            {
                continue;
            }

            checkedPresets++;
            NamingTemplate.Render(template, NamingTemplateTests.SampleValues())
                .Should().Contain("Random Access Memories (2013)/", $"preset {layout}");
        }

        checkedPresets.Should().Be(2, "artist_album and plexamp have an album folder");
    }

    [Fact]
    public void An_unknown_year_leaves_no_empty_parentheses()
    {
        var values = NamingTemplateTests.SampleValues() with { ReleaseYear = null };

        foreach (var (layout, template) in NamingTemplate.PresetTemplates)
        {
            var path = NamingTemplate.Render(template, values);

            path.Should().NotContain("()", $"preset {layout}");
            path.Should().NotContain(" /", $"preset {layout}");
        }

        NamingTemplate.Render(NamingTemplate.PresetTemplates[LibraryLayout.Plexamp], values)
            .Should().Be("Daft Punk/Random Access Memories/08 - Get Lucky");
    }

    [Fact]
    public void A_year_of_zero_is_an_unknown_year()
    {
        var values = NamingTemplateTests.SampleValues() with { ReleaseYear = 0 };

        NamingTemplate.Render(NamingTemplate.PresetTemplates[LibraryLayout.ArtistAlbum], values)
            .Should().Be("Daft Punk/Random Access Memories/08 - Get Lucky");
    }

    [Fact]
    public void The_singles_pseudo_album_never_carries_a_year_in_any_preset()
    {
        var artist = new Artist { Name = "The Dixie Cups", SortName = "Dixie Cups, The" };
        var song = new Song { Title = "Chapel of Love", ArtistCredit = "The Dixie Cups" };
        var album = new AlbumContext
        {
            Kind = AlbumContextKind.PseudoSingles,
            AlbumTitle = "Singles",
            AlbumArtist = "The Dixie Cups",
            AlbumKey = "singles",
            TrackNo = 1,
            DiscNo = 1,
            Date = "1964-05-01",
        };
        var media = new MediaInfo("mp3", "mp3", 320, 44_100, null, 2, 150_000, false, 6_000_000);
        var quality = new Quality { Name = "MP3-320", Codec = "mp3" };

        var values = NamingValuesBuilder.Build(song, album, artist, media, quality, "soulseek");

        values.ReleaseYear.Should().BeNull();

        foreach (var (layout, template) in NamingTemplate.PresetTemplates)
        {
            var path = NamingTemplate.Render(template, values);

            if (template.Contains("{Album Title}", StringComparison.Ordinal))
            {
                path.Split('/').Should().Contain("Singles", $"preset {layout}");
            }

            path.Should().NotContain("1964", $"preset {layout}");
        }
    }

    [Fact]
    public void Two_self_titled_albums_with_different_years_get_different_folders()
    {
        var template = NamingTemplate.PresetTemplates[LibraryLayout.Plexamp];
        var blue = NamingTemplateTests.SampleValues() with
        {
            ArtistName = "Weezer",
            AlbumArtistName = "Weezer",
            AlbumTitle = "Weezer",
            ReleaseYear = 1994,
        };
        var green = blue with { ReleaseYear = 2001 };

        NamingTemplate.Render(template, blue).Split('/')[1].Should().Be("Weezer (1994)");
        NamingTemplate.Render(template, green).Split('/')[1].Should().Be("Weezer (2001)");
    }

    [Fact]
    public void A_stored_copy_of_the_old_plexamp_preset_follows_the_new_one()
    {
        NamingTemplate.Effective(LibraryLayout.Plexamp, OldPlexampTemplate)
            .Should().Be(NamingTemplate.PresetTemplates[LibraryLayout.Plexamp]);
        NamingTemplate.Effective(LibraryLayout.Plexamp, string.Empty)
            .Should().Be(NamingTemplate.PresetTemplates[LibraryLayout.Plexamp]);
    }

    [Fact]
    public void A_custom_template_is_untouched()
    {
        const string custom = "{Album Artist Name}/{Album Title} [{Album Type}]/{track:00} - {Track Title}";

        NamingTemplate.Effective(LibraryLayout.Plexamp, custom).Should().Be(custom);

        // The old preset text on another layout is the user's own choice, not a preset copy.
        NamingTemplate.Effective(LibraryLayout.ArtistAlbum, OldPlexampTemplate).Should().Be(OldPlexampTemplate);
    }
}
