using FluentAssertions;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Sources.Torznab.Searching;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Searching;

/// <summary>
/// The size window a release's name sets (M-35): a bare "[FLAC]" may hold 24-bit files, a release that
/// states 16-bit, CD or a depth keeps the window of what it states.
/// </summary>
public sealed class ContainerCandidatesTests
{
    /// <summary>253 s at about 3,800 kbps: 24-bit/96 kHz FLAC, over the 16-bit window of 2,700 kbps.</summary>
    private const long HiResBytes = 253 * 3_800 * 1000L / 8;

    private static readonly DateTimeOffset Now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);

    private static readonly ContainerMatchRequest Wanted =
        new("Letting You", VersionFlags.None, null, 253_000, 1);

    [Theory]
    [InlineData("nine-inch-nails-the-slip_202607 [FLAC]", false, true)]
    [InlineData("Nine Inch Nails - The Slip (2008) FLAC", false, true)]
    [InlineData("Nine Inch Nails - The Slip (2008) FLAC 16-bit", true, false)]
    [InlineData("Nine Inch Nails - The Slip (2008) FLAC CD", true, false)]
    [InlineData("Nine Inch Nails - The Slip (2008) [FLAC 24-96]", true, true)]
    [InlineData("Nine Inch Nails - The Slip (2008) [FLAC 24bit]", true, true)]
    public void A_flac_release_sets_its_size_window_by_what_its_name_states(string title, bool stated, bool accepted)
    {
        var listing = Listing(title, [new ContainerFile(0, "nine-inch-nails-the-slip/03_letting_you.flac", HiResBytes)]);

        var candidate = ContainerCandidates.Build(DownloadProtocol.Torrent, listing, Wanted, "nin the slip", Now);

        if (accepted)
        {
            candidate.Should().NotBeNull("a file of that size fits the window the name leaves open");
            candidate!.Release!.Song!.BitDepthStated.Should().Be(stated);
        }
        else
        {
            candidate.Should().BeNull("the release said 16-bit, and this file is not");
        }
    }

    [Fact]
    public void A_bare_flac_release_still_takes_a_16_bit_sized_file()
    {
        var listing = Listing(
            "Artist - Album [FLAC]",
            [new ContainerFile(0, "Artist - Album [FLAC]/03 - Letting You.flac", 253 * 900 * 1000L / 8)]);

        ContainerCandidates.Build(DownloadProtocol.Torrent, listing, Wanted, "q", Now).Should().NotBeNull();
    }

    private static ContainerListing Listing(string title, IReadOnlyList<ContainerFile> files) =>
        new(
            new IndexerRelease(
                title,
                "guid-1",
                "https://tracker.example/download.php",
                null,
                null,
                null,
                null,
                10,
                1,
                5,
                [3040],
                1.0,
                null,
                DownloadProtocol.Torrent,
                1,
                "Indexer",
                null),
            files,
            "0123456789abcdef0123456789abcdef01234567",
            null);
}
