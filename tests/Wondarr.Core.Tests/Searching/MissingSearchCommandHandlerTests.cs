using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Searching;

public sealed class MissingSearchCommandHandlerTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [Theory]
    [InlineData(0, 0.5, false)]
    [InlineData(0, 2.0, true)]
    [InlineData(1, 0.5, false)]
    [InlineData(1, 2.0, true)]
    [InlineData(2, 5.0, false)]
    [InlineData(2, 7.0, true)]
    [InlineData(5, 100.0, false)]
    [InlineData(5, 200.0, true)]
    public async Task Missing_search_backs_off_by_the_number_of_fruitless_runs(
        int fruitlessRuns,
        double hoursSinceLastRun,
        bool expectedToSearch)
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        await SeedRunsAsync(host, songId, fruitlessRuns, hoursSinceLastRun);

        var progress = new List<string>();
        var message = await Handler(host).ExecuteAsync(Context(progress), Token);

        host.Provider.Requests.Should().HaveCount(expectedToSearch ? 1 : 0);

        if (expectedToSearch)
        {
            message.Should().Be("1 songs: 1 grabbed, 0 without an acceptable result, 0 skipped");
            progress.Should().ContainSingle().Which.Should().Be("Searched 1 of 1: 1 grabbed");
        }
        else
        {
            message.Should().Be("0 songs: 0 grabbed, 0 without an acceptable result, 0 skipped");
        }
    }

    [Fact]
    public async Task Missing_search_waits_for_a_download_slot_before_it_searches()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        // Three downloads are in flight and the limit is three: nothing may start.
        var items = new List<long>();

        for (var index = 0; index < 3; index++)
        {
            var blocker = await host.SeedSongAsync($"Blocker {index}");
            items.AddRange(await SeedActiveQueueItemsAsync(host, blocker, 1));
        }

        var running = Handler(host).ExecuteAsync(Context([]), Token);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            host.Time.Advance(TimeSpan.FromSeconds(host.Options.SlotWaitSeconds));
            await Task.Delay(20);
        }

        host.Provider.Requests.Should().BeEmpty("every download slot is taken");

        // One download finishes, which frees the slot the search has been waiting for.
        await using (var context = host.Database.CreateContext(host.Time))
        {
            var finished = await context.QueueItems.FirstAsync(item => item.Id == items[0], Token);
            finished.State = QueueItemState.Failed;
            await context.SaveChangesAsync(Token);
        }

        for (var attempt = 0; attempt < 20 && !running.IsCompleted; attempt++)
        {
            host.Time.Advance(TimeSpan.FromSeconds(host.Options.SlotWaitSeconds));
            await Task.Delay(20);
        }

        var message = await running.WaitAsync(TimeSpan.FromSeconds(10));

        host.Provider.Requests.Should().ContainSingle();
        message.Should().Be("1 songs: 1 grabbed, 0 without an acceptable result, 0 skipped");
    }

    [Fact]
    public async Task Missing_search_respects_the_batch_size_and_goes_on_after_a_failing_song()
    {
        await using var host = await SearchTestHost.CreateAsync(options => options.MissingBatchSize = 2);
        await host.SeedSongAsync("Alpha");
        await host.SeedSongAsync("Broken");
        await host.SeedSongAsync("Gamma");

        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        host.Provider.FailingTitles.Add("Broken");

        var progress = new List<string>();
        var message = await Handler(host).ExecuteAsync(Context(progress), Token);

        host.Provider.Requests.Should().HaveCount(2, "the batch size caps how many songs are searched");
        host.Provider.Requests.Select(request => request.Title).Should().Equal("Alpha", "Broken");

        message.Should().Be("2 songs: 1 grabbed, 0 without an acceptable result, 1 skipped");
        progress.Should().Equal("Searched 1 of 2: 1 grabbed", "Searched 2 of 2: 1 grabbed");
    }

    [Fact]
    public async Task Missing_search_leaves_unmonitored_songs_and_songs_already_downloading_alone()
    {
        await using var host = await SearchTestHost.CreateAsync();
        await host.SeedSongAsync("Alpha");
        await host.SeedSongAsync("Beta", monitored: false);
        var downloading = await host.SeedSongAsync("Gamma");
        await SeedActiveQueueItemsAsync(host, downloading, 1);

        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        var message = await Handler(host).ExecuteAsync(Context([]), Token);

        message.Should().Be("1 songs: 1 grabbed, 0 without an acceptable result, 0 skipped");
        host.Provider.Requests.Should().ContainSingle();
        host.Provider.Requests[0].Title.Should().Be("Alpha");
    }

    /// <summary>Seeds the song's run history: <paramref name="fruitlessRuns"/> fruitless runs then an older good one.</summary>
    private static async Task SeedRunsAsync(
        SearchTestHost host,
        long songId,
        int fruitlessRuns,
        double hoursSinceLastRun)
    {
        await using var context = host.Database.CreateContext(host.Time);
        var now = host.Time.GetUtcNow().UtcDateTime;

        if (fruitlessRuns == 0)
        {
            context.SearchRuns.Add(new SearchRun
            {
                SongId = songId,
                Trigger = SearchTrigger.Automatic,
                StartedAt = now.AddHours(-hoursSinceLastRun),
                FinishedAt = now.AddHours(-hoursSinceLastRun),
                Outcome = SearchOutcome.Grabbed,
            });
        }
        else
        {
            for (var index = fruitlessRuns; index >= 1; index--)
            {
                context.SearchRuns.Add(new SearchRun
                {
                    SongId = songId,
                    Trigger = SearchTrigger.Automatic,
                    StartedAt = now.AddHours(-hoursSinceLastRun).AddHours(-(index - 1)),
                    FinishedAt = now.AddHours(-hoursSinceLastRun).AddHours(-(index - 1)),
                    Outcome = SearchOutcome.NoResults,
                });
            }
        }

        await context.SaveChangesAsync();
    }

    /// <summary>Seeds <paramref name="count"/> grabs in flight for the song and returns their ids.</summary>
    private static async Task<List<long>> SeedActiveQueueItemsAsync(SearchTestHost host, long songId, int count)
    {
        await using var context = host.Database.CreateContext(host.Time);
        var now = host.Time.GetUtcNow().UtcDateTime;

        var run = new SearchRun
        {
            SongId = songId,
            Trigger = SearchTrigger.Automatic,
            StartedAt = now,
            Outcome = SearchOutcome.Grabbed,
        };

        context.SearchRuns.Add(run);
        await context.SaveChangesAsync();

        var ids = new List<long>();

        for (var index = 0; index < count; index++)
        {
            var candidate = new CandidateRecord
            {
                SearchRunId = run.Id,
                SongId = songId,
                SourceType = SourceTypes.Soulseek,
                BlocklistKey = $"peer\u001fMusic\\other{index}.flac",
                DisplayName = $"other{index}.flac",
                RemotePath = $"Music\\other{index}.flac",
                Provider = "peer",
            };

            context.Candidates.Add(candidate);
            await context.SaveChangesAsync();

            var item = new QueueItem
            {
                SongId = songId,
                CandidateId = candidate.Id,
                SearchRunId = run.Id,
                SourceType = SourceTypes.Soulseek,
                State = QueueItemState.Downloading,
                Destination = $"wondarr/{candidate.Id}",
                Attempt = 1,
            };

            context.QueueItems.Add(item);
            await context.SaveChangesAsync();

            ids.Add(item.Id);
        }

        return ids;
    }

    private static MissingSearchCommandHandler Handler(SearchTestHost host) =>
        new(host.Scopes, host.Monitor, host.Time, NullLogger<MissingSearchCommandHandler>.Instance);

    private static CommandContext Context(List<string> progress) =>
        new(1, null, CommandTrigger.Scheduled, message =>
        {
            progress.Add(message);
            return Task.CompletedTask;
        });
}
