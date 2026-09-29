using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Metadata;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Searching;

public sealed class SongSearchServiceTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    /// <summary>The shape the service stores its JSON columns in: camelCase, enums as strings.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public async Task Grabs_the_clean_candidate_and_records_the_live_and_radio_edit_rejections()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();

        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha (Live).flac", VersionFlags.Live));
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha (Radio Edit).flac", VersionFlags.RadioEdit));

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

        result.Outcome.Should().Be(SearchOutcome.Grabbed);
        result.Decisions.Should().HaveCount(3);
        result.QueueItemId.Should().NotBeNull();

        var stored = await host.Runs.GetCandidatesAsync(result.SearchRunId, Token);
        stored.Should().HaveCount(3);
        stored.Count(record => record.Accepted).Should().Be(1);

        var grabbed = stored.Single(record => record.Grabbed);
        grabbed.DisplayName.Should().Be("Alpha.flac");
        grabbed.Accepted.Should().BeTrue();
        grabbed.Score.Should().BeGreaterThanOrEqualTo(850);

        stored
            .Where(record => !record.Accepted)
            .Should()
            .OnlyContain(record => record.Rejections.Contains("versionMismatch"))
            .And.HaveCount(2);

        host.Provider.Grabs.Should().ContainSingle();
        host.Provider.Grabs[0].Candidate.DisplayName.Should().Be("Alpha.flac");

        var item = (await host.Queue.GetActiveAsync(Token)).Single();
        item.Id.Should().Be(result.QueueItemId!.Value);
        item.State.Should().Be(QueueItemState.Queued);
        // The folder is named after a fresh guid, not the row id: the item and its folder are
        // written in one save, so a grab can never leave a queue item with no folder.
        item.Destination.Should().StartWith("wondarr/").And.HaveLength("wondarr/".Length + 32);
        item.Handle.Should().NotBeNull();

        var history = await host.Context.History.AsNoTracking().ToListAsync(Token);
        history.Should().ContainSingle();
        history[0].EventType.Should().Be(HistoryEventType.Grabbed);
        history[0].SongId.Should().Be(songId);
        history[0].QualityId.Should().Be(36);
        history[0].Data.Should().Contain("\"destination\"");

        var run = await host.Runs.GetLatestAsync(songId, Token);
        run!.Outcome.Should().Be(SearchOutcome.Grabbed);
        run.Queries.Should().Equal("aphex twin alpha");
        run.Sources.Should().Equal(SourceTypes.Soulseek);
    }

    [Fact]
    public async Task An_automatic_search_asks_for_the_pool_check_and_a_manual_one_does_not()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);

        host.Provider.Requests.Should().ContainSingle();
        host.Provider.Requests[0].IsPoolGoodEnough.Should().NotBeNull();
        host.Provider.PoolVerdict.Should().BeTrue("the only candidate is a clean, fast, accepted one");

        host.Provider.Requests.Clear();

        await host.Search.SearchAsync(songId, SearchTrigger.Manual, grab: false, Token);

        host.Provider.Requests.Should().ContainSingle();
        host.Provider.Requests[0].IsPoolGoodEnough.Should().BeNull("an interactive search runs every query");
        host.Provider.PoolVerdict.Should().BeNull();
    }

    [Fact]
    public async Task A_search_with_no_available_source_finishes_as_source_unavailable()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Available = false;
        host.Provider.UnavailableReason = "Soulseek: not logged in";

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

        result.Outcome.Should().Be(SearchOutcome.SourceUnavailable);
        result.Message.Should().Be("Soulseek: not logged in");
        result.Decisions.Should().BeEmpty();
        result.QueueItemId.Should().BeNull();
        host.Provider.Requests.Should().BeEmpty();

        var run = await host.Runs.GetLatestAsync(songId, Token);
        run!.Outcome.Should().Be(SearchOutcome.SourceUnavailable);
        run.Message.Should().Be("Soulseek: not logged in");
    }

    [Fact]
    public async Task A_song_that_is_already_downloading_is_not_searched_again()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        await SeedActiveQueueItemAsync(host, songId);

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

        result.Outcome.Should().Be(SearchOutcome.Cancelled);
        result.Message.Should().Be("Already downloading");
        result.SearchRunId.Should().Be(0, "no run is opened when there is nothing to do");
        host.Provider.Requests.Should().BeEmpty();

        var runs = await host.Context.SearchRuns.AsNoTracking().ToListAsync(Token);
        runs.Should().ContainSingle();
    }

    [Fact]
    public async Task A_failed_grab_falls_through_to_the_next_candidate_and_records_the_failure()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();

        var best = SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac");
        var second = SearchTestHost.Candidate(
            "Music\\Aphex Twin\\Alpha.mp3",
            qualityId: 29,
            sizeBytes: 8_000_000,
            extension: "mp3",
            freeSlot: false,
            speed: 100_000);

        host.Provider.Candidates.Add(best);
        host.Provider.Candidates.Add(second);
        host.Provider.FailingKeys.Add(best.BlocklistKey);

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

        result.Outcome.Should().Be(SearchOutcome.Grabbed);
        host.Provider.Grabs.Should().HaveCount(2);

        var items = await host.Context.QueueItems.AsNoTracking().OrderBy(item => item.Id).ToListAsync(Token);
        items.Should().HaveCount(2);
        items[0].State.Should().Be(QueueItemState.Failed);
        items[0].Message.Should().Be("The peer went offline.");
        items[1].State.Should().Be(QueueItemState.Queued);
        items[1].Handle.Should().NotBeNull();
        result.QueueItemId.Should().Be(items[1].Id);

        var stored = await host.Runs.GetCandidatesAsync(result.SearchRunId, Token);
        stored.Should().OnlyContain(record => record.Grabbed, "a failed candidate is not retried");
    }

    [Fact]
    public async Task Blocklisted_keys_for_this_song_and_for_no_song_are_rejected_and_another_songs_are_not()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        var otherSongId = await host.SeedSongAsync("Beta");

        var thisSong = SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac");
        var noSong = SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha 2.flac");
        var otherSong = SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha 3.flac");

        await using (var context = host.Database.CreateContext(host.Time))
        {
            context.Blocklist.Add(new BlocklistItem
            {
                SongId = songId,
                SourceType = SourceTypes.Soulseek,
                BlocklistKey = thisSong.BlocklistKey,
                Reason = "failed verification",
            });

            context.Blocklist.Add(new BlocklistItem
            {
                SongId = null,
                SourceType = SourceTypes.Soulseek,
                BlocklistKey = noSong.BlocklistKey,
                Reason = "not the right file for anything",
            });

            context.Blocklist.Add(new BlocklistItem
            {
                SongId = otherSongId,
                SourceType = SourceTypes.Soulseek,
                BlocklistKey = otherSong.BlocklistKey,
                Reason = "another song's problem",
            });

            await context.SaveChangesAsync();
        }

        host.Provider.Candidates.Add(thisSong);
        host.Provider.Candidates.Add(noSong);

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);

        result.Decisions.Should().HaveCount(2);
        result.Decisions.Should().OnlyContain(decision =>
            decision.Rejections.Any(rejection => rejection.Reason == RejectionReason.Blocklisted));

        // The same file for another song is nobody's business but that song's.
        host.Provider.Candidates.Clear();
        host.Provider.Candidates.Add(otherSong);

        var other = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

        other.Outcome.Should().Be(SearchOutcome.Grabbed, other.Message);
        host.Provider.Grabs.Should().ContainSingle();
        host.Provider.Grabs[0].Candidate.BlocklistKey.Should().Be(otherSong.BlocklistKey);
    }

    [Fact]
    public async Task Stored_candidates_are_capped_at_the_configured_maximum()
    {
        await using var host = await SearchTestHost.CreateAsync(options => options.MaxStoredCandidates = 20);
        var songId = await host.SeedSongAsync();

        for (var index = 0; index < 25; index++)
        {
            host.Provider.Candidates.Add(
                SearchTestHost.Candidate($"Music\\Aphex Twin\\Alpha {index:00}.flac", (VersionFlags)(1 << 20)));
        }

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);

        result.Decisions.Should().HaveCount(25);

        var stored = await host.Runs.GetCandidatesAsync(result.SearchRunId, Token);
        stored.Should().HaveCount(20);

        var run = await host.Runs.GetLatestAsync(songId, Token);
        run!.CandidateCount.Should().Be(20);
    }

    [Fact]
    public async Task The_stored_normalised_candidate_round_trips_to_an_equal_candidate()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        var candidate = SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac");
        host.Provider.Candidates.Add(candidate);

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

        var stored = (await host.Runs.GetCandidatesAsync(result.SearchRunId, Token)).Single();

        var restored = JsonSerializer.Deserialize<Candidate>(stored.Normalised, Json);

        restored.Should().NotBeNull();
        restored.Should().BeEquivalentTo(candidate);

        var decision = result.Decisions.Single(decision => decision.Candidate.BlocklistKey == candidate.BlocklistKey);
        JsonSerializer.Deserialize<ScoreBreakdown>(stored.ScoreBreakdown, Json)
            .Should()
            .BeEquivalentTo(decision.Score);
    }

    [Fact]
