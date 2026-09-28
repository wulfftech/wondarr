using Wondarr.Core.Organizer;
using FluentAssertions;
using Xunit;

namespace Wondarr.Core.Tests.Organizer;

public class RemotePathMapperTests
{
    // Where the download client's /downloads really is on this host. Per OS, so the expected path
    // is built with the local separator the mapper promises.
    private static readonly string LocalRoot =
        OperatingSystem.IsWindows() ? @"D:\dl" : "/data/downloads";

    private static RemotePathMapper Mapper(params RemotePathMapping[] mappings) =>
        new RemotePathMapper(new TestOptionsMonitor<ImportOptions>(new ImportOptions
        {
            RemotePathMappings = [.. mappings],
        }));

    private static RemotePathMapping Mapping(string host, string remote, string local) =>
        new() { Host = host, RemotePath = remote, LocalPath = local };

    [Fact]
    public void Maps_a_remote_path_onto_the_local_root()
    {
        var mapper = Mapper(Mapping("qbit", "/downloads", LocalRoot));

        mapper.MapToLocal("qbit", "/downloads/music/x.mp3")
            .Should().Be(Path.Combine(LocalRoot, "music", "x.mp3"));
    }

    [Fact]
    public void Matches_the_host_without_regard_to_case()
    {
        var mapper = Mapper(Mapping("QBit", "/downloads", LocalRoot));

        mapper.MapToLocal("qbit", "/downloads/x.mp3")
            .Should().Be(Path.Combine(LocalRoot, "x.mp3"));
    }

    [Fact]
    public void Leaves_a_path_from_another_host_alone()
    {
        var mapper = Mapper(Mapping("qbit", "/downloads", LocalRoot));

        mapper.MapToLocal("sabnzbd", "/downloads/music/x.mp3")
            .Should().Be("/downloads/music/x.mp3");
    }

    [Fact]
    public void Does_not_match_a_prefix_that_stops_in_the_middle_of_a_segment()
    {
        var mapper = Mapper(Mapping("qbit", "/down", LocalRoot));

        mapper.MapToLocal("qbit", "/downloads/music/x.mp3")
            .Should().Be("/downloads/music/x.mp3");
    }

    [Fact]
    public void Accepts_a_mapping_that_ends_with_a_separator()
    {
        var mapper = Mapper(Mapping("qbit", "/downloads/", LocalRoot));

        mapper.MapToLocal("qbit", "/downloads/music/x.mp3")
            .Should().Be(Path.Combine(LocalRoot, "music", "x.mp3"));
    }

    [Fact]
    public void Reads_backslashes_as_separators_on_both_sides()
    {
        var mapper = Mapper(Mapping("qbit", @"\downloads", LocalRoot));

        mapper.MapToLocal("qbit", @"\downloads\music\x.mp3")
            .Should().Be(Path.Combine(LocalRoot, "music", "x.mp3"));
    }

    [Fact]
    public void Maps_onto_the_root_itself_when_the_path_is_the_prefix()
    {
        var mapper = Mapper(Mapping("qbit", "/downloads", LocalRoot));

        mapper.MapToLocal("qbit", "/downloads").Should().Be(LocalRoot);
    }

    [Fact]
    public void Uses_the_first_mapping_that_matches()
    {
        var mapper = Mapper(
            Mapping("qbit", "/downloads/music", Path.Combine(LocalRoot, "music")),
            Mapping("qbit", "/downloads", LocalRoot));

        mapper.MapToLocal("qbit", "/downloads/music/x.mp3")
            .Should().Be(Path.Combine(LocalRoot, "music", "x.mp3"));
    }

    [Fact]
    public void Leaves_the_path_alone_when_no_mapping_covers_it()
    {
        var mapper = Mapper(Mapping("qbit", "/downloads", LocalRoot));

        mapper.MapToLocal("qbit", "/elsewhere/x.mp3").Should().Be("/elsewhere/x.mp3");
        Mapper().MapToLocal("qbit", "/downloads/x.mp3").Should().Be("/downloads/x.mp3");
    }
}