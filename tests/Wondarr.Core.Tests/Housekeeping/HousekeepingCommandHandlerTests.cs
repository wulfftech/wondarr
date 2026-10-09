using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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
                Command("Alpha", CommandStatus.Failed, endedAt: now.AddDays(-30)),
                Command("Alpha", CommandStatus.Orphaned, endedAt: now.AddDays(-10)),
                Command("Alpha", CommandStatus.Completed, endedAt: now.AddDays(-8)),
                Command("Beta", CommandStatus.Aborted, endedAt: now.AddDays(-10)),
                Command("Beta", CommandStatus.Cancelled, endedAt: null, queuedAt: now.AddDays(-9)),
                Command("Beta", CommandStatus.Completed, endedAt: now.AddDays(-6)),
                Command("Gamma", CommandStatus.Queued, endedAt: null, queuedAt: now.AddDays(-40)),
                Command("Delta", CommandStatus.Started, endedAt: null, queuedAt: now.AddDays(-40)));
        });

        var message = await host.Handler.ExecuteAsync(NoContext, CancellationToken.None);

        message.Should().StartWith("commands 4,");
        (await host.ReadAsync(context => context.Commands.Select(command => command.Name + ":" + command.Status).ToListAsync()))
            .Should().BeEquivalentTo("Alpha:Completed", "Beta:Completed", "Gamma:Queued", "Delta:Started");
    }

    [Fact]
    public async Task The_newest_finished_command_of_each_name_is_kept_however_old_so_the_last_run_still_shows()
    {
        await using var host = await HousekeepingHost.CreateAsync();
        var now = host.Now;

        await host.SeedAsync(context =>
            context.Commands.AddRange(
                TimedCommand("Backup", now.AddDays(-300)),
                TimedCommand("Backup", now.AddDays(-200))));

        await host.Handler.ExecuteAsync(NoContext, CancellationToken.None);

        var left = await host.ReadAsync(context => context.Commands.ToListAsync());
        left.Should().ContainSingle().Which.EndedAt.Should().Be(now.AddDays(-200));
    }

    [Fact]
    public async Task An_old_search_run_goes_with_its_candidates_but_runs_and_candidates_in_use_stay()
    {
        await using var host = await HousekeepingHost.CreateAsync();
        var old = host.Now.AddDays(-31);
        var songs = await host.AddSongsAsync(5);

        // 0: plain old run, with two candidates -> deleted
        // 1: old run with a queue item of its own -> kept
        // 2: old run whose candidate a queue item (of another, recent run) points at -> kept
        // 3: old run whose candidate a song file's source_ref names -> kept
        // 4: a recent run -> kept
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
        var runs = await host.ReadAsync(context => context.SearchRuns.OrderBy(run => run.Id).Select(run => run.Id).ToListAsync());
        var candidates = new Dictionary<int, long>();
        foreach (var candidate in await host.ReadAsync(context => context.Candidates.ToListAsync()))
        {
            candidates[(int)(candidate.SearchRunId - runs[0])] = candidate.Id;
        }

        await host.SeedAsync(context =>
        {
            context.QueueItems.Add(QueueItem(songs[1], candidates[1], runs[1]));
            context.QueueItems.Add(QueueItem(songs[2], candidates[2], runs[4]));

            // The run id in this source_ref names nothing real, so only the candidate protects run 3.
            context.SongFiles.Add(SongFile(songs[3], ImportSourceRef(candidates[3], runs[3] + 1000)));
        });

        var message = await host.Handler.ExecuteAsync(NoContext, CancellationToken.None);

        message.Should().Contain("search runs 1 (candidates 2)");
        (await host.ReadAsync(context => context.SearchRuns.Select(run => run.Id).ToListAsync()))
            .Should().BeEquivalentTo(new[] { runs[1], runs[2], runs[3], runs[4] });
        (await host.ReadAsync(context => context.Candidates.CountAsync())).Should().Be(8);
        (await host.ReadAsync(context => context.QueueItems.CountAsync())).Should().Be(2);
    }

    [Fact]
    public async Task A_run_named_only_by_a_source_ref_search_run_id_is_kept()
    {
        await using var host = await HousekeepingHost.CreateAsync();
        var songs = await host.AddSongsAsync(2);

        await host.SeedAsync(context => context.SearchRuns.Add(
            new SearchRun { SongId = songs[0], Trigger = SearchTrigger.Automatic, StartedAt = host.Now.AddDays(-40) }));
        var runId = (await host.ReadAsync(context => context.SearchRuns.SingleAsync())).Id;
        await host.SeedAsync(context => context.SongFiles.Add(SongFile(songs[1], ImportSourceRef(candidateId: 123_456, searchRunId: runId))));

        await host.Handler.ExecuteAsync(NoContext, CancellationToken.None);

        (await host.ReadAsync(context => context.SearchRuns.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task More_old_runs_than_one_batch_are_all_deleted_across_batches()
    {
        await using var host = await HousekeepingHost.CreateAsync();
        var songs = await host.AddSongsAsync(1);

        await host.SeedAsync(context =>
        {
            for (var index = 0; index < 600; index++)
            {
                var run = new SearchRun
                {
                    SongId = songs[0],
                    Trigger = SearchTrigger.Automatic,
                    StartedAt = host.Now.AddDays(-40),
                    Outcome = SearchOutcome.NoResults,
                };
                context.SearchRuns.Add(run);
                context.Candidates.Add(Candidate(run, songs[0], $"k{index}"));
            }
        });

        var message = await host.Handler.ExecuteAsync(NoContext, CancellationToken.None);

        message.Should().Contain("search runs 600 (candidates 600)");
        (await host.ReadAsync(context => context.SearchRuns.CountAsync())).Should().Be(0);
        (await host.ReadAsync(context => context.Candidates.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task A_malformed_source_ref_protects_nothing()
    {
        await using var host = await HousekeepingHost.CreateAsync();
        var songs = await host.AddSongsAsync(2);

        await host.SeedAsync(context =>
        {
            context.SearchRuns.Add(
                new SearchRun { SongId = songs[0], Trigger = SearchTrigger.Automatic, StartedAt = host.Now.AddDays(-40) });
            context.SongFiles.Add(SongFile(songs[1], "{not json"));
        });

        await host.Handler.ExecuteAsync(NoContext, CancellationToken.None);

        (await host.ReadAsync(context => context.SearchRuns.CountAsync())).Should().Be(0);
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

    [Fact]
    public async Task The_vacuum_runs_when_the_policy_asks_for_it_and_the_file_shrinks()
    {
        await using var host = await HousekeepingHost.CreateAsync(shouldVacuum: (pages, free) => free > 20 && free * 4 > pages);
        var now = host.Now;

        await host.SeedAsync(context =>
        {
            for (var index = 0; index < 150; index++)
            {
                var command = TimedCommand("Bulk" + index, now.AddDays(-20));
                command.Message = new string('x', 8_000);
                context.Commands.Add(command);
                context.Commands.Add(TimedCommand("Bulk" + index, now.AddDays(-1)));
            }
        });
        var before = await host.LogicalSizeAsync();

        var message = await host.Handler.ExecuteAsync(NoContext, CancellationToken.None);

        message.Should().EndWith("vacuumed");
        (await host.ReadAsync(context => context.Commands.CountAsync())).Should().Be(150);
        host.FileLength().Should().BeLessThan(before);
    }

    [Fact]
    public async Task The_vacuum_waits_while_another_command_is_running()
    {
        await using var host = await HousekeepingHost.CreateAsync(shouldVacuum: (_, _) => true);
        await host.SeedAsync(context => context.Commands.Add(new CommandRecord
        {
            Name = "Other",
            Status = CommandStatus.Started,
            QueuedAt = host.Now,
            StartedAt = host.Now,
        }));

        var message = await host.Handler.ExecuteAsync(NoContext, CancellationToken.None);

        message.Should().EndWith("checkpointed, vacuum skipped (commands running)");
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

    private static CommandRecord TimedCommand(string name, DateTime endedAt) =>
        new()
        {
            Name = name,
            Status = CommandStatus.Completed,
            QueuedAt = endedAt.AddMinutes(-2),
            StartedAt = endedAt.AddMinutes(-1),
            EndedAt = endedAt,
        };

    private static SongFile SongFile(long songId, string sourceRef) =>
        new()
        {
            SongId = songId,
            Path = $"/music/{songId}.flac",
            Codec = "flac",
            Container = "flac",
            QualityId = 36,
            SourceType = "soulseek",
            SourceRef = sourceRef,
            ImportedAt = DateTime.UnixEpoch,
        };

    /// <summary>Mirrors ImportService's private SourceReference record, which is what song_file.source_ref holds.</summary>
    private sealed record ImportedSourceReference(
        string? Provider,
        string RemotePath,
        long CandidateId,
        long QueueItemId,
        long SearchRunId);

    // The same options ImportService serialises with: web defaults (camelCase), nulls left out.
    private static readonly JsonSerializerOptions ImportJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string ImportSourceRef(long candidateId, long searchRunId) =>
        JsonSerializer.Serialize(
            new ImportedSourceReference(null, "remote/path.flac", candidateId, 99, searchRunId),
            ImportJson);

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

        private HousekeepingHost(
            ServiceProvider provider,
            string directory,
            FakeTimeProvider time,
            IRecycleBin bin,
            Func<long, long, bool>? shouldVacuum)
        {
            _provider = provider;
            _directory = directory;
            RecycleBin = bin;
            Handler = new HousekeepingCommandHandler(
                provider.GetRequiredService<IServiceScopeFactory>(),
                time,
                NullLogger<HousekeepingCommandHandler>.Instance,
                shouldVacuum ?? HousekeepingCommandHandler.ShouldVacuum);
            Now = time.GetUtcNow().UtcDateTime;
        }

        public HousekeepingCommandHandler Handler { get; }

        public IRecycleBin RecycleBin { get; }

        public DateTime Now { get; }

        public static async Task<HousekeepingHost> CreateAsync(Func<long, long, bool>? shouldVacuum = null)
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

            return new HousekeepingHost(provider, directory, time, bin, shouldVacuum);
        }

        public async Task<List<long>> AddSongsAsync(int count)
        {
            await SeedAsync(context =>
            {
                for (var index = 0; index < count; index++)
                {
                    var artist = new Artist { Name = $"Artist {index}", SortName = $"Artist {index}" };
                    context.Songs.Add(new Song
                    {
                        Title = $"Song {index}",
                        ArtistCredit = artist.Name,
                        PrimaryArtist = artist,
                        QualityProfileId = SeedData.StandardProfileId,
                        LibraryId = SeedData.DefaultLibraryId,
                        AddedBy = "test",
                    });
                }
            });

            return await ReadAsync(context => context.Songs.OrderBy(song => song.Id).Select(song => song.Id).ToListAsync());
        }

        public long FileLength() => new FileInfo(Path.Combine(_directory, "wondarr.db")).Length;

        /// <summary>What the database weighs logically (pages times page size), the WAL's pages included.</summary>
        public async Task<long> LogicalSizeAsync()
        {
            await using var scope = _provider.CreateAsyncScope();
            var connection = scope.ServiceProvider.GetRequiredService<WondarrDbContext>().Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT (SELECT page_count FROM pragma_page_count) * (SELECT page_size FROM pragma_page_size);";

            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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