public async Task A_search_whose_source_throws_finishes_the_run_as_failed()
{
    await using var host = await SearchTestHost.CreateAsync();
    var songId = await host.SeedSongAsync();
    host.Provider.FailingTitles.Add("Alpha");

    var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
        () => host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token));

    var run = await host.Runs.GetLatestAsync(songId, Token);

    run!.Outcome.Should().Be(SearchOutcome.Failed, "a run that errored must not be left open");
    run.Message.Should().Be(thrown.Message);
    run.FinishedAt.Should().NotBeNull();
}

[Fact]
public async Task A_cancelled_search_finishes_the_run_as_cancelled()
{
    await using var host = await SearchTestHost.CreateAsync();
    var songId = await host.SeedSongAsync();
    host.Provider.SearchFailure = new OperationCanceledException("The search was stopped.");

    await Assert.ThrowsAsync<OperationCanceledException>(
        () => host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token));

    var run = await host.Runs.GetLatestAsync(songId, Token);

    run!.Outcome.Should().Be(SearchOutcome.Cancelled);
    run.FinishedAt.Should().NotBeNull("the cancelled token must not leave the run open");
}

[Fact]
public async Task Two_grabs_for_one_song_leave_one_queue_item_and_the_loser_is_told()
{
    await using var host = await SearchTestHost.CreateAsync();
    var songId = await host.SeedSongAsync();
    host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

    var search = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);
    var candidateId = (await host.Runs.GetCandidatesAsync(search.SearchRunId, Token)).Single().Id;

    using var first = host.CreateScope();
    using var second = host.CreateScope();

    var outcomes = await Task.WhenAll(
        AttemptAsync(first.ServiceProvider.GetRequiredService<ISongSearchService>(), candidateId),
        AttemptAsync(second.ServiceProvider.GetRequiredService<ISongSearchService>(), candidateId));

    outcomes.Count(outcome => outcome is null).Should().Be(1, "only one grab may win");
    outcomes.Single(outcome => outcome is not null).Should().BeOfType<AlreadyDownloadingException>();

    await using var context = host.Database.CreateContext(host.Time);
    var items = await context.QueueItems.AsNoTracking().Where(item => item.SongId == songId).ToListAsync(Token);

    items.Should().ContainSingle("the database, not the pre-check, decides who wins");
    items[0].State.Should().Be(QueueItemState.Queued);
}

