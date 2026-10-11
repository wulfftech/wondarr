using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Searching;

/// <summary>
/// R-082: a container's download links (they carry passkeys and API keys) are stored only on a
/// candidate that can still be grabbed, cleared once it is grabbed, failed or rejected, and refused
/// after a day.
/// </summary>
public sealed class DownloadLinkLifetimeTests
{
    private const string AcceptedPasskey = "ACCEPTED-PASSKEY-1234";
    private const string RejectedPasskey = "REJECTED-PASSKEY-5678";

    private static readonly CancellationToken Token = CancellationToken.None;

    [Fact]
    public async Task A_rejected_automatic_candidate_is_stored_without_its_link_and_an_accepted_one_keeps_it()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(Container("Selected Ambient Works/01 - Alpha.flac", AcceptedPasskey));
        host.Provider.Candidates.Add(Container("Selected Ambient Works/01 - Alpha (Live).flac", RejectedPasskey, VersionFlags.Live));

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);

        var stored = await host.Runs.GetCandidatesAsync(result.SearchRunId, Token);
        var accepted = stored.Single(record => record.Accepted);
        var rejected = stored.Single(record => !record.Accepted);
        accepted.Normalised.Should().Contain(AcceptedPasskey, "it can still be grabbed");
        rejected.Normalised.Should().NotContain(RejectedPasskey);
        rejected.Normalised.Should().Contain("Alpha (Live).flac", "the rest of the candidate is kept for the history");
    }

    [Fact]
    public async Task A_pushed_release_follows_the_same_rule()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        var rejected = Container("Selected Ambient Works/01 - Alpha (Live).flac", RejectedPasskey, VersionFlags.Live);

        var result = await host.Search.JudgeAsync(songId, [rejected], SearchTrigger.Push, grab: true, Token);

        (await host.Runs.GetCandidatesAsync(result.SearchRunId, Token)).Single().Normalised.Should().NotContain(RejectedPasskey);
    }

    [Fact]
    public async Task A_grabbed_candidate_s_link_is_cleared_once_the_source_has_it()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(Container("Selected Ambient Works/01 - Alpha.flac", AcceptedPasskey));

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

        result.Outcome.Should().Be(SearchOutcome.Grabbed);
        host.Provider.Grabs.Single().Candidate.Release!.DownloadUrl.Should().Contain(AcceptedPasskey, "the source needs the link to fetch the release");
        var record = (await host.Runs.GetCandidatesAsync(result.SearchRunId, Token)).Single();
        record.Grabbed.Should().BeTrue();
        record.Normalised.Should().NotContain(AcceptedPasskey);
        (await host.Context.QueueItems.SingleAsync(Token)).Handle.Should().NotContain(AcceptedPasskey);
    }

    [Fact]
    public async Task A_failed_grab_clears_the_link()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        var container = Container("Selected Ambient Works/01 - Alpha.flac", AcceptedPasskey);
        host.Provider.Candidates.Add(container);
        host.Provider.FailingKeys.Add(container.BlocklistKey);

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

        result.QueueItemId.Should().BeNull();
        var record = (await host.Runs.GetCandidatesAsync(result.SearchRunId, Token)).Single();
        record.Normalised.Should().NotContain(AcceptedPasskey);
    }

    [Fact]
    public async Task An_interactive_search_keeps_every_link_and_a_grab_within_the_window_works()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(Container("Selected Ambient Works/01 - Alpha.flac", AcceptedPasskey));
        host.Provider.Candidates.Add(Container("Selected Ambient Works/01 - Alpha (Live).flac", RejectedPasskey, VersionFlags.Live));

        var search = await host.Search.SearchAsync(songId, SearchTrigger.Manual, grab: false, Token);
        var stored = await host.Runs.GetCandidatesAsync(search.SearchRunId, Token);

        stored.Should().OnlyContain(record => record.Normalised.Contains(AcceptedPasskey) || record.Normalised.Contains(RejectedPasskey));

        host.Time.Advance(TimeSpan.FromHours(23));
        var queueItemId = await host.Search.GrabCandidateAsync(stored.Single(record => record.Accepted).Id, 1, Token);

        queueItemId.Should().BeGreaterThan(0);
        host.Provider.Grabs.Single().Candidate.Release!.DownloadUrl.Should().Contain(AcceptedPasskey);
    }

    [Fact]
    public async Task An_interactive_result_cannot_be_grabbed_after_a_day()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(Container("Selected Ambient Works/01 - Alpha.flac", AcceptedPasskey));
        var search = await host.Search.SearchAsync(songId, SearchTrigger.Manual, grab: false, Token);
        var candidateId = (await host.Runs.GetCandidatesAsync(search.SearchRunId, Token)).Single().Id;

        host.Time.Advance(TimeSpan.FromHours(25));
        var act = () => host.Search.GrabCandidateAsync(candidateId, 1, Token);

        (await act.Should().ThrowAsync<GrabFailedException>()).Which.Message.Should().Contain("search again");
        host.Provider.Grabs.Should().BeEmpty();
        (await host.Context.QueueItems.CountAsync(Token)).Should().Be(0, "no queue item is left behind");
    }

    [Fact]
    public async Task A_candidate_whose_link_was_cleared_is_refused_with_a_message_to_search_again()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(Container("Selected Ambient Works/01 - Alpha (Live).flac", RejectedPasskey, VersionFlags.Live));
        var search = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);
        var candidateId = (await host.Runs.GetCandidatesAsync(search.SearchRunId, Token)).Single().Id;

        var act = () => host.Search.GrabCandidateAsync(candidateId, 1, Token);

        (await act.Should().ThrowAsync<GrabFailedException>()).Which.Message.Should().Contain("search again");
    }

    /// <summary>A torrent container with one link, as an indexer lists it.</summary>
    private static Candidate Container(string path, string passkey, VersionFlags flags = VersionFlags.None)
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        ContainerFile[] files = [new(0, path, 30_000_000)];

        return SearchTestHost.Candidate(path, flags) with
        {
            Container = CandidateContainer.AlbumContainer,
            BlocklistKey = BlocklistKeys.Torrent(hash, path),
            Release = new ContainerRelease
            {
                IndexerId = 1,
                IndexerName = "Indexer",
                Title = "Aphex Twin - Selected Ambient Works [FLAC]",
                ReleaseId = "guid-" + path.Replace(" ", "_", StringComparison.Ordinal),
                DownloadUrl = "https://tracker.example/download.php?passkey=" + passkey,
                InfoHash = hash,
                FileIndex = 0,
                Files = files,
            },
        };
    }
}
