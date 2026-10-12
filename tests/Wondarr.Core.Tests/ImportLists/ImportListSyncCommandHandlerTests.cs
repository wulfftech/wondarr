using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.ImportLists;
using Wondarr.Core.Importing;
using Wondarr.Core.Jobs;
using Wondarr.Core.Notifications;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.ImportLists;

/// <summary>
/// R-020: "Sync now" and the scheduled run are different commands, and they never sync one list at
/// once. The tests drive the real handler, the real service and a real database; the signals are
/// completion sources, never sleeps.
/// </summary>
public sealed class ImportListSyncCommandHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly IIdentityResolver _resolver = Substitute.For<IIdentityResolver>();
    private readonly ISongService _songs = Substitute.For<ISongService>();
    private readonly IPlaylistWriter _playlists = Substitute.For<IPlaylistWriter>();
    private readonly ICommandQueue _commands = Substitute.For<ICommandQueue>();
    private readonly SongFileLock _fileLock = new();
    private readonly BlockingProvider _provider = new();

    [Fact]
    public async Task An_hourly_run_during_a_long_manual_sync_leaves_the_list_to_it()
    {
        await _database.MigrateAsync(_time);
        using var services = BuildServices();
        var listId = await CreateListAsync(services);
        var handler = new ImportListSyncCommandHandler(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ImportListSyncCommandHandler>.Instance);

        _provider.Entries = [new("a", "A", "One"), new("b", "B", "Two"), new("c", "C", "Three")];

        // The manual sync starts and sits in the provider's read.
        var manual = handler.ExecuteAsync(ManualContext(listId, yieldWorker: null), CancellationToken.None);
        await _provider.FirstFetchStarted.Task;

        // The hourly run finds the list due, and finds it being synced.
        var hourly = await handler.ExecuteAsync(
            new CommandContext(2, null, CommandTrigger.Scheduled, _ => Task.CompletedTask),
            CancellationToken.None);

        hourly.Should().Contain("1 already syncing");
        _provider.FetchCount.Should().Be(1, "the list was not read a second time");

        _provider.ReleaseFirstFetch.SetResult();
        await manual;

        await using var scope = services.CreateAsyncScope();
        var items = await scope.ServiceProvider.GetRequiredService<WondarrDbContext>().ImportListItems.AsNoTracking().ToListAsync();
        items.Select(item => item.ExternalId).Should().BeEquivalentTo(["a", "b", "c"]);
        await _playlists.Received(1).WriteAsync(listId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_second_manual_sync_waits_for_the_first_and_hands_its_executor_place_back_meanwhile()
    {
        await _database.MigrateAsync(_time);
        using var services = BuildServices();
        var listId = await CreateListAsync(services);
        var handler = new ImportListSyncCommandHandler(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ImportListSyncCommandHandler>.Instance);
        _provider.Entries = [new("a", "A", "One")];

        var first = handler.ExecuteAsync(ManualContext(listId, yieldWorker: null), CancellationToken.None);
        await _provider.FirstFetchStarted.Task;

        var yielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new Lease();
        var second = handler.ExecuteAsync(
            ManualContext(listId, () =>
            {
                yielded.TrySetResult();

                return lease;
            }),
            CancellationToken.None);

        // The second sync reached the gate, gave its place up and is waiting.
        await yielded.Task;
        second.IsCompleted.Should().BeFalse();
        lease.Disposed.Should().BeFalse("the place is only taken back once the gate is open");
        _provider.FetchCount.Should().Be(1);

        _provider.ReleaseFirstFetch.SetResult();
        await first;
        await second;

        lease.Disposed.Should().BeTrue();
        _provider.FetchCount.Should().Be(2, "the manual sync asked for a read and gets one, after the first");

        await using var scope = services.CreateAsyncScope();
        var items = await scope.ServiceProvider.GetRequiredService<WondarrDbContext>().ImportListItems.AsNoTracking().ToListAsync();
        items.Should().ContainSingle("the second read found the item the first had written");
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    private static CommandContext ManualContext(long listId, Func<IAsyncDisposable?>? yieldWorker) =>
        new(
            1,
            JsonSerializer.Serialize(new { name = "ImportListSync", importListId = listId }),
            CommandTrigger.Manual,
            _ => Task.CompletedTask,
            yieldWorker);

    private static async Task<long> CreateListAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var list = await scope.ServiceProvider.GetRequiredService<IImportListService>().CreateAsync(
            new ImportListDraft(
                BlockingProvider.BlockingType,
                "Long list",
                JsonDocument.Parse("{}").RootElement.Clone(),
                SourceText: null,
                Policy: "Mirror",
                QualityProfileId: null,
                LibraryId: null),
            CancellationToken.None);

        return list.Id;
    }

    private ServiceProvider BuildServices()
    {
        _resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolveResult { Status = ResolveStatus.Unresolved, Reason = "No match." });

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton(_resolver);
        services.AddSingleton(_songs);
        services.AddSingleton(_playlists);
        services.AddSingleton(_commands);
        services.AddSingleton<ISongFileLock>(_fileLock);
        services.AddSingleton<IImportListProvider>(_provider);
        services.AddScoped(_ => _database.CreateContext(_time));
        services.AddScoped<IPasteListService>(provider => new PasteListService(
            provider.GetRequiredService<WondarrDbContext>(),
            _resolver,
            _songs,
            _time,
            NullLogger<PasteListService>.Instance));
        services.AddScoped<IImportListService>(provider => new ImportListService(
            provider.GetRequiredService<WondarrDbContext>(),
            provider.GetRequiredService<IPasteListService>(),
            _songs,
            _fileLock,
            _commands,
            _playlists,
            [_provider],
            _time,
            NullLogger<ImportListService>.Instance));

        return services.BuildServiceProvider();
    }

    private sealed class Lease : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A provider whose first read blocks until the test lets it go.</summary>
    private sealed class BlockingProvider : IImportListProvider
    {
        public const string BlockingType = "blocking";

        private int _fetches;

        public IReadOnlyList<ImportListEntry> Entries { get; set; } = [];

        public TaskCompletionSource FirstFetchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstFetch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int FetchCount => Volatile.Read(ref _fetches);

        public string Type => BlockingType;

        public string DisplayName => "Blocking";

        public IReadOnlyList<NotificationField> Fields { get; } = [];

        public IReadOnlyList<string> Validate(JsonElement settings, string? sourceText) => [];

        public async Task<ImportListFetchResult> FetchAsync(ImportList list, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _fetches) == 1)
            {
                FirstFetchStarted.SetResult();
                await ReleaseFirstFetch.Task.ConfigureAwait(false);
            }

            return new ImportListFetchResult(Entries);
        }
    }
}
