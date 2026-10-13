using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;
using Wondarr.Core.References;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.References;

/// <summary>A scan that waits for another scan of the same library holds no executor place meanwhile (R-075).</summary>
public sealed class ReferenceLibraryScanYieldTests
{
    [Fact]
    public async Task A_scan_waiting_on_the_library_gate_hands_its_place_back_and_takes_one_before_it_scans()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        using var database = new SqliteTestDatabase();
        await database.MigrateAsync(time);

        long libraryId;

        await using (var context = database.CreateContext(time))
        {
            var library = new ReferenceLibrary { Id = 90_101, Name = "Music", RootPath = "/reference/music" };
            context.ReferenceLibraries.Add(library);
            await context.SaveChangesAsync();
            libraryId = library.Id;
        }

        var scanner = Substitute.For<IReferenceScanner>();
        scanner
            .ScanAsync(libraryId, Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(new ReferenceScanResult(0, 0, 0, 0, 0, 0));
        var identifier = Substitute.For<IReferenceIdentifier>();
        identifier
            .IdentifyPendingAsync(libraryId, Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(new ReferenceIdentifyResult(0, 0, 0, 0));

        var services = new ServiceCollection();
        services.AddDbContext<WondarrDbContext>(options => options
            .UseSqlite($"Data Source={database.FilePath}")
            .UseSnakeCaseNamingConvention());
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(scanner);
        services.AddSingleton(identifier);

        await using var provider = services.BuildServiceProvider();
        var handler = new ReferenceLibraryScanCommandHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ReferenceLibraryScanCommandHandler>.Instance);

        var yielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var takingBack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var placeBack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context2 = new CommandContext(
            1,
            $"{{\"name\":\"ReferenceLibraryScan\",\"referenceLibraryId\":{libraryId}}}",
            CommandTrigger.Manual,
            _ => Task.CompletedTask,
            () =>
            {
                yielded.TrySetResult();

                return new Lease(takingBack, placeBack.Task);
            });

        // Another scan of the same library holds the gate.
        var gate = ReferenceLibraryGates.For(libraryId);
        await gate.WaitAsync();

        var running = handler.ExecuteAsync(context2, CancellationToken.None);

        await yielded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        running.IsCompleted.Should().BeFalse();
        await scanner.DidNotReceiveWithAnyArgs().ScanAsync(default, default, default);

        // The other scan finishes: this one asks for its place back, and scans only once it has it.
        gate.Release();
        await takingBack.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await scanner.DidNotReceiveWithAnyArgs().ScanAsync(default, default, default);

        placeBack.SetResult();
        var message = await running.WaitAsync(TimeSpan.FromSeconds(10));

        message.Should().StartWith("1 library:");
        await scanner.Received(1).ScanAsync(libraryId, Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>());
        gate.CurrentCount.Should().Be(1, "the gate is free again");
    }

    [Fact]
    public async Task A_scan_that_finds_the_gate_free_does_not_give_up_its_place()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        using var database = new SqliteTestDatabase();
        await database.MigrateAsync(time);

        long libraryId;

        await using (var context = database.CreateContext(time))
        {
            var library = new ReferenceLibrary { Id = 90_102, Name = "Music", RootPath = "/reference/music" };
            context.ReferenceLibraries.Add(library);
            await context.SaveChangesAsync();
            libraryId = library.Id;
        }

        var scanner = Substitute.For<IReferenceScanner>();
        scanner
            .ScanAsync(libraryId, Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(new ReferenceScanResult(0, 0, 0, 0, 0, 0));
        var identifier = Substitute.For<IReferenceIdentifier>();
        identifier
            .IdentifyPendingAsync(libraryId, Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(new ReferenceIdentifyResult(0, 0, 0, 0));

        var services = new ServiceCollection();
        services.AddDbContext<WondarrDbContext>(options => options
            .UseSqlite($"Data Source={database.FilePath}")
            .UseSnakeCaseNamingConvention());
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(scanner);
        services.AddSingleton(identifier);

        await using var provider = services.BuildServiceProvider();
        var handler = new ReferenceLibraryScanCommandHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ReferenceLibraryScanCommandHandler>.Instance);

        var yields = 0;
        var context2 = new CommandContext(
            1,
            $"{{\"referenceLibraryId\":{libraryId}}}",
            CommandTrigger.Scheduled,
            _ => Task.CompletedTask,
            () =>
            {
                Interlocked.Increment(ref yields);

                return null;
            });

        await handler.ExecuteAsync(context2, CancellationToken.None);

        yields.Should().Be(0);
        await scanner.Received(1).ScanAsync(libraryId, Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>());
    }

    private sealed class Lease(TaskCompletionSource taking, Task release) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            taking.TrySetResult();
            await release;
        }
    }
}
