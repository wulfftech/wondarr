using System.Text.Json;
using Wondarr.Core.Compaction;
using Wondarr.Core.Domain;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Compaction;

/// <summary>
/// What a restart does about a compaction that was cut short (code review R-004): the unfinished rows
/// queue a Compact run for their library, and the health check names the library until they are done.
/// </summary>
public sealed class CompactStartupTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ICommandQueue _commands = Substitute.For<ICommandQueue>();

    [Fact]
    public async Task Startup_queues_a_compact_run_for_a_library_with_unfinished_rows()
    {
        await using var context = await ContextAsync();
        await AddRowAsync(context, CompactMoveState.Staged, stagedPath: "/music/.wondarr-compact/1/a.flac");
        await AddRowAsync(context, CompactMoveState.Placed);

        await Service().StartAsync(CancellationToken.None);

        var call = _commands.ReceivedCalls().Single();
        var arguments = call.GetArguments();

        arguments[0].Should().Be("CompactLibrary");
        arguments[2].Should().Be(CommandTrigger.Scheduled);

        using var body = JsonDocument.Parse((string)arguments[1]!);
        body.RootElement.GetProperty("libraryId").GetInt64().Should().Be(SeedData.DefaultLibraryId);
        body.RootElement.GetProperty("name").GetString().Should().Be("CompactLibrary");

        // A restart may only finish or give up what was left; it never applies a fresh plan.
        body.RootElement.GetProperty("resumeOnly").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_row_still_holding_a_staged_file_counts_as_unfinished()
    {
        await using var context = await ContextAsync();
        await AddRowAsync(context, CompactMoveState.Failed, stagedPath: "/music/.wondarr-compact/1/a.flac");

        await Service().StartAsync(CancellationToken.None);

        _commands.ReceivedCalls().Should().HaveCount(1);
    }

    [Fact]
    public async Task Startup_queues_nothing_when_every_row_is_finished()
    {
        await using var context = await ContextAsync();
        await AddRowAsync(context, CompactMoveState.Placed);
        await AddRowAsync(context, CompactMoveState.Failed);

        await Service().StartAsync(CancellationToken.None);

        _commands.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_queue_that_throws_does_not_stop_startup()
    {
        await using var context = await ContextAsync();
        await AddRowAsync(context, CompactMoveState.Staged);

        _commands
            .EnqueueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CommandTrigger>(), Arg.Any<CancellationToken>())
            .Returns<Task<CommandRecord>>(_ => throw new InvalidOperationException("the queue is down"));

        var start = async () => await Service().StartAsync(CancellationToken.None);

        await start.Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_health_check_warns_about_each_library_with_unfinished_rows_and_clears_when_they_finish()
    {
        await using var context = await ContextAsync();
        await AddRowAsync(context, CompactMoveState.Staged, stagedPath: "/music/.wondarr-compact/1/a.flac");
        await AddRowAsync(context, CompactMoveState.Planned);

        var warning = await new CompactMoveHealthCheck(context).CheckAsync(CancellationToken.None);

        warning.Type.Should().Be(HealthCheckResult.Warning);
        warning.Message.Should().Contain("Music").And.Contain("2 files").And.Contain(".wondarr-compact");

        await context.CompactMoves.ExecuteUpdateAsync(update => update
            .SetProperty(row => row.State, CompactMoveState.Placed)
            .SetProperty(row => row.StagedPath, (string?)null));

        var cleared = await new CompactMoveHealthCheck(context).CheckAsync(CancellationToken.None);

        cleared.Type.Should().Be(HealthCheckResult.Ok);
    }

    [Fact]
    public async Task The_health_check_warns_about_a_given_up_file_that_still_sits_in_staging()
    {
        await using var context = await ContextAsync();
        context.CompactMoves.Add(new CompactMoveRecord
        {
            LibraryId = SeedData.DefaultLibraryId,
            SongId = 1,
            FromPath = "/music/a.flac",
            FinalPath = "/music/.wondarr-compact/1/a.flac",
            State = CompactMoveState.GivenUp,
        });
        context.CompactMoves.Add(new CompactMoveRecord
        {
            LibraryId = SeedData.DefaultLibraryId,
            SongId = 2,
            FromPath = "/music/b.flac",
            FinalPath = "/music/b.flac",
            State = CompactMoveState.GivenUp,
        });
        await context.SaveChangesAsync();

        var warning = await new CompactMoveHealthCheck(context).CheckAsync(CancellationToken.None);

        warning.Type.Should().Be(HealthCheckResult.Warning);
        warning.Message.Should().Contain("Music (1 file)").And.Contain("given up");
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_time);

        return _database.CreateContext(_time);
    }

    private CompactResumeHostedService Service()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _database.CreateContext(_time));
        services.AddSingleton(_commands);

        return new CompactResumeHostedService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CompactResumeHostedService>.Instance);
    }

    private static async Task AddRowAsync(WondarrDbContext context, CompactMoveState state, string? stagedPath = null)
    {
        context.CompactMoves.Add(new CompactMoveRecord
        {
            LibraryId = SeedData.DefaultLibraryId,
            SongId = 1,
            FromPath = "/music/a.flac",
            StagedPath = stagedPath,
            State = state,
        });

        await context.SaveChangesAsync();
    }
}
