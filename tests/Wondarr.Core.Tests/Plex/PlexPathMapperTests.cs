using Wondarr.Core.Domain;
using Wondarr.Core.Plex;
using FluentAssertions;
using Xunit;

namespace Wondarr.Core.Tests.Plex;

public class PlexPathMapperTests
{
    [Fact]
    public void Maps_a_Linux_path_onto_a_Linux_Plex_root()
    {
        var library = Library("/data/music", "/Music");

        PlexPathMapper.ToServerPath(library, "/data/music/Artist/Album/01 - Song.flac")
            .Should().Be("/Music/Artist/Album/01 - Song.flac");
    }

    [Fact]
    public void Maps_a_Linux_path_onto_a_Windows_Plex_root()
    {
        var library = Library("/data/music", @"D:\Music");

        PlexPathMapper.ToServerPath(library, "/data/music/Artist/Album/01 - Song.flac")
            .Should().Be(@"D:\Music\Artist\Album\01 - Song.flac");
    }

    [Fact]
    public void Joins_with_one_separator_however_the_roots_are_written()
    {
        var library = Library("/data/music/", "/Music/");

        PlexPathMapper.ToServerPath(library, "/data/music/Artist/Album")
            .Should().Be("/Music/Artist/Album");
    }

    [Fact]
    public void Maps_the_library_root_itself_to_the_Plex_root()
    {
        var library = Library("/data/music/", "/Music/");

        PlexPathMapper.ToServerPath(library, "/data/music").Should().Be("/Music");
        PlexPathMapper.ToServerPath(library, "/data/music/").Should().Be("/Music");
    }

    [Fact]
    public void Leaves_a_path_outside_the_library_root_alone()
    {
        var library = Library("/data/music", "/Music");

        PlexPathMapper.ToServerPath(library, "/data/other/Album").Should().Be("/data/other/Album");

        // "…/music2" merely starts with "…/music".
        PlexPathMapper.ToServerPath(library, "/data/music2/Album").Should().Be("/data/music2/Album");
    }

    [Fact]
    public void Leaves_the_path_alone_when_no_Plex_root_is_configured()
    {
        var library = Library("/data/music", plexLibraryPath: null);

        PlexPathMapper.ToServerPath(library, "/data/music/Artist/Album").Should().Be("/data/music/Artist/Album");

        var blank = Library("/data/music", "  ");
        PlexPathMapper.ToServerPath(blank, "/data/music/Artist/Album").Should().Be("/data/music/Artist/Album");
    }

    [Fact]
    public void Keeps_a_bare_Windows_drive_root_whole()
    {
        var library = Library("/data/music", @"D:\");

        PlexPathMapper.ToServerPath(library, "/data/music/Song.flac").Should().Be(@"D:\Song.flac");
    }

    private static Library Library(string rootPath, string? plexLibraryPath) =>
        new() { Name = "Music", RootPath = rootPath, PlexLibraryPath = plexLibraryPath };
}
