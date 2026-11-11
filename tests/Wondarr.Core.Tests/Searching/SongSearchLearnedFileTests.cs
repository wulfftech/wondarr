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

    [Fact]
    public async Task The_rest_of_the_run_skips_every_copy_titled_like_a_learned_other_recording()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync(title: "Please Come Tonight");
        var first = Named(@"Music\Aphex Twin\Both Sides\11 - Please Come Out Tonight.flac", "userA");
        var second = Named(@"Share\Aphex Twin\Love Songs\09. Please Come out Tonight.flac", "userB");
        host.Provider.Candidates.Add(first);
        host.Provider.Candidates.Add(second);
        var search = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);
        search.Decisions.Should().OnlyContain(decision => decision.Accepted, "every word of the song's title is in theirs");

        // What the import writes when userA's file is fingerprinted as "Please Come Out Tonight".
        await host.Blocklist.AddAsync(Row(songId, first.BlocklistKey), Token);
        await host.Blocklist.AddAsync(Row(songId, BlocklistKeys.OtherTitle("Please Come Out Tonight")), Token);

        var queueItemId = await host.Search.GrabBestAsync(search.SearchRunId, 1, Token);

        queueItemId.Should().BeNull("userB's file has the title the fingerprint named instead of the song");
        host.Provider.Grabs.Should().BeEmpty();
    }

    [Fact]
    public async Task The_rest_of_the_run_skips_copies_that_hold_the_learned_title_among_other_words()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync(title: "Please Come Tonight");
        var first = Named(@"Music\Aphex Twin\Both Sides\11 - Please Come Out Tonight.flac", "userA");
        var copies = new[]
        {
            Named(@"Music\Aphex Twin\Aphex Twin - Both Sides - 11 - Please Come Out Tonight FLAC.flac", "userB"),
            Named(@"music\Aphex Twin\111_aphex_twin_-_please_come_out_tonight_(2015_remaster)-soundly.mp3", "userC"),
            Named(@"music\Aphex Twin\111-aphex_twin-please_come_out_tonight.flac", "userD"),
        };
        host.Provider.Candidates.Add(first);
        host.Provider.Candidates.AddRange(copies);
        var search = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);
        search.Decisions.Should().OnlyContain(decision => decision.Accepted, "every word of the song's title is in theirs");

        // What the import writes when the first grab is fingerprinted as "Please Come Out Tonight".
        await host.Blocklist.AddAsync(Row(songId, first.BlocklistKey), Token);
        await host.Blocklist.AddAsync(Row(songId, BlocklistKeys.OtherTitle("Please Come Out Tonight")), Token);

        var queueItemId = await host.Search.GrabBestAsync(search.SearchRunId, 1, Token);

        queueItemId.Should().BeNull("the three copies are the recording the fingerprint named, however they are spelled");
        host.Provider.Grabs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_learned_other_title_leaves_a_copy_titled_like_the_song_to_be_grabbed()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync(title: "Please Come Tonight");
        var wrong = Named(@"Music\Aphex Twin\Both Sides\11 - Please Come Out Tonight.flac", "userA");
        var right = Named(@"Music\Aphex Twin\Singles\Aphex Twin - Please Come Tonight.flac", "userB");
        host.Provider.Candidates.Add(wrong);
        host.Provider.Candidates.Add(right);
        var search = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);
        await host.Blocklist.AddAsync(Row(songId, BlocklistKeys.OtherTitle("Please Come Out Tonight")), Token);

        var queueItemId = await host.Search.GrabBestAsync(search.SearchRunId, 1, Token);

        queueItemId.Should().NotBeNull();
        host.Provider.Grabs.Should().ContainSingle().Which.Candidate.RemotePath.Should().Be(right.RemotePath);
    }

    [Fact]
    public async Task The_rest_of_the_run_skips_compilation_files_that_pass_on_their_title_alone_after_two_misses()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        var first = Named(@"Share\Various Artists\Chill Out 2004\05 Alpha.flac", "userA");
        var second = Named(@"Share\VA - Ambient Hits\03 - Alpha.flac", "userB");
        host.Provider.Candidates.Add(first);
        host.Provider.Candidates.Add(second);
        var search = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);
        search.Decisions.Should().OnlyContain(decision => decision.Accepted, "a compilation file passes on its full title");

        await host.Blocklist.AddAsync(Row(songId, first.BlocklistKey), Token);
        await host.Blocklist.AddAsync(Row(songId, BlocklistKeys.CompilationMiss(first.BlocklistKey)), Token);
        await host.Blocklist.AddAsync(Row(songId, BlocklistKeys.CompilationMiss(BlocklistKeys.Soulseek("userX", @"VA\Hits\01 Alpha.mp3"))), Token);

        var queueItemId = await host.Search.GrabBestAsync(search.SearchRunId, 1, Token);

        queueItemId.Should().BeNull("two files like userB's were other recordings");
        host.Provider.Grabs.Should().BeEmpty();
    }

    /// <summary>A candidate parsed from its path the way the Soulseek source parses it.</summary>
    private static Candidate Named(string remotePath, string provider) =>
        SearchTestHost.Candidate(remotePath, provider: provider) with { Parsed = SoulseekFilenameParser.Parse(remotePath).Parsed };

    private static BlocklistItem Row(long songId, string key) => new()
    {
        SongId = songId,
        SourceType = SourceTypes.Soulseek,
        BlocklistKey = key,
        Reason = "Fingerprint belongs to a different recording",
    };
}
