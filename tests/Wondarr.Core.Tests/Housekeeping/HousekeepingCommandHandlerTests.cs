using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Housekeeping;
using Wondarr.Core.Jobs;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.Housekeeping;

public class HousekeepingCommandHandlerTests
{
    private static readonly CommandContext NoContext = new(1, null, CommandTrigger.Scheduled, _ => Task.CompletedTask);

    [Fact]
    public async Task Old_finished_commands_go_while_recent_ones_and_old_queued_or_started_ones_stay()
    {
        await using var host = await HousekeepingHost.CreateAsync();
        var now = host.Now;

        await host.SeedAsync(context =>
        {
            context.Commands.AddRange(
                Command("OldCompleted", CommandStatus.Completed, endedAt: now.AddDays(-8)),
                Command("OldFailed", CommandStatus.Failed, endedAt: now.AddDays(-30)),
                Command("OldCancelled", CommandStatus.Cancelled, endedAt: null, queuedAt: now.AddDays(-9)),
                Command("OldOrphaned", CommandStatus.Orphaned, endedAt: now.AddDays(-10)),
                Command("OldAborted", CommandStatus.Aborted, endedAt: now.AddDays(-10)),
                Command("RecentCompleted", CommandStatus.Completed, endedAt: now.AddDays(-6)),
                Command("OldQueued", CommandStatus.Queued, endedAt: null, queuedAt: now.AddDays(-40)),
                Command("OldStarted", CommandStatus.Started, endedAt: null, queuedAt: now.AddDays(-40)));
        });

        var message = await host.Handler.ExecuteAsync(NoContext, CancellationToken.None);

        message.Should().StartWith("commands 5,");
        (await host.ReadAsync(context => context.Commands.Select(command => command.Name).ToListAsync()))
            .Should().BeEquivalentTo("RecentCompleted", "OldQueued", "OldStarted");
    }

    [Fact]
    public async Task An_old_search_run_goes_with_its_candidates_but_runs_and_candidates_in_use_stay()
    {
        await using var host = await HousekeepingHost.CreateAsync();
        var old = host.Now.AddDays(-31);
        var songs = new List<long>();

        await host.SeedAsync(context =>
        {
            for (var index = 0; index < 5; index++)
            {
                var artist = new Artist { Name = $"Artist {index}", SortName = $"Artist {index}" };
                var song = new Song
                {
                    Title = $"Song {index}",
                    ArtistCredit = artist.Name,
                    PrimaryArtist = artist,
                    QualityProfileId = SeedData.StandardProfileId,
                    LibraryId = SeedData.DefaultLibraryId,
                    AddedBy = "test",
                };
                context.Songs.Add(song);
            }
        });
        songs.AddRange(await host.ReadAsync(context => context.Songs.OrderBy(song => song.Id).Select(song => song.Id).ToListAsync()));

        // 0: plain old run, with two candidates -> deleted
        // 1: old run with a queue item of its own -> kept
        // 2: old run whose candidate a queue item (of another run) points at -> kept
        // 3: old run whose candidate a song file's source_ref names -> kept
        // 4: a recent run -> kept
        var runs = new List<long>();
        var candidates = new Dictionary<int, long>();

        await host.SeedAsync(context =>
        {
            for (var index = 0; index < 5; index++)
            {
                var run = new SearchRun
                {
                    SongId = songs[index],
                    Trigger = SearchTrigger.Automatic,
                    StartedAt = index == 4 ? host.Now.AddDays(-1) : old,
                    Outcome = SearchOutcome.NoResults,
                };
                context.SearchRuns.Add(run);
                context.Candidates.Add(Candidate(run, songs[index], $"a{index}"));
                context.Candidates.Add(Candidate(run, songs[index], $"b{index}"));
            }
        });
        runs.AddRange(await host.ReadAsync(context => context.SearchRuns.OrderBy(run => run.Id).Select(run => run.Id).ToListAsync()));
        foreach (var candidate in await host.ReadAsync(context => context.Candidates.ToListAsync()))
        {
            candidates[(int)(candidate.SearchRunId - runs[0])] = candidate.Id;
        }

        // Run 5 is the "other" run the queue item of case 2 belongs to; make it recent so it is kept anyway.
        await host.SeedAsync(context =>
        {
            context.QueueItems.Add(QueueItem(songs[1], candidates[1], runs[1]));

            // Case 2: the queue item's run is run 4 (recent), its candidate belongs to old run 2.
            context.QueueItems.Add(QueueItem(songs[2], candidates[2], runs[4]));

            var file = new SongFile
            {
                SongId = songs[3],
                Path = "/music/x.flac",
                Codec = "flac",
                Container = "flac",
                QualityId = 36,
                SourceType = "soulseek",
                SourceRef = $"{{\"provider\":null,\"remotePath\":\"x\",\"candidateId\":{candidates[3]},\"queueItemId\":99,\"searchRunId\":{runs[3] + 1000}}}",
                ImportedAt = host.Now,
            };
            context.SongFiles.Add(file);
        });

        var message = await host.Handler.ExecuteAsync(NoContext, CancellationToken.None);

        message.Should().Contain("search runs 1 (candidates 2)");
        (await host.ReadAsync(context => context.SearchRuns.Select(run => run.Id).ToListAsync()))
            .Should().BeEquivalentTo(new[] { runs[1], runs[2], runs[3], runs[4] });
        (await host.ReadAsync(context => context.Candidates.CountAsync())).Should().Be(8);
        (await host.ReadAsync(context => context.QueueItems.CountAsync())).Should().Be(2);
    }