[Fact]
public async Task A_grab_whose_source_fails_leaves_one_failed_item_and_no_orphan()
{
    await using var host = await SearchTestHost.CreateAsync();
    var songId = await host.SeedSongAsync();
    var candidate = SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac");
    host.Provider.Candidates.Add(candidate);
    host.Provider.FailingKeys.Add(candidate.BlocklistKey);

    var search = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);
    var candidateId = (await host.Runs.GetCandidatesAsync(search.SearchRunId, Token)).Single().Id;

    await Assert.ThrowsAsync<GrabFailedException>(
        () => host.Search.GrabCandidateAsync(candidateId, 1, Token));

    await using var context = host.Database.CreateContext(host.Time);
    var items = await context.QueueItems.AsNoTracking().ToListAsync(Token);

    items.Should().ContainSingle("a failed grab leaves the item it created, and nothing else");
    items[0].State.Should().Be(QueueItemState.Failed);
    items[0].Message.Should().Be("The peer went offline.");
    items[0].Destination.Should().StartWith("wondarr/");

    (await host.Runs.GetCandidatesAsync(search.SearchRunId, Token))
        .Single()
        .Grabbed.Should()
        .BeTrue("the failed candidate is not retried");
}

[Fact]
public async Task A_grab_cancelled_by_the_peer_leaves_the_item_failed_and_not_queued()
{
    await using var host = await SearchTestHost.CreateAsync();
    var songId = await host.SeedSongAsync();
    var candidate = SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac");
    host.Provider.Candidates.Add(candidate);
    host.Provider.CancellingKeys.Add(candidate.BlocklistKey);

    var search = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);
    var candidateId = (await host.Runs.GetCandidatesAsync(search.SearchRunId, Token)).Single().Id;

    await Assert.ThrowsAsync<OperationCanceledException>(
        () => host.Search.GrabCandidateAsync(candidateId, 1, Token));

    await using var context = host.Database.CreateContext(host.Time);
    var items = await context.QueueItems.AsNoTracking().ToListAsync(Token);

    items.Should().ContainSingle();
    items[0].State.Should().Be(QueueItemState.Failed, "a queued row would look like a live download");
    items[0].Message.Should().Be("Cancelled before the peer answered");
}

