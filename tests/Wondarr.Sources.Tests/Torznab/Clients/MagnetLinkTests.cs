using Wondarr.Sources.Torznab.Clients;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Clients;

/// <summary>Tests of the magnet infohash reader.</summary>
public sealed class MagnetLinkTests
{
    // The base32 form of 0123456789abcdef0123456789abcdef01234567, a known pair.
    private const string Base32Hash = "AERUKZ4JVPG66AJDIVTYTK6N54ASGRLH";

    [Fact]
    public void A_hex_infohash_is_read_in_lower_case()
    {
        var magnet = "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=Some+Release";

        MagnetLink.TryGetInfoHash(magnet, out var hex).Should().BeTrue();
        hex.Should().Be("0123456789abcdef0123456789abcdef01234567");
    }

    [Fact]
    public void A_base32_infohash_is_converted_to_hex()
    {
        var magnet = "magnet:?xt=urn:btih:" + Base32Hash + "&dn=Some+Release";

        MagnetLink.TryGetInfoHash(magnet, out var hex).Should().BeTrue();
        hex.Should().Be("0123456789abcdef0123456789abcdef01234567");
    }

    [Fact]
    public void A_bitTorrent_v2_hash_is_refused()
    {
        var magnet = "magnet:?xt=urn:btmh:1220ea8d3b40a7a2c0c1f3d1e0a9b8c7d6e5f4a3b2c1d" +
            "0e9f8a7b6c5d4e3f2a1b0c9d8e7f6a5b4c3d2e1f0a9&dn=Some+Release";

        MagnetLink.TryGetInfoHash(magnet, out var hex).Should().BeFalse();
        hex.Should().BeEmpty();
    }

    [Fact]
    public void A_magnet_without_an_xt_parameter_is_refused()
    {
        var magnet = "magnet:?dn=Some+Release";

        MagnetLink.TryGetInfoHash(magnet, out var hex).Should().BeFalse();
        hex.Should().BeEmpty();
    }

    [Fact]
    public void A_hash_that_is_neither_hex_nor_base32_is_refused()
    {
        var magnet = "magnet:?xt=urn:btih:tooshort";

        MagnetLink.TryGetInfoHash(magnet, out var hex).Should().BeFalse();
        hex.Should().BeEmpty();
    }
}
