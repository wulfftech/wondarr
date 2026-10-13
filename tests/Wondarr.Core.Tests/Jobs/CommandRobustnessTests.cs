using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.Jobs;

/// <summary>
/// A failed status write never strands a command (R-033), a cancel and a start cannot both win
/// (R-069), and names are deduped by their canonical spelling (R-068); a batch search that names its
/// songs is not swallowed by one that does not (R-034).
/// </summary>
public class CommandRobustnessTests
{
    [Fact]
    public async Task A_start_that_cannot_be_written_fails_the_command_and_the_next_tick_runs_it()
    {
        var interceptor = new StartWriteInterceptor { FailStarts = true };
        await using var host = await JobTestHost.CreateAsync(services => Intercept(services, interceptor));
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        var first = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Scheduled, CancellationToken.None);

        (await TestWait.UntilAsync(async () =>
        {
            AdvancePastRetries(host);

            return await StatusAsync(host, first.Id) == CommandStatus.Failed;
        })).Should().BeTrue("dispatch could not mark the command started, so it is failed, not left Queued");

        var failed = await host.Queue.GetAsync(first.Id, CancellationToken.None);
        failed!.Exception.Should().Contain("database is locked");
        failed.EndedAt.Should().NotBeNull();

        // The next scheduled tick enqueues it again: the failed row does not swallow it.
        interceptor.FailStarts = false;
        var second = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Scheduled, CancellationToken.None);

        second.Id.Should().NotBe(first.Id);
        (await TestWait.UntilAsync(async () => await StatusAsync(host, second.Id) == CommandStatus.Completed))
            .Should().BeTrue("the next tick runs the command");

        executor.ExecuteTask!.IsFaulted.Should().BeFalse();
        using var stopBudget = new CancellationTokenSource(TestWait.Timeout);
        await executor.StopAsync(stopBudget.Token);
    }

    [Fact]
    public async Task A_status_write_that_fails_once_is_retried_and_the_command_runs()
    {
        var interceptor = new StartWriteInterceptor { FailNextStarts = 1 };
        await using var host = await JobTestHost.CreateAsync(services => Intercept(services, interceptor));
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        var queued = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Scheduled, CancellationToken.None);

        (await TestWait.UntilAsync(async () =>
        {
            AdvancePastRetries(host);

            return await StatusAsync(host, queued.Id) == CommandStatus.Completed;
        })).Should().BeTrue("the second try of the write succeeds");

        interceptor.StartAttempts.Should().Be(2);

        using var stopBudget = new CancellationTokenSource(TestWait.Timeout);
        await executor.StopAsync(stopBudget.Token);
    }

    [Fact]
    public async Task A_stranded_row_does_not_swallow_an_enqueue()
    {
        await using var host = await JobTestHost.CreateAsync();

        // Rows nobody holds in memory: a failed status write left them Queued and Started.
        var queued = await host.InsertCommandAsync("Heartbeat", CommandStatus.Queued);
        var started = await host.InsertCommandAsync("Heartbeat", CommandStatus.Started);

        var fresh = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Scheduled, CancellationToken.None);

        fresh.Id.Should().NotBe(queued).And.NotBe(started);
        fresh.Status.Should().Be(CommandStatus.Queued);

        foreach (var id in new[] { queued, started })
        {
            var stale = await host.Queue.GetAsync(id, CancellationToken.None);

            stale!.Status.Should().Be(CommandStatus.Failed);
            stale.Result.Should().Be(CommandResult.Unsuccessful);
            stale.Exception.Should().Contain("Stranded");
            stale.EndedAt.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task A_row_the_process_holds_is_still_deduped()
    {
        await using var host = await JobTestHost.CreateAsync();
        var running = await host.InsertCommandAsync("Heartbeat", CommandStatus.Started, live: true);

        var again = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Manual, CancellationToken.None);

        again.Id.Should().Be(running);
        (await host.Queue.GetAsync(running, CancellationToken.None))!.Status.Should().Be(CommandStatus.Started);
    }

    [Fact]
    public async Task A_command_name_in_another_case_is_deduped_against_the_running_one()
    {
        var handler = new BlockingCommandHandler("Long");
        await using var host = await JobTestHost.CreateAsync(services => services.AddSingleton<ICommandHandler>(handler));
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        var running = await host.Queue.EnqueueAsync("Long", null, CommandTrigger.Manual, CancellationToken.None);
        (await TestWait.UntilAsync(() => Task.FromResult(handler.StartedCount == 1))).Should().BeTrue();

        var lower = await host.Queue.EnqueueAsync("long", null, CommandTrigger.Manual, CancellationToken.None);
        var upper = await host.Queue.EnqueueAsync("LONG", "{\"name\":\"Long\"}", CommandTrigger.Manual, CancellationToken.None);

        lower.Id.Should().Be(running.Id);
        upper.Id.Should().Be(running.Id);
        handler.StartedCount.Should().Be(1);

        handler.Release();
        (await TestWait.UntilAsync(async () => await StatusAsync(host, running.Id) == CommandStatus.Completed)).Should().BeTrue();
        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_command_queued_in_another_case_is_stored_under_the_handlers_name()
    {
        await using var host = await JobTestHost.CreateAsync();

        var queued = await host.Queue.EnqueueAsync("missingsearch", null, CommandTrigger.Manual, CancellationToken.None);

        queued.Name.Should().Be("MissingSearch");
    }

    [Fact]
    public async Task A_batch_search_that_names_its_songs_is_not_swallowed_by_one_that_does_not()
    {
        await using var host = await JobTestHost.CreateAsync();
        var running = await host.InsertCommandAsync("MissingSearch", CommandStatus.Started, live: true);

        var album = await host.Queue.EnqueueAsync(
            "MissingSearch",
            "{\"name\":\"MissingSearch\",\"songIds\":[7,8,9]}",
            CommandTrigger.Unspecified,
            CancellationToken.None);
        var sameAlbum = await host.Queue.EnqueueAsync(
            "MissingSearch",
            "{\"songIds\":[7,8,9],\"name\":\"MissingSearch\"}",
            CommandTrigger.Unspecified,
            CancellationToken.None);
        var otherAlbum = await host.Queue.EnqueueAsync(
            "MissingSearch",
            "{\"name\":\"MissingSearch\",\"songIds\":[10]}",
            CommandTrigger.Unspecified,
            CancellationToken.None);

        album.Id.Should().NotBe(running, "the running one chose its batch before these songs existed");
        sameAlbum.Id.Should().Be(album.Id, "an identical body is the same request");
        otherAlbum.Id.Should().Be(album.Id, "a request to search more songs is folded into the one still waiting to start");
        (await host.Queue.GetAsync(album.Id, CancellationToken.None))!.Body.Should().Contain("7,8,9,10");
    }

    [Fact]
    public async Task A_cancel_that_lands_while_the_executor_starts_the_command_wins_and_the_command_never_runs()
    {
        var handler = new BlockingCommandHandler("Long");
        var interceptor = new StartWriteInterceptor();
        await using var host = await JobTestHost.CreateAsync(services =>
        {
            services.AddSingleton<ICommandHandler>(handler);
            Intercept(services, interceptor);
        });
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        // At the moment the executor is about to write "started", the user's cancel gets in first.
        var cancelResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        interceptor.BeforeStart = async () =>
        {
            var newest = (await host.Queue.ListAsync(1)).Single();
            cancelResult.SetResult(await host.Queue.CancelAsync(newest.Id, CancellationToken.None));
        };

        var queued = await host.Queue.EnqueueAsync("Long", null, CommandTrigger.Manual, CancellationToken.None);

        // Signals, not polling: the cancel has answered, then the executor's start update has run (and
        // changed no row). The generous cap only guards against a hang.
        using var cap = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        (await cancelResult.Task.WaitAsync(cap.Token)).Should().BeTrue();
        await interceptor.StartUpdateExecuted.WaitAsync(cap.Token);

        // Stopping waits for the worker to be done with the command, whichever way it went.
        await executor.StopAsync(cap.Token);

        handler.StartedCount.Should().Be(0, "a cancelled command must not run");
        (await StatusAsync(host, queued.Id)).Should().Be(CommandStatus.Cancelled);
    }

    [Fact]
    public async Task A_start_that_lands_first_makes_the_cancel_report_a_conflict()
    {
        var handler = new BlockingCommandHandler("Long");
        await using var host = await JobTestHost.CreateAsync(services => services.AddSingleton<ICommandHandler>(handler));
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        var queued = await host.Queue.EnqueueAsync("Long", null, CommandTrigger.Manual, CancellationToken.None);
        (await TestWait.UntilAsync(() => Task.FromResult(handler.StartedCount == 1))).Should().BeTrue();

        (await host.Queue.CancelAsync(queued.Id, CancellationToken.None)).Should().BeFalse();
        (await StatusAsync(host, queued.Id)).Should().Be(CommandStatus.Started);

        handler.Release();
        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_bookkeeping_fault_after_the_start_fails_the_command_instead_of_leaving_it_started()
    {
        await using var host = await JobTestHost.CreateAsync();
        var aggregator = new ThrowOnceAggregator(host.EventAggregator);
        var executor = host.CreateExecutor(aggregator);
        await executor.StartAsync(CancellationToken.None);

        // The first publish from the executor (the "started" one) throws, as a failed write would.
        var queued = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Manual, CancellationToken.None);

        (await TestWait.UntilAsync(async () =>
        {
            AdvancePastRetries(host);

            return await StatusAsync(host, queued.Id) == CommandStatus.Failed;
        })).Should().BeTrue("a command the executor lost track of is failed, not left Started");

        var again = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Manual, CancellationToken.None);
        again.Id.Should().NotBe(queued.Id);

        using var stopBudget = new CancellationTokenSource(TestWait.Timeout);
        await executor.StopAsync(stopBudget.Token);
    }

    [Fact]
    public async Task A_command_that_finishes_while_an_enqueue_reads_the_rows_is_not_overwritten_as_stranded()
    {
        var interceptor = new StartWriteInterceptor();
        await using var host = await JobTestHost.CreateAsync(services => Intercept(services, interceptor));
        var running = await host.InsertCommandAsync("Heartbeat", CommandStatus.Started, live: true);

        // The enqueue has read X as Started; then X finishes (its row is Completed, the executor lets go).
        interceptor.AfterCommandRead = async () =>
        {
            await host.UseDatabaseAsync(async context =>
            {
                var row = await context.Commands.SingleAsync(command => command.Id == running);
                row.Status = CommandStatus.Completed;
                row.Result = CommandResult.Successful;
                row.EndedAt = host.TimeProvider.GetUtcNow().UtcDateTime;
            });
            host.Active.Remove(running);
        };

        var again = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Scheduled, CancellationToken.None);

        var finished = await host.Queue.GetAsync(running, CancellationToken.None);
        finished!.Status.Should().Be(CommandStatus.Completed, "it was held when the enqueue looked, so it is not stranded");
        finished.Exception.Should().BeNull();
        again.Id.Should().Be(running, "the enqueue judged the command by what it saw: still running");
    }

    [Fact]
    public async Task A_stranded_row_that_finished_after_it_was_read_is_left_alone_by_the_conditional_update()
    {
        var interceptor = new StartWriteInterceptor();
        await using var host = await JobTestHost.CreateAsync(services => Intercept(services, interceptor));
        var stale = await host.InsertCommandAsync("Heartbeat", CommandStatus.Started);

        interceptor.AfterCommandRead = async () =>
            await host.UseDatabaseAsync(async context =>
            {
                var row = await context.Commands.SingleAsync(command => command.Id == stale);
                row.Status = CommandStatus.Completed;
            });

        await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Scheduled, CancellationToken.None);

        (await StatusAsync(host, stale)).Should().Be(CommandStatus.Completed, "only a Queued or Started row is failed");
    }

    [Fact]
    public async Task A_start_whose_answer_was_lost_after_it_committed_still_runs_the_command()
    {
        var handler = new BlockingCommandHandler("Long");
        var interceptor = new StartWriteInterceptor { ThrowAfterNextStart = true };
        await using var host = await JobTestHost.CreateAsync(services =>
        {
            services.AddSingleton<ICommandHandler>(handler);
            Intercept(services, interceptor);
        });
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        // The update commits, then the call throws; the retry updates no row but finds its own start.
        var queued = await host.Queue.EnqueueAsync("Long", null, CommandTrigger.Manual, CancellationToken.None);

        (await TestWait.UntilAsync(() =>
        {
            AdvancePastRetries(host);

            return Task.FromResult(handler.StartedCount == 1);
        })).Should().BeTrue("the command was started, so it runs");

        handler.Release();
        (await TestWait.UntilAsync(async () => await StatusAsync(host, queued.Id) == CommandStatus.Completed)).Should().BeTrue();
        interceptor.StartAttempts.Should().Be(2);

        using var stopBudget = new CancellationTokenSource(TestWait.Timeout);
        await executor.StopAsync(stopBudget.Token);
    }

    [Fact]
    public async Task A_command_a_scheduler_queued_before_the_executor_started_is_not_orphaned()
    {
        await using var host = await JobTestHost.CreateAsync();

        // An overdue trigger fires as the host comes up and queues the command before the executor starts.
        var early = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Scheduled, CancellationToken.None);
        var leftOver = await host.InsertCommandAsync("CheckHealth", CommandStatus.Started);

        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        (await TestWait.UntilAsync(async () => await StatusAsync(host, early.Id) == CommandStatus.Completed))
            .Should().BeTrue("it ran instead of being marked Orphaned");
        (await StatusAsync(host, leftOver)).Should().Be(CommandStatus.Orphaned, "a row nobody holds is a previous process's");

        using var stopBudget = new CancellationTokenSource(TestWait.Timeout);
        await executor.StopAsync(stopBudget.Token);
    }

    [Fact]
    public async Task A_batch_search_is_merged_into_one_that_has_not_started_yet()
    {
        await using var host = await JobTestHost.CreateAsync();

        var first = await host.Queue.EnqueueAsync(
            "MissingSearch",
            "{\"name\":\"MissingSearch\",\"songIds\":[9,7]}",
            CommandTrigger.Unspecified,
            CancellationToken.None);
        var second = await host.Queue.EnqueueAsync(
            "MissingSearch",
            "{\"name\":\"MissingSearch\",\"songIds\":[8,7,10]}",
            CommandTrigger.Unspecified,
            CancellationToken.None);

        second.Id.Should().Be(first.Id, "one queued command, not two");
        var stored = await host.Queue.GetAsync(first.Id, CancellationToken.None);
        using var document = System.Text.Json.JsonDocument.Parse(stored!.Body!);
        document.RootElement.GetProperty("songIds").EnumerateArray().Select(id => id.GetInt64())
            .Should().Equal(7, 8, 9, 10);
        (await host.Queue.ListAsync(10)).Should().ContainSingle(command => command.Name == "MissingSearch");
    }

    [Fact]
    public async Task Two_move_requests_to_different_libraries_stay_two_commands()
    {
        await using var host = await JobTestHost.CreateAsync();

        var first = await host.Queue.EnqueueAsync(
            "MoveSongs",
            "{\"name\":\"MoveSongs\",\"songIds\":[1,2],\"libraryId\":5}",
            CommandTrigger.Manual,
            CancellationToken.None);
        var second = await host.Queue.EnqueueAsync(
            "MoveSongs",
            "{\"name\":\"MoveSongs\",\"songIds\":[3],\"libraryId\":6}",
            CommandTrigger.Manual,
            CancellationToken.None);

        second.Id.Should().NotBe(first.Id);
        (await host.Queue.GetAsync(first.Id, CancellationToken.None))!.Body.Should().Contain("\"libraryId\":5").And.NotContain("3");
        second.Body.Should().Contain("\"libraryId\":6");
    }

    [Fact]
    public async Task Two_conversions_with_different_rules_stay_two_commands()
    {
        await using var host = await JobTestHost.CreateAsync();

        var first = await host.Queue.EnqueueAsync(
            "ConvertFiles",
            "{\"name\":\"ConvertFiles\",\"songIds\":[1],\"libraryId\":5,\"rule\":\"lossless\"}",
            CommandTrigger.Manual,
            CancellationToken.None);
        var second = await host.Queue.EnqueueAsync(
            "ConvertFiles",
            "{\"name\":\"ConvertFiles\",\"songIds\":[2],\"libraryId\":5,\"rule\":\"lossy\"}",
            CommandTrigger.Manual,
            CancellationToken.None);

        second.Id.Should().NotBe(first.Id);
        (await host.Queue.GetAsync(first.Id, CancellationToken.None))!.Body.Should().Contain("lossless").And.NotContain("lossy");
    }

    [Fact]
    public async Task A_merge_keeps_every_other_member_of_the_queued_body()
    {
        await using var host = await JobTestHost.CreateAsync();

        var first = await host.Queue.EnqueueAsync(
            "MissingSearch",
            "{\"name\":\"MissingSearch\",\"songIds\":[2],\"note\":\"kept\"}",
            CommandTrigger.Unspecified,
            CancellationToken.None);
        await host.Queue.EnqueueAsync(
            "MissingSearch",
            "{\"name\":\"MissingSearch\",\"songIds\":[1]}",
            CommandTrigger.Unspecified,
            CancellationToken.None);

        var stored = await host.Queue.GetAsync(first.Id, CancellationToken.None);
        stored!.Body.Should().Contain("\"note\":\"kept\"").And.Contain("\"songIds\":[1,2]");
    }

    [Fact]
    public async Task A_batch_search_is_not_merged_into_one_that_has_started()
    {
        await using var host = await JobTestHost.CreateAsync();
        var running = await host.InsertCommandAsync(
            "MissingSearch",
            CommandStatus.Started,
            "{\"name\":\"MissingSearch\",\"songIds\":[1]}",
            live: true);

        var next = await host.Queue.EnqueueAsync(
            "MissingSearch",
            "{\"name\":\"MissingSearch\",\"songIds\":[2]}",
            CommandTrigger.Unspecified,
            CancellationToken.None);

        next.Id.Should().NotBe(running);
        (await host.Queue.GetAsync(running, CancellationToken.None))!.Body.Should().Contain("[1]");
    }

    private static void AdvancePastRetries(JobTestHost host) =>
        host.TimeProvider.Advance(CommandExecutor.WorkerFaultPause + CommandExecutor.StatusWriteRetryPause);

    private static void Intercept(IServiceCollection services, StartWriteInterceptor interceptor) =>
        services.AddDbContext<WondarrDbContext>(options => options.AddInterceptors(interceptor));

    private static async Task<CommandStatus?> StatusAsync(JobTestHost host, long id) =>
        (await host.Queue.GetAsync(id, CancellationToken.None))?.Status;

    /// <summary>
    /// Sits in front of the executor's "mark started" update (the only statement that sets
    /// <c>started_at</c> without <c>ended_at</c>): it can run a hook first, and it can fail the write.
    /// </summary>
    private sealed class StartWriteInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _startUpdateExecuted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;
        private int _throwAfter;
        private int _failLeft;
        private Func<Task>? _afterCommandRead;
        private Func<Task>? _beforeStart;

        /// <summary>Gets a task that completes once a start update has run (committed or not).</summary>
        public Task StartUpdateExecuted => _startUpdateExecuted.Task;

        public bool FailStarts { get; set; }

        /// <summary>Gets or sets a value indicating whether the next start update commits and then throws.</summary>
        public bool ThrowAfterNextStart
        {
            get => Volatile.Read(ref _throwAfter) == 1;
            set => Volatile.Write(ref _throwAfter, value ? 1 : 0);
        }

        /// <summary>Sets a hook that runs once, right after the next read of the command table.</summary>
        public Func<Task>? AfterCommandRead
        {
            set => _afterCommandRead = value;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("FROM \"command\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _afterCommandRead, null) is { } hook)
            {
                await hook();
            }

            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (IsStartUpdate(command.CommandText))
            {
                _startUpdateExecuted.TrySetResult();

                if (Interlocked.CompareExchange(ref _throwAfter, 0, 1) == 1)
                {
                    throw new InvalidOperationException("the answer was lost");
                }
            }

            return ValueTask.FromResult(result);
        }

        private static bool IsStartUpdate(string sql) =>
            sql.StartsWith("UPDATE", StringComparison.Ordinal)
            && sql.Contains("\"started_at\"", StringComparison.Ordinal)
            && !sql.Contains("\"ended_at\"", StringComparison.Ordinal);

        public int FailNextStarts
        {
            set => Volatile.Write(ref _failLeft, value);
        }

        public int StartAttempts => Volatile.Read(ref _attempts);

        public Func<Task>? BeforeStart
        {
            set => _beforeStart = value;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var sql = command.CommandText;

            if (sql.StartsWith("UPDATE", StringComparison.Ordinal)
                && sql.Contains("\"started_at\"", StringComparison.Ordinal)
                && !sql.Contains("\"ended_at\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _attempts);

                if (Interlocked.Exchange(ref _beforeStart, null) is { } hook)
                {
                    await hook();
                }

                if (FailStarts || Interlocked.Decrement(ref _failLeft) >= 0)
                {
                    throw new InvalidOperationException("database is locked");
                }
            }

            return result;
        }

    }

    private sealed class ThrowOnceAggregator(Wondarr.Core.Messaging.IEventAggregator inner) : Wondarr.Core.Messaging.IEventAggregator
    {
        private int _thrown;

        public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
            where TEvent : Wondarr.Core.Messaging.IEvent
        {
            if (Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new InvalidOperationException("database is locked");
            }

            return inner.PublishAsync(message, cancellationToken);
        }
    }
}
