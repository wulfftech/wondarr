using Wondarr.Sources.Torznab.Clients;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Clients;

/// <summary>Tests of the per-client remote path mapping (DECISIONS build session 8 #8).</summary>
public sealed class RemotePathMapperTests
{
    [Fact]
    public void The_longest_matching_prefix_wins()
    {
        var mappings = new[]
        {
            new RemotePathMapping("/downloads", "/mnt/media"),
            new RemotePathMapping("/downloads/wondarr", "/mnt/media/wondarr"),
        };

        RemotePathMapper.Map("/downloads/wondarr/Some Release", mappings)
            .Should().Be(Path.Combine("/mnt/media/wondarr", "Some Release"));
    }

    [Fact]
    public void A_windows_prefix_matches_without_case_and_a_unix_one_with_it()
    {
        var mappings = new[]
        {
            new RemotePathMapping(@"C:\Downloads", "/mnt/windows"),
            new RemotePathMapping("/Data", "/mnt/data"),
        };

        RemotePathMapper.Map(@"c:\downloads\Album", mappings).Should().Be(Path.Combine("/mnt/windows", "Album"));
        RemotePathMapper.Map("/data/Album", mappings).Should().Be("/data/Album");
    }

    [Fact]
    public void A_prefix_that_stops_mid_segment_does_not_match()
    {
        var mappings = new[] { new RemotePathMapping("/data", "/mnt/media") };

        RemotePathMapper.Map("/database/wondarr", mappings).Should().Be("/database/wondarr");
    }

    [Fact]
    public void The_remote_side_is_matched_separator_blind()
    {
        var mappings = new[] { new RemotePathMapping("\\\\server\\share", "/mnt/media") };

        RemotePathMapper.Map("\\\\server\\share\\wondarr\\Some Release", mappings)
            .Should().Be(Path.Combine("/mnt/media", "wondarr", "Some Release"));
    }

    [Fact]
    public void A_windows_client_path_maps_to_a_posix_local_path()
    {
        var mappings = new[] { new RemotePathMapping("C:\\Users\\qbit\\Downloads", "/mnt/media") };

        RemotePathMapper.Map("C:\\Users\\qbit\\Downloads\\wondarr\\Some Release", mappings)
            .Should().Be(Path.Combine("/mnt/media", "wondarr", "Some Release"));
    }

    [Fact]
    public void A_posix_client_path_maps_to_a_windows_local_path()
    {
        var mappings = new[] { new RemotePathMapping("/downloads", "D:\\media") };

        RemotePathMapper.Map("/downloads/wondarr/Some Release", mappings)
            .Should().Be(Path.Combine("D:\\media", "wondarr", "Some Release"));
    }

    [Fact]
    public void The_exact_remote_path_maps_to_the_local_path()
    {
        var mappings = new[] { new RemotePathMapping("/downloads/wondarr/", "/mnt/media/wondarr") };

        RemotePathMapper.Map("/downloads/wondarr", mappings).Should().Be("/mnt/media/wondarr");
    }

    [Fact]
    public void A_path_no_mapping_covers_comes_back_unchanged()
    {
        var mappings = new[] { new RemotePathMapping("/downloads", "/mnt/media") };

        RemotePathMapper.Map("/srv/torrents/Some Release", mappings).Should().Be("/srv/torrents/Some Release");
        RemotePathMapper.Map("/srv/torrents/Some Release", []).Should().Be("/srv/torrents/Some Release");
    }
}
