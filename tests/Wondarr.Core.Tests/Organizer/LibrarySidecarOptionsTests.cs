using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.Organizer;

/// <summary>
/// The sidecar options are user-editable JSON behind a settings page that does not exist yet, so
/// every way of getting them wrong has to land on the defaults rather than on an import failure.
/// </summary>
public sealed class LibrarySidecarOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("not json at all")]
    [InlineData("{\"coverJpg\": tru")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    public void Falls_back_to_the_defaults(string? json)
    {
        var options = LibrarySidecarOptions.Parse(json);

        options.CoverJpg.Should().BeTrue();
        options.CoverMaxEdge.Should().Be(1400);
        options.Lyrics.Should().BeTrue();
    }

    [Fact]
    public void Reads_the_camel_case_keys_and_ignores_the_ones_it_does_not_know()
    {
        var options = LibrarySidecarOptions.Parse(
            "{\"coverJpg\":false,\"coverMaxEdge\":900,\"lyrics\":false,\"artistJpg\":true}");

        options.CoverJpg.Should().BeFalse();
        options.CoverMaxEdge.Should().Be(900);
        options.Lyrics.Should().BeFalse();
    }

    [Fact]
    public void Keeps_the_default_of_every_key_the_json_leaves_out()
    {
        var options = LibrarySidecarOptions.Parse("{\"coverMaxEdge\":900}");

        options.CoverMaxEdge.Should().Be(900);
        options.CoverJpg.Should().BeTrue();
        options.Lyrics.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(299, 300)]
    [InlineData(300, 300)]
    [InlineData(4000, 4000)]
    [InlineData(4001, 4000)]
    [InlineData(-100, 300)]
    public void Clamps_the_cover_edge(int written, int expected)
    {
        LibrarySidecarOptions.Parse($"{{\"coverMaxEdge\":{written}}}").CoverMaxEdge.Should().Be(expected);
    }

    [Fact]
    public void Round_trips_through_its_own_json()
    {
        var options = new LibrarySidecarOptions { CoverJpg = false, CoverMaxEdge = 800, Lyrics = false };

        LibrarySidecarOptions.Parse(options.ToJson()).Should().Be(options);
        options.ToJson().Should().Be("{\"coverJpg\":false,\"coverMaxEdge\":800,\"lyrics\":false}");
    }

    [Theory]
    [InlineData(LibraryLayout.Plexamp, true)]
    [InlineData(LibraryLayout.ArtistAlbum, true)]
    [InlineData(LibraryLayout.Flat, false)]
    [InlineData(LibraryLayout.Artist, false)]
    public void Only_an_album_layout_has_a_folder_for_cover_jpg(LibraryLayout layout, bool expected)
    {
        LibrarySidecarOptions.HasAlbumFolder(layout).Should().Be(expected);
    }
}