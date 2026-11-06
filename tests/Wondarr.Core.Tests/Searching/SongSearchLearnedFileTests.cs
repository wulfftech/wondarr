using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Searching;

/// <summary>
/// A file the import learned to be a different recording is skipped when a run grabs its next
/// accepted candidate, even though that candidate was accepted before the rejection.
/// </summary>
public sealed class SongSearchLearnedFileTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [Fact]
    public async Task The_next_candidate_of_the_run_is_skipped_when_it_is_the_same_file_from_another_user()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        var first = SearchTestHost.Candidate(@"Music\Aphex Twin\Selected Ambient Works\01 - Alpha.flac", provider: "userA");
        var second = SearchTestHost.Candidate(@"Share\Deeper\selected ambient works\01 - ALPHA.mp3", provider: "userB", extension: "mp3");
        host.Provider.Candidates.Add(first);
        host.Provider.Candidates.Add(second);
        var search = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);

        // What the import writes when userA's file is fingerprinted as another recording.
        await host.Blocklist.AddAsync(Row(songId, first.BlocklistKey), Token);
        await host.Blocklist.AddAsync(Row(songId, BlocklistKeys.SoulseekFile(first.RemotePath, "Alpha")!), Token);

        var queueItemId = await host.Search.GrabBestAsync(search.SearchRunId, 1, Token);

        queueItemId.Should().BeNull("userB offers the file userA's copy of which was a different recording");
        host.Provider.Grabs.Should().BeEmpty();
    }

    private static BlocklistItem Row(long songId, string key) => new()
    {
        SongId = songId,
        SourceType = SourceTypes.Soulseek,
        BlocklistKey = key,
        Reason = "Fingerprint belongs to a different recording",
    };
}
