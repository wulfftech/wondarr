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
    [InlineData("Nine Inch Nails - The Slip (2008) FLAC 16-44", true, false)]
    [InlineData("Nine Inch Nails - The Slip (2008) [FLAC 24-96]", false, true)]
    [InlineData("Nine Inch Nails - The Slip (2008) [FLAC 24bit]", false, true)]
    // 24-bit spellings the quality parser reads as plain FLAC still get the wide window.
    [InlineData("Nine Inch Nails - The Slip (2008) [FLAC 24-88.2]", false, true)]
    [InlineData("Nine Inch Nails - The Slip (2008) [FLAC 24 96]", false, true)]
    [InlineData("Nine Inch Nails - The Slip (2008) [FLAC 24-176.4]", false, true)]
    public void A_flac_release_sets_its_size_window_by_what_its_name_claims(string title, bool sixteenBit, bool accepted)
    {
        var listing = Listing(title, [new ContainerFile(0, "nine-inch-nails-the-slip/03_letting_you.flac", HiResBytes)]);

        var candidate = ContainerCandidates.Build(DownloadProtocol.Torrent, listing, Wanted, "nin the slip", Now);

        if (accepted)
        {
            candidate.Should().NotBeNull("a file of that size fits the window the name leaves open");
            candidate!.Release!.Song!.SixteenBitStated.Should().Be(sixteenBit);
        }
        else
        {
            candidate.Should().BeNull("the release said 16-bit or CD, and this file is not");
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

    [Theory]
    [InlineData("Norah.Jones-Come.Away.with.Me-CD-FLAC-2002-FADA", "Norah Jones", "FADA")]
    [InlineData("Norah.Jones-Come.Away.With.Me-JAPANESE.RETAIL-2002-pH", "Norah Jones", "pH")]
    [InlineData("Imagine Dragons-Smoke And Mirrors-Deluxe Edition-2CD-FLAC-2015-JLM", "Imagine Dragons", "JLM")]
    [InlineData("Nine Inch Nails - The Slip (2008) [FLAC]", "Nine Inch Nails", null)]
    [InlineData("Daft Punk - Discovery (2001) [FLAC]", "Daft Punk", null)]
    public void The_matcher_is_told_the_release_s_artist_and_scene_group(string title, string artist, string? group)
    {
        var listing = Listing(title, null);

        var candidate = ContainerCandidates.Build(DownloadProtocol.Torrent, listing, Wanted, "q", Now);

        var song = candidate!.Release!.Song!;
        song.ReleaseArtist.Should().Be(artist);
        song.ReleaseGroup.Should().Be(group);
    }

    [Fact]
    public void A_file_list_unknown_candidate_keeps_the_song_with_the_release_s_artist_and_group_for_after_the_grab()
    {
        var listing = Listing("Velvet.Harbour-Paper.Lanterns-CD-FLAC-2002-FADA", null);

        var candidate = ContainerCandidates.Build(DownloadProtocol.Usenet, listing, Wanted, "q", Now)!;

        candidate.Extension.Should().BeNull();
        candidate.Release!.FileIndex.Should().BeNull();
        candidate.Availability.FileListKnown.Should().BeFalse();

        // The handle is saved as JSON and read back when the unpacked post is matched.
        var handle = new ContainerGrab(1, 1, "guid-1", null, "job", null, "staging", candidate.Release.Song, true);
        var song = ContainerGrab.Deserialize(handle.Serialize()).Song!;

        song.ReleaseArtist.Should().Be("Velvet Harbour");
        song.ReleaseGroup.Should().Be("FADA");
    }

    [Fact]
    public void A_handle_saved_before_the_artist_and_group_existed_still_reads()
    {
        const string json = """{"clientId":1,"indexerId":1,"releaseId":"guid-1","jobId":"job","stagingDir":"staging","song":{"title":"Letting You","versionFlags":0,"trackNo":3,"durationMs":253000,"qualityId":1,"sixteenBitStated":false}}""";

        var song = ContainerGrab.Deserialize(json).Song!;

        song.Title.Should().Be("Letting You");
        song.ReleaseArtist.Should().BeNull();
        song.ReleaseGroup.Should().BeNull();
    }

    private static ContainerListing Listing(string title, IReadOnlyList<ContainerFile>? files) =>
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
