using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Searching;

/// <summary>
/// The upgrade loop's selection and cadence (MATCHING_ENGINE §6.6): which songs it takes, which it
/// leaves alone, and when its own backoff lets a song come back. The search itself is the same
/// <see cref="ISongSearchService"/> every other path uses, so these only pin the picking.
/// </summary>
public sealed class UpgradeSearchCommandHandlerTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [Fact]
    public async Task An_upgrade_search_grabs_a_better_file_for_a_song_below_cutoff()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        await host.SeedFileAsync(songId, qualityId: 23);

        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        var progress = new List<string>();
        var message = await Handler(host).ExecuteAsync(Context(progress), Token);

        host.Provider.Requests.Should().ContainSingle();
        message.Should().Be("1 songs: 1 grabbed, 0 without an acceptable result, 0 skipped");
        progress.Should().Equal("Searched 1 of 1: 1 grabbed");

        var run = await host.Runs.GetLatestAsync(songId, Token);
        run!.Trigger.Should().Be(SearchTrigger.Upgrade, "the upgrade loop's runs are its own");
    }

    [Fact]
    public async Task The_upgrade_search_leaves_cutoff_met_songs_reference_files_and_unmonitored_songs_alone()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var belowCutoff = await host.SeedSongAsync("Alpha");
        var cutoffMet = await host.SeedSongAsync("Beta");
        var reference = await host.SeedSongAsync("Gamma");
        var unmonitored = await host.SeedSongAsync("Delta", monitored: false);

        await host.SeedFileAsync(belowCutoff, qualityId: 23);
        await host.SeedFileAsync(cutoffMet, qualityId: 29);
        await host.SeedFileAsync(reference, qualityId: 23, sourceType: SourceTypes.Reference);
        await host.SeedFileAsync(unmonitored, qualityId: 23);

        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        var message = await Handler(host).ExecuteAsync(Context([]), Token);

        host.Provider.Requests.Should().ContainSingle();
        host.Provider.Requests[0].Title.Should().Be("Alpha");
        message.Should().Be("1 songs: 1 grabbed, 0 without an acceptable result, 0 skipped");
    }

    [Fact]
    public async Task A_song_with_a_grab_in_flight_is_not_upgraded()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        await host.SeedFileAsync(songId, qualityId: 23);

        await using (var context = host.Database.CreateContext(host.Time))
        {
            var now = host.Time.GetUtcNow().UtcDateTime;
            var run = new SearchRun
            {
                SongId = songId,
                Trigger = SearchTrigger.Automatic,
                StartedAt = now,
                Outcome = SearchOutcome.Grabbed,
            };

            context.SearchRuns.Add(run);
            await context.SaveChangesAsync(Token);

            var candidate = new CandidateRecord
            {
                SearchRunId = run.Id,
                SongId = songId,
                SourceType = SourceTypes.Soulseek,
                BlocklistKey = "peer\u001fMusic\\inflight.flac",
                DisplayName = "inflight.flac",
                RemotePath = "Music\\inflight.flac",
                Provider = "peer",
            };

            context.Candidates.Add(candidate);
            await context.SaveChangesAsync(Token);

            context.QueueItems.Add(new QueueItem
            {
                SongId = songId,
                CandidateId = candidate.Id,
                SearchRunId = run.Id,
                SourceType = SourceTypes.Soulseek,
                State = QueueItemState.Downloading,
                Destination = $"wondarr/{run.Id}",
                Attempt = 1,
            });

            await context.SaveChangesAsync(Token);
        }

        var message = await Handler(host).ExecuteAsync(Context([]), Token);

        host.Provider.Requests.Should().BeEmpty("the song already has a grab in flight");
        message.Should().Be("0 songs: 0 grabbed, 0 without an acceptable result, 0 skipped");
    }

    [Theory]
    [InlineData(0, 0.5, false)]
    [InlineData(0, 2.0, true)]
    [InlineData(1, 0.5, false)]
    [InlineData(1, 2.0, true)]
    [InlineData(2, 5.0, false)]
    [InlineData(2, 25.0, true)]
    [InlineData(5, 100.0, false)]
    [InlineData(5, 200.0, true)]
    public async Task The_upgrade_backoff_counts_only_upgrade_runs(
        int fruitlessRuns,
        double hoursSinceLastRun,
        bool expectedToSearch)
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        await host.SeedFileAsync(songId, qualityId: 23);

        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        await SeedUpgradeRunsAsync(host, songId, fruitlessRuns, hoursSinceLastRun);

        var message = await Handler(host).ExecuteAsync(Context([]), Token);

        host.Provider.Requests.Should().HaveCount(expectedToSearch ? 1 : 0);

        if (expectedToSearch)
        {
            message.Should().Be("1 songs: 1 grabbed, 0 without an acceptable result, 0 skipped");
        }
        else
        {
            message.Should().Be("0 songs: 0 grabbed, 0 without an acceptable result, 0 skipped");
        }
    }

    [Fact]
    public async Task A_missing_song_run_neither_resets_nor_delays_the_upgrade_backoff()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        await host.SeedFileAsync(songId, qualityId: 23);

        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        await using (var context = host.Database.CreateContext(host.Time))
        {
            var now = host.Time.GetUtcNow().UtcDateTime;

            // A fruitless upgrade run half an hour ago: the first backoff step (1 h) is not over.
            context.SearchRuns.Add(new SearchRun
            {
                SongId = songId,
                Trigger = SearchTrigger.Upgrade,
                StartedAt = now.AddMinutes(-30),
                FinishedAt = now.AddMinutes(-30),
                Outcome = SearchOutcome.NoResults,
            });

            // A missing-song run that grabbed something five hours ago: it must neither reset the
            // upgrade backoff nor delay it.
            context.SearchRuns.Add(new SearchRun
            {
                SongId = songId,
                Trigger = SearchTrigger.Automatic,
                StartedAt = now.AddHours(-5),
                FinishedAt = now.AddHours(-5),
                Outcome = SearchOutcome.Grabbed,
            });

            await context.SaveChangesAsync();
        }

        var message = await Handler(host).ExecuteAsync(Context([]), Token);

        host.Provider.Requests.Should().BeEmpty("the fruitless upgrade run still holds the song back");
        message.Should().Be("0 songs: 0 grabbed, 0 without an acceptable result, 0 skipped");
    }

    [Fact]
    public async Task The_upgrade_search_respects_the_batch_size_and_takes_the_longest_waiting_first()
    {
        await using var host = await SearchTestHost.CreateAsync(options => options.UpgradeBatchSize = 2);

        var oldest = await host.SeedSongAsync("Alpha");
        var middle = await host.SeedSongAsync("Beta");
        var newest = await host.SeedSongAsync("Gamma");

        var now = host.Time.GetUtcNow().UtcDateTime;

        await host.SeedFileAsync(oldest, qualityId: 23, importedAt: now.AddDays(-10));
        await host.SeedFileAsync(middle, qualityId: 23, importedAt: now.AddDays(-5));
        await host.SeedFileAsync(newest, qualityId: 23, importedAt: now);

        var message = await Handler(host).ExecuteAsync(Context([]), Token);

        host.Provider.Requests.Should().HaveCount(2, "the batch size caps how many songs are searched");
        host.Provider.Requests.Select(request => request.Title).Should().Equal("Alpha", "Beta");
        message.Should().Be("2 songs: 0 grabbed, 2 without an acceptable result, 0 skipped");
    }

    /// <summary>Seeds the song's upgrade-run history: <paramref name="fruitlessRuns"/> fruitless runs then an older good one.</summary>
    private static async Task SeedUpgradeRunsAsync(
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
                Trigger = SearchTrigger.Upgrade,
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
                    Trigger = SearchTrigger.Upgrade,
                    StartedAt = now.AddHours(-hoursSinceLastRun).AddHours(-(index - 1)),
                    FinishedAt = now.AddHours(-hoursSinceLastRun).AddHours(-(index - 1)),
                    Outcome = SearchOutcome.NoResults,
                });
            }
        }

        await context.SaveChangesAsync();
    }

    private static UpgradeSearchCommandHandler Handler(SearchTestHost host) =>
        new(host.Scopes, host.Monitor, host.Time, NullLogger<UpgradeSearchCommandHandler>.Instance);

    private static CommandContext Context(List<string> progress) =>
        new(1, null, CommandTrigger.Scheduled, message =>
        {
            progress.Add(message);
            return Task.CompletedTask;
        });
}
