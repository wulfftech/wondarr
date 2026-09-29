using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
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
        item.Destination.Should().Be($"wondarr/{item.Id}");
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