[Fact]
public async Task The_grab_fall_through_stops_at_the_attempt_budget()
{
    await using var host = await SearchTestHost.CreateAsync(options => options.MaxAutoAttemptsPerSearch = 3);
    var songId = await host.SeedSongAsync();

    for (var index = 0; index < 6; index++)
    {
        var candidate = SearchTestHost.Candidate($"Music\\Aphex Twin\\Alpha {index:00}.flac");
        host.Provider.Candidates.Add(candidate);
        host.Provider.FailingKeys.Add(candidate.BlocklistKey);
    }

    var search = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);

    var grabbed = await host.Search.GrabBestAsync(search.SearchRunId, 1, Token);

    grabbed.Should().BeNull();
    host.Provider.Grabs.Should().HaveCount(3, "the budget is the attempts left of the configured maximum");

    await using var context = host.Database.CreateContext(host.Time);
    (await context.QueueItems.AsNoTracking().ToListAsync(Token))
        .Should()
        .HaveCount(3)
        .And.OnlyContain(item => item.State == QueueItemState.Failed);
}

[Fact]
public async Task An_interactive_search_with_an_acceptable_candidate_finishes_cancelled()
{
    await using var host = await SearchTestHost.CreateAsync();
    var songId = await host.SeedSongAsync("Alpha");
    var rejected = await host.SeedSongAsync("Beta");

    host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

    var acceptable = await host.Search.SearchAsync(songId, SearchTrigger.Manual, grab: false, Token);

    acceptable.Outcome.Should().Be(SearchOutcome.Cancelled, "an interactive search did its job");
    acceptable.Message.Should().Be("Interactive search: 1 acceptable");
    acceptable.QueueItemId.Should().BeNull();
    host.Provider.Grabs.Should().BeEmpty();

    // Nothing acceptable is still nothing acceptable: the interactive search is not exempt.
    host.Provider.Candidates.Clear();
    host.Provider.Candidates.Add(
        SearchTestHost.Candidate("Music\\Aphex Twin\\Beta (Live).flac", VersionFlags.Live));

    var nothing = await host.Search.SearchAsync(rejected, SearchTrigger.Manual, grab: false, Token);

    nothing.Outcome.Should().Be(SearchOutcome.NoAcceptableCandidate);
}

[Fact]
public async Task An_automatic_grab_does_not_start_without_a_free_download_slot()
{
    await using var host = await SearchTestHost.CreateAsync();
    var songId = await host.SeedSongAsync("Alpha");
    host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

    for (var index = 0; index < 3; index++)
    {
        var blocker = await host.SeedSongAsync($"Blocker {index}");
        await SeedActiveQueueItemAsync(host, blocker);
    }

    var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

    result.Outcome.Should().Be(SearchOutcome.Cancelled);
    result.Message.Should().Be("No free download slot");
    result.QueueItemId.Should().BeNull();
    host.Provider.Grabs.Should().BeEmpty("three downloads in flight is the limit");
}

