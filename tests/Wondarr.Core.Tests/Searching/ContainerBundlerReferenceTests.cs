using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Searching;

/// <summary>
/// A bundle never pulls in a song the user owns through a reference file: such a song is never searched
/// for, upgraded or replaced, so a container grabbed for another song must not fetch it either.
/// </summary>
public sealed class ContainerBundlerReferenceTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [Fact]
    public async Task A_song_owned_through_a_reference_row_is_left_out_of_the_bundle_and_a_free_one_rides_along()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var alpha = await host.SeedSongAsync("Alpha");
        long artistId;

        await using (var seed = host.Database.CreateContext(host.Time))
        {
            artistId = (await seed.Songs.SingleAsync(song => song.Id == alpha, Token)).PrimaryArtistId;
        }

        var beta = await host.SeedSongAsync("Beta", artistId: artistId);
        var gamma = await host.SeedSongAsync("Gamma", artistId: artistId);

        // Beta is the user's own, through an Identified reference row.
        await host.SeedReferenceFileAsync(beta, ReferenceFileState.Identified);

        ContainerFile[] files =
        [
            new(0, "Selected Ambient Works/01 - Alpha.flac", 30_000_000),
            new(1, "Selected Ambient Works/02 - Beta.flac", 30_000_000),
            new(2, "Selected Ambient Works/03 - Gamma.flac", 30_000_000),
        ];

        var container = Container("Selected Ambient Works/01 - Alpha.flac");
        container = container with { Release = container.Release! with { Files = files } };
        host.Provider.Candidates.Add(container);

        var search = await host.Search.SearchAsync(alpha, SearchTrigger.Automatic, grab: false, Token);
        var candidateId = (await host.Runs.GetCandidatesAsync(search.SearchRunId, Token)).Single().Id;

        await host.Search.GrabCandidateAsync(candidateId, 1, Token);

        await using var context = host.Database.CreateContext(host.Time);

        (await context.QueueItems.AsNoTracking().AnyAsync(item => item.SongId == gamma, Token))
            .Should().BeTrue("a song nobody owns rides along");
        (await context.QueueItems.AsNoTracking().AnyAsync(item => item.SongId == beta, Token))
            .Should().BeFalse("the user owns it through a reference file");
        (await context.Candidates.AsNoTracking().AnyAsync(record => record.SongId == beta, Token))
            .Should().BeFalse();
    }

    private static Candidate Container(string path)
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        ContainerFile[] files = [new(0, path, 30_000_000)];

        return SearchTestHost.Candidate(path) with
        {
            Container = CandidateContainer.AlbumContainer,
            BlocklistKey = BlocklistKeys.Torrent(hash, path),
            Release = new ContainerRelease
            {
                IndexerId = 1,
                IndexerName = "Indexer",
                Title = "Aphex Twin - Selected Ambient Works [FLAC]",
                ReleaseId = "guid-bundler",
                DownloadUrl = "https://tracker.example/download.php?passkey=PASSKEY",
                InfoHash = hash,
                FileIndex = 0,
                Files = files,
            },
        };
    }
}
