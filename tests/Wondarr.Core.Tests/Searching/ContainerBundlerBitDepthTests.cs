using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Metadata;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Searching;

/// <summary>
/// The bundle looks for the other songs with the size window the release's name set (M-35): a release
/// that stated 16-bit does not bundle a 24-bit-sized file, a bare "[FLAC]" one does.
/// </summary>
public sealed class ContainerBundlerBitDepthTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_file_the_size_of_24_bit_rides_along_only_when_the_release_did_not_state_its_depth(
        bool sixteenBitStated,
        bool bundled)
    {
        await using var host = await SearchTestHost.CreateAsync();
        var alpha = await host.SeedSongAsync("Alpha");
        long artistId;

        await using (var seed = host.Database.CreateContext(host.Time))
        {
            artistId = (await seed.Songs.SingleAsync(song => song.Id == alpha, Token)).PrimaryArtistId;
        }

        var gamma = await host.SeedSongAsync("Gamma", artistId: artistId);

        // 200 s: the grabbed file is 16-bit sized (about 1,200 kbps), the other 24-bit/96 kHz sized (about 3,800 kbps).
        ContainerFile[] files =
        [
            new(0, "Album/01 - Alpha.flac", 30_000_000),
            new(1, "Album/02 - Gamma.flac", 200 * 3_800 * 1000L / 8),
        ];

        const string hash = "0123456789abcdef0123456789abcdef01234567";
        var candidate = SearchTestHost.Candidate("Album/01 - Alpha.flac") with
        {
            Container = CandidateContainer.AlbumContainer,
            BlocklistKey = BlocklistKeys.Torrent(hash, "Album/01 - Alpha.flac"),
            Release = new ContainerRelease
            {
                IndexerId = 1,
                IndexerName = "Indexer",
                Title = "Artist - Album [FLAC]",
                ReleaseId = "guid-depth",
                InfoHash = hash,
                FileIndex = 0,
                Files = files,
                Song = new ContainerMatchRequest("Alpha", VersionFlags.None, null, 200_000, 36, sixteenBitStated),
            },
        };

        await using var context = host.Database.CreateContext(host.Time);

        var found = await ContainerBundler.FindAsync(context, alpha, candidate, Token);

        found.Any(entry => entry.Song.Id == gamma).Should().Be(bundled);
    }
}