[Fact]
public async Task A_candidate_the_reputation_rejects_afterwards_does_not_fail_the_run()
{
    await using var host = await SearchTestHost.CreateAsync();
    var songId = await host.SeedSongAsync();

    var rejectedLater = SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha (bad peer).flac", provider: "badpeer");
    var grabbed = SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha (good peer).flac", provider: "goodpeer");

    host.Provider.Candidates.Add(rejectedLater);
    host.Provider.Candidates.Add(grabbed);

    // The early-stop callback only sees the bad peer's file, before the reputation is read, and
    // accepts it: the pool is good enough and the source may stop asking.
    host.Provider.PoolCandidates = [rejectedLater];

    for (var failure = 0; failure < 2; failure++)
    {
        await host.Users.RecordFailureAsync("badpeer", Token);
    }

    var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

    host.Provider.PoolVerdict.Should().BeTrue("the pool was judged before the reputation was known");
    result.Outcome.Should().Be(SearchOutcome.Grabbed, "the final evaluation keeps the candidate it accepts");
    host.Provider.Grabs.Should().ContainSingle();
    host.Provider.Grabs[0].Candidate.BlocklistKey.Should().Be(grabbed.BlocklistKey);

    result
        .Decisions.Single(decision => decision.Candidate.BlocklistKey == rejectedLater.BlocklistKey)
        .Rejections.Should()
        .Contain(rejection => rejection.Reason == RejectionReason.UserOnCooldown);
}

[Fact]
public async Task The_search_now_command_reports_an_already_downloading_song_as_a_result()
{
    await using var host = await SearchTestHost.CreateAsync();
    var songId = await host.SeedSongAsync();
    await SeedActiveQueueItemAsync(host, songId);

    var handler = new SongSearchCommandHandler(host.Search, NullLogger<SongSearchCommandHandler>.Instance);
    var context = new CommandContext(
        1,
        $"{{\"songId\":{songId}}}",
        CommandTrigger.Manual,
        _ => Task.CompletedTask);

    var message = await handler.ExecuteAsync(context, Token);

    message.Should().Be($"Song {songId}: Cancelled — Already downloading");
    host.Provider.Requests.Should().BeEmpty();
}

/// <summary>Grabs a candidate on its own service, reporting what stopped it instead of throwing.</summary>
private static async Task<Exception?> AttemptAsync(ISongSearchService search, long candidateId)
{
    try
    {
        await search.GrabCandidateAsync(candidateId, 1, Token);
        return null;
    }
    catch (Exception exception)
    {
        return exception;
    }
}

/// <summary>Seeds a run, a candidate and one active queue item for the song.</summary>
private static async Task SeedActiveQueueItemAsync(SearchTestHost host, long songId)
    {
        await using var context = host.Database.CreateContext(host.Time);

        var run = new SearchRun
        {
            SongId = songId,
            Trigger = SearchTrigger.Automatic,
            StartedAt = host.Time.GetUtcNow().UtcDateTime,
        };

        context.SearchRuns.Add(run);
        await context.SaveChangesAsync();

        var candidate = new CandidateRecord
        {
            SearchRunId = run.Id,
            SongId = songId,
            SourceType = SourceTypes.Soulseek,
            BlocklistKey = "peer\u001fMusic\\one.flac",
            DisplayName = "one.flac",
            RemotePath = "Music\\one.flac",
            Provider = "peer",
        };

        context.Candidates.Add(candidate);
        await context.SaveChangesAsync();

        context.QueueItems.Add(new QueueItem
        {
            SongId = songId,
            CandidateId = candidate.Id,
            SearchRunId = run.Id,
            SourceType = SourceTypes.Soulseek,
            State = QueueItemState.Queued,
            Destination = $"wondarr/{run.Id}",
            Attempt = 1,
        });

        await context.SaveChangesAsync();
    }
}