    [Fact]
    public async Task Expired_blocklist_rows_go_while_permanent_and_future_ones_stay()
    {
        await using var host = await HousekeepingHost.CreateAsync();

        await host.SeedAsync(context =>
        {
            context.Blocklist.AddRange(
                Block("expired", host.Now.AddMinutes(-1)),
                Block("permanent", null),
                Block("future", host.Now.AddDays(3)));
        });

        var message = await host.Handler.ExecuteAsync(NoContext, CancellationToken.None);

        message.Should().Contain("blocklist 1,");
        (await host.ReadAsync(context => context.Blocklist.Select(item => item.BlocklistKey).ToListAsync()))
            .Should().BeEquivalentTo("permanent", "future");
    }

    [Fact]
    public async Task The_recycle_bin_is_cleaned_once_and_the_message_summarises_every_step()
    {
        await using var host = await HousekeepingHost.CreateAsync();
        host.RecycleBin.CleanupAsync(Arg.Any<CancellationToken>()).Returns(5);

        var message = await host.Handler.ExecuteAsync(NoContext, CancellationToken.None);

        await host.RecycleBin.Received(1).CleanupAsync(Arg.Any<CancellationToken>());
        message.Should().Be("commands 0, search runs 0 (candidates 0), blocklist 0, recycle bin 5 files, checkpointed");
    }

    [Fact]
    public async Task A_failing_step_fails_the_command_naming_it_after_the_other_steps_ran()
    {
        await using var host = await HousekeepingHost.CreateAsync();
        host.RecycleBin.CleanupAsync(Arg.Any<CancellationToken>()).Returns<Task<int>>(_ => throw new IOException("bin is locked"));
        await host.SeedAsync(context =>
            context.Blocklist.Add(Block("expired", host.Now.AddDays(-1))));

        var act = () => host.Handler.ExecuteAsync(NoContext, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("recycle bin: bin is locked").And.Contain("blocklist 1");
        (await host.ReadAsync(context => context.Blocklist.CountAsync())).Should().Be(0, "the steps after the failure still ran");
    }

    [Theory]
    [InlineData(10_000, 999, false)]
    [InlineData(10_000, 1_000, false)]
    [InlineData(10_000, 2_500, false)]
    [InlineData(10_000, 2_501, true)]
    [InlineData(1_000, 900, false)]
    [InlineData(4_000, 1_001, true)]
    public void A_vacuum_needs_more_than_a_quarter_of_the_file_and_more_than_a_thousand_pages_free(
        long pages, long free, bool expected) =>
        HousekeepingCommandHandler.ShouldVacuum(pages, free).Should().Be(expected);

    private static CommandRecord Command(string name, CommandStatus status, DateTime? endedAt, DateTime? queuedAt = null) =>
        new()
        {
            Name = name,
            Status = status,
            QueuedAt = queuedAt ?? endedAt!.Value.AddMinutes(-1),
            EndedAt = endedAt,
        };

    private static CandidateRecord Candidate(SearchRun run, long songId, string key) =>
        new()
        {
            SearchRun = run,
            SongId = songId,
            SourceType = "soulseek",
            BlocklistKey = key,
            DisplayName = key,
            RemotePath = key,
        };

    private static QueueItem QueueItem(long songId, long candidateId, long runId) =>
        new()
        {
            SongId = songId,
            CandidateId = candidateId,
            SearchRunId = runId,
            SourceType = "soulseek",
            Destination = $"wondarr/{candidateId}",
            State = QueueItemState.Imported,
        };

    private static BlocklistItem Block(string key, DateTime? expiresAt) =>
        new() { SourceType = "soulseek", BlocklistKey = key, Reason = "test", ExpiresAt = expiresAt };

    private sealed class HousekeepingHost : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _directory;

        private HousekeepingHost(ServiceProvider provider, string directory, FakeTimeProvider time, IRecycleBin bin)
        {
            _provider = provider;
            _directory = directory;
            RecycleBin = bin;
            Handler = new HousekeepingCommandHandler(
                provider.GetRequiredService<IServiceScopeFactory>(),
                time,
                NullLogger<HousekeepingCommandHandler>.Instance);
            Now = time.GetUtcNow().UtcDateTime;
        }

        public HousekeepingCommandHandler Handler { get; }

        public IRecycleBin RecycleBin { get; }

        public DateTime Now { get; }

        public static async Task<HousekeepingHost> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "wondarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
            var bin = Substitute.For<IRecycleBin>();
            bin.CleanupAsync(Arg.Any<CancellationToken>()).Returns(0);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton(bin);
            services.AddWondarrPersistence($"Data Source={Path.Combine(directory, "wondarr.db")}");

            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken.None);
            }

            return new HousekeepingHost(provider, directory, time, bin);
        }

        public async Task SeedAsync(Action<WondarrDbContext> seed)
        {
            await using var scope = _provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
            seed(context);
            await context.SaveChangesAsync();
        }

        public async Task<T> ReadAsync<T>(Func<WondarrDbContext, Task<T>> read)
        {
            await using var scope = _provider.CreateAsyncScope();

            return await read(scope.ServiceProvider.GetRequiredService<WondarrDbContext>());
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(
                new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_directory, "wondarr.db")}"));

            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
