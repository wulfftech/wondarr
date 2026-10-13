using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Jobs;

/// <summary>
/// The two batch loops wait for download slots without holding an executor place (R-067, owner
/// question 22): with both of them waiting and a long scan running, a user's "Search now" starts at once.
/// </summary>
public class BatchLoopExecutorTests
{
    [Fact]
    public async Task A_search_now_starts_at_once_while_both_batch_loops_wait_and_a_long_scan_runs()
    {
        var scan = new BlockingCommandHandler("LongScan");
        await using var host = await JobTestHost.CreateAsync(services => services.AddSingleton<ICommandHandler>(scan));

        long target = 0;

        await host.UseDatabaseAsync(async context =>
        {
            var artist = new Artist { Name = "Aphex Twin", SortName = "Aphex Twin" };
            context.Artists.Add(artist);
            await context.SaveChangesAsync();

            Song NewSong(string title) => new()
            {
                Title = title,
                ArtistCredit = artist.Name,
                PrimaryArtistId = artist.Id,
                Monitored = true,
                QualityProfileId = SeedData.StandardProfileId,
                LibraryId = SeedData.DefaultLibraryId,
                AddedBy = "api",
            };

            // One wanted song for MissingSearch, one below its cutoff for UpgradeSearch, and the one the
            // user asks for.
            var missing = NewSong("Missing");
            var upgrade = NewSong("Upgrade");
            var wanted = NewSong("Wanted");
            context.Songs.AddRange(missing, upgrade, wanted);
            await context.SaveChangesAsync();

            target = wanted.Id;
            context.SongFiles.Add(new SongFile
            {
                SongId = upgrade.Id,
                Path = "/data/music/upgrade.mp3",
                Size = 1,
                Codec = "mp3",
                Container = "mpeg",
                QualityId = 17,
                SourceType = SourceTypes.Soulseek,
                ImportedAt = host.TimeProvider.GetUtcNow().UtcDateTime,
            });

            // Every download slot is taken, so both loops have to wait.
            for (var index = 0; index < new SearchOptions().MaxActiveDownloads; index++)
            {
                var blocker = NewSong($"Blocker {index}");
                context.Songs.Add(blocker);
                await context.SaveChangesAsync();

                var run = new SearchRun { SongId = blocker.Id, Trigger = SearchTrigger.Automatic, StartedAt = host.TimeProvider.GetUtcNow().UtcDateTime };
                context.SearchRuns.Add(run);
                await context.SaveChangesAsync();

                var candidate = new CandidateRecord
                {
                    SearchRunId = run.Id,
                    SongId = blocker.Id,
                    SourceType = SourceTypes.Soulseek,
                    BlocklistKey = $"peer\u001fMusic\\{blocker.Id}.flac",
                    DisplayName = "one.flac",
                    RemotePath = $"Music\\{blocker.Id}.flac",
                    Provider = "peer",
                };
                context.Candidates.Add(candidate);
                await context.SaveChangesAsync();

                context.QueueItems.Add(new QueueItem
                {
                    SongId = blocker.Id,
                    CandidateId = candidate.Id,
                    SearchRunId = run.Id,
                    SourceType = SourceTypes.Soulseek,
                    State = QueueItemState.Downloading,
                    Destination = $"wondarr/{candidate.Id}",
                    Attempt = 1,
                });
            }
        });

        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        var longScan = await host.Queue.EnqueueAsync("LongScan", null, CommandTrigger.Manual, CancellationToken.None);
        var missingLoop = await host.Queue.EnqueueAsync("MissingSearch", null, CommandTrigger.Scheduled, CancellationToken.None);
        var upgradeLoop = await host.Queue.EnqueueAsync("UpgradeSearch", null, CommandTrigger.Scheduled, CancellationToken.None);

        foreach (var id in new[] { longScan.Id, missingLoop.Id, upgradeLoop.Id })
        {
            (await TestWait.UntilAsync(async () => await StatusAsync(host, id) == CommandStatus.Started))
                .Should().BeTrue("all three places are in use");
        }

        // Three commands hold the three places, two of them only waiting for a slot.
        var searchNow = await host.Queue.EnqueueAsync(
            "SongSearch",
            $"{{\"name\":\"SongSearch\",\"songId\":{target}}}",
            CommandTrigger.Manual,
            CancellationToken.None);

        (await TestWait.UntilAsync(async () => await StatusAsync(host, searchNow.Id) == CommandStatus.Completed))
            .Should().BeTrue("the waiting loops handed their places back, so the user's search is not queued behind them");

        (await StatusAsync(host, longScan.Id)).Should().Be(CommandStatus.Started);
        (await StatusAsync(host, missingLoop.Id)).Should().Be(CommandStatus.Started);
        (await StatusAsync(host, upgradeLoop.Id)).Should().Be(CommandStatus.Started);

        scan.Release();
        using var stopBudget = new CancellationTokenSource(TestWait.Timeout);
        await executor.StopAsync(stopBudget.Token);
    }

    private static async Task<CommandStatus?> StatusAsync(JobTestHost host, long id) =>
        (await host.Queue.GetAsync(id, CancellationToken.None))?.Status;
}
