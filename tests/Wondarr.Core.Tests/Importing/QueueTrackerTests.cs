using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Core.Tests.Searching;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// The queue poll (ARCHITECTURE §5.2 step 7, §5.5): it follows a grab through the source, gives up on
/// a peer that never answers, stalls or queues us forever, grabs the next candidate after a failure,
/// hands finished files to the import service, recovers from a restart, and only ever tells the UI
/// about a change.
/// </summary>
public sealed class QueueTrackerTests
{
    [Fact]
    public async Task Updates_the_progress_of_a_downloading_grab()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync();

        host.Source.Status = new DownloadStatus(DownloadState.Downloading, 0.4, 4_000, 10_000, null, null);

        await host.Tracker.RunCycleAsync(CancellationToken.None);

        var item = await host.ItemAsync(seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Downloading);
        item.Progress.Should().Be(0.4);
        item.BytesTransferred.Should().Be(4_000);
        item.SizeBytes.Should().Be(10_000);

        var changed = host.Events.Of<QueueItemChangedEvent>().Should().ContainSingle().Subject;
        changed.QueueItemId.Should().Be(seed.QueueItemId);
        changed.State.Should().Be(QueueItemState.Downloading);
        changed.Progress.Should().Be(0.4);
    }

    [Fact]
    public async Task Publishes_nothing_when_a_cycle_learns_nothing_new()
    {
        await using var host = await QueueTestHost.CreateAsync();
        await host.SeedAsync();

        host.Source.Status = new DownloadStatus(DownloadState.Downloading, 0.4, 4_000);

        await host.Tracker.RunCycleAsync(CancellationToken.None);
        await host.Tracker.RunCycleAsync(CancellationToken.None);

        // The first cycle moved the item to Downloading; the second learned exactly the same bytes.
        host.Events.Of<QueueItemChangedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task Imports_a_finished_download_once()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync(options =>
        {
            options.State = QueueItemState.Completed;
            options.Progress = 1;
            options.CreateDownload = true;
        });

        await host.Tracker.RunCycleAsync(CancellationToken.None);
        await host.Tracker.RunCycleAsync(CancellationToken.None);

        host.Import.Imports.Should().Equal(seed.QueueItemId);
    }

    [Fact]
    public async Task Waits_for_a_deferred_item_until_its_next_check()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync(options =>
        {
            options.State = QueueItemState.Completed;
            options.Progress = 1;
            options.NextCheckAt = host.Time.GetUtcNow().UtcDateTime.AddMinutes(10);
        });

        var wait = await host.Tracker.RunCycleAsync(CancellationToken.None);
        host.Import.Imports.Should().BeEmpty();

        // Nothing is moving and the retry is ten minutes away, so that is what the poll sleeps for.
        wait.Should().Be(TimeSpan.FromMinutes(10));

        host.Time.Advance(TimeSpan.FromMinutes(10));

        await host.Tracker.RunCycleAsync(CancellationToken.None);

        host.Import.Imports.Should().Equal(seed.QueueItemId);
    }

    [Fact]
    public async Task Fails_a_grab_the_peer_never_acknowledged()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync(options =>
        {
            options.StateChangedAt = host.Time.GetUtcNow().UtcDateTime.AddMinutes(-11);
        });

        host.Source.Status = new DownloadStatus(DownloadState.Queued, 0, 0);

        await host.Tracker.RunCycleAsync(CancellationToken.None);

        await host.ShouldHaveFailedAsync(seed, "Peer did not respond");
    }

    [Fact]
    public async Task Fails_a_grab_the_peer_queues_us_for()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync(options =>
        {
            options.State = QueueItemState.RemotelyQueued;
            options.StateChangedAt = host.Time.GetUtcNow().UtcDateTime.AddMinutes(-31);
        });

        host.Source.Status = new DownloadStatus(DownloadState.RemotelyQueued, 0, 0, 10_000, 5);

        await host.Tracker.RunCycleAsync(CancellationToken.None);

        await host.ShouldHaveFailedAsync(seed, "Waited 30 min in the peer's queue (place 5)");
    }

    [Fact]
    public async Task Fails_a_grab_that_stalled()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync(options =>
        {
            options.State = QueueItemState.Downloading;
            options.Progress = 0.42;
            options.StateChangedAt = host.Time.GetUtcNow().UtcDateTime.AddMinutes(-6);
            options.LastProgressAt = host.Time.GetUtcNow().UtcDateTime.AddMinutes(-6);
        });

        host.Source.Status = new DownloadStatus(DownloadState.Downloading, 0.42, 0);

        await host.Tracker.RunCycleAsync(CancellationToken.None);

        await host.ShouldHaveFailedAsync(seed, "Stalled at 42 %");
    }

    [Fact]
    public async Task Stops_asking_for_candidates_once_the_attempt_budget_is_spent()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync(options =>
        {
            options.Attempt = host.Search.MaxAutoAttemptsPerSearch;
            options.StateChangedAt = host.Time.GetUtcNow().UtcDateTime.AddMinutes(-11);
        });

        host.Source.Status = new DownloadStatus(DownloadState.Queued, 0, 0);

        await host.Tracker.RunCycleAsync(CancellationToken.None);

        host.SearchService.Grabs.Should().BeEmpty();

        var item = await host.ItemAsync(seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Failed);
        item.Message.Should().Be("Peer did not respond (no more candidates)");
    }

    [Fact]
    public async Task Leaves_the_item_for_the_next_cycle_when_the_source_throws()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync();

        host.Source.StatusFailure = new InvalidOperationException("slskd is not answering");

        await host.Tracker.RunCycleAsync(CancellationToken.None);

        var item = await host.ItemAsync(seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Queued);
        item.Message.Should().BeNull();

        host.Source.Cancels.Should().BeEmpty();
        host.Import.Imports.Should().BeEmpty();
        host.Events.Of<QueueItemChangedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task Recovers_an_item_left_mid_import_by_a_previous_run()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync(options =>
        {
            options.State = QueueItemState.Importing;
            options.CreateDownload = true;
        });

        await host.Tracker.RecoverAsync(CancellationToken.None);

        var item = await host.ItemAsync(seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Completed);
        item.Message.Should().Be("recovered after restart");
        item.NextCheckAt.Should().BeNull();

        var history = await host.HistoryAsync();
        history.Should().ContainSingle();
        history[0].EventType.Should().Be(HistoryEventType.Failed);
        history[0].Data.Should().Contain("recovered after restart");
    }

    [Fact]
    public async Task A_wake_runs_a_cycle_without_the_clock_moving()
    {
        await using var host = await QueueTestHost.CreateAsync();
        await host.SeedAsync();

        host.Source.Status = new DownloadStatus(DownloadState.Downloading, 0.25, 2_500);

        // The first cycle runs on start; the second one only happens because the tracker was woken,
        // because with the clock stopped the poll's own wait never runs out.
        var first = host.Source.NextStatusCall();
        await host.Tracker.StartAsync(CancellationToken.None);
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        var second = host.Source.NextStatusCall();
        host.Tracker.Wake();
        await second.WaitAsync(TimeSpan.FromSeconds(10));

        await host.Tracker.StopAsync(CancellationToken.None);

        host.Source.StatusCalls.Should().Be(2);
    }
}

/// <summary>
/// One test's worth of queue polling: a real SQLite database, a fake clock, a scripted source, a
/// scripted import service and a scripted search, next to the real queue and peer-reputation services.
/// </summary>
internal sealed class QueueTestHost : IAsyncDisposable
{
    private readonly SqliteTestDatabase _database;
    private readonly ServiceProvider _services;
    private readonly string _downloads;

    private QueueTestHost(SqliteTestDatabase database, ServiceProvider services, FakeTimeProvider time, string downloads)
    {
        _database = database;
        _services = services;
        _downloads = downloads;
        Time = time;
    }

    /// <summary>The fake clock the tracker and the database read; it starts at 2026-09-29T12:00Z.</summary>
    public FakeTimeProvider Time { get; }

    /// <summary>The poll intervals and timeouts every test runs with.</summary>
    public QueueOptions Options { get; private init; } = new();

    /// <summary>The attempt budget.</summary>
    public SearchOptions Search { get; private init; } = new();

    /// <summary>The scripted source.</summary>
    public FakeQueueSourceProvider Source { get; private init; } = null!;

    /// <summary>The scripted import service.</summary>
    public FakeQueueImportService Import { get; private init; } = null!;

    /// <summary>The scripted search.</summary>
    public FakeQueueSearchService SearchService { get; private init; } = null!;

    /// <summary>The event aggregator, which records what was published.</summary>
    public RecordingEventAggregator Events { get; private init; } = null!;

    /// <summary>The poll under test.</summary>
    public QueueTracker Tracker => _services.GetRequiredService<QueueTracker>();

    /// <summary>What the user can do to the queue.</summary>
    public IQueueActions Actions => _services.GetRequiredService<IQueueActions>();

    /// <summary>Builds a host over a fresh database and a fresh downloads folder.</summary>
    /// <param name="configure">Changes the poll intervals and timeouts.</param>
    /// <param name="configureSearch">Changes the attempt budget.</param>
    public static async Task<QueueTestHost> CreateAsync(
        Action<QueueOptions>? configure = null,
        Action<SearchOptions>? configureSearch = null)
    {
        var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        await database.MigrateAsync(time);

        var options = new QueueOptions();
        configure?.Invoke(options);

        var search = new SearchOptions();
        configureSearch?.Invoke(search);

        var downloads = Path.Combine(Path.GetTempPath(), "wondarr-queue-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloads);

        var source = new FakeQueueSourceProvider();
        var import = new FakeQueueImportService();
        var searchService = new FakeQueueSearchService { Next = 99 };
        var events = new RecordingEventAggregator();

        // The import service under test is a fake, but what it does to the item is not: it marks the
        // row Imported, which is what takes the item out of the queue.
        import.OnImport = async queueItemId =>
        {
            await using var context = database.CreateContext(time);

            var item = await context.QueueItems.FirstOrDefaultAsync(entry => entry.Id == queueItemId);

            if (item is not null)
            {
                item.State = QueueItemState.Imported;
                item.StateChangedAt = time.GetUtcNow().UtcDateTime;
                item.FinishedAt = time.GetUtcNow().UtcDateTime;
            }

            await context.SaveChangesAsync();

            return ImportOutcome.Imported;
        };

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IOptionsMonitor<QueueOptions>>(new TestOptionsMonitor<QueueOptions>(options));
        services.AddSingleton<IOptionsMonitor<SearchOptions>>(new TestOptionsMonitor<SearchOptions>(search));
        services.AddSingleton<ISourceProvider>(source);
        services.AddSingleton<IImportService>(import);
        services.AddSingleton<ISongSearchService>(searchService);
        services.AddSingleton<IEventAggregator>(events);
        services.AddDbContext<WondarrDbContext>(builder => builder
            .UseSqlite($"Data Source={database.FilePath}")
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IQueueService, QueueService>();
        services.AddScoped<IQueueActions, QueueActions>();
        services.AddScoped<ISoulseekUserService, SoulseekUserService>();
        services.AddSingleton<QueueTracker>();
        services.AddSingleton<IQueueTracker>(provider => provider.GetRequiredService<QueueTracker>());

        var host = new QueueTestHost(database, services.BuildServiceProvider(), time, downloads)
        {
            Options = options,
            Search = search,
            Source = source,
            Import = import,
            SearchService = searchService,
            Events = events,
        };

        return host;
    }

    /// <summary>Inserts one grab, and - when asked - the file it downloaded.</summary>
    /// <param name="configure">Changes what the grab looks like.</param>
    public async Task<QueueSeed> SeedAsync(Action<QueueSeedOptions>? configure = null)
    {
        var options = new QueueSeedOptions();
        configure?.Invoke(options);

        await using var context = _database.CreateContext(Time);

        var artist = new Artist { Name = "Daft Punk", SortName = "Daft Punk" };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var song = new Song
        {
            Title = "Get Lucky",
            ArtistCredit = artist.Name,
            PrimaryArtistId = artist.Id,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };

        context.Songs.Add(song);
        await context.SaveChangesAsync();

        var run = new SearchRun
        {
            SongId = song.Id,
            Trigger = SearchTrigger.Automatic,
            StartedAt = Time.GetUtcNow().UtcDateTime,
        };

        context.SearchRuns.Add(run);
        await context.SaveChangesAsync();

        var candidate = new CandidateRecord
        {
            SearchRunId = run.Id,
            SongId = song.Id,
            SourceType = options.SourceType,
            BlocklistKey = BlocklistKeys.Soulseek(options.Provider, options.RemotePath),
            DisplayName = "08 - Get Lucky.flac",
            RemotePath = options.RemotePath,
            Provider = options.Provider,
            QualityId = 36,
            SizeBytes = 10_000_000,
            DurationMs = 369_000,
            Score = 900,
            Accepted = true,
            Grabbed = true,
        };

        context.Candidates.Add(candidate);
        await context.SaveChangesAsync();

        var directory = Path.Combine(_downloads, "wondarr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var downloadPath = Path.Combine(directory, options.DownloadName);

        if (options.CreateDownload)
        {
            await File.WriteAllBytesAsync(downloadPath, new byte[64]);
        }

        var now = Time.GetUtcNow().UtcDateTime;

        var item = new QueueItem
        {
            SongId = song.Id,
            CandidateId = candidate.Id,
            SearchRunId = run.Id,
            SourceType = options.SourceType,
            Handle = options.Handle,
            Destination = "wondarr/" + Path.GetFileName(directory),
            State = options.State,
            Progress = options.Progress,
            BytesTransferred = options.BytesTransferred,
            Attempt = options.Attempt,
            SizeBytes = 10_000_000,
            DownloadPath = options.CreateDownload || options.WithDownloadPath ? downloadPath : null,
            StateChangedAt = options.StateChangedAt ?? now,
            LastProgressAt = options.LastProgressAt ?? now,
            NextCheckAt = options.NextCheckAt,
        };

        context.QueueItems.Add(item);
        await context.SaveChangesAsync();

        return new QueueSeed(song.Id, item.Id, run.Id, candidate.Id, directory, downloadPath);
    }

    /// <summary>Reads one item back, untracked.</summary>
    /// <param name="queueItemId">The item's id.</param>
    public async Task<QueueItem> ItemAsync(long queueItemId)
    {
        await using var context = _database.CreateContext(Time);

        return await context.QueueItems.AsNoTracking().SingleAsync(item => item.Id == queueItemId);
    }

    /// <summary>Reads the item back, or <see langword="null"/> when the row is gone.</summary>
    /// <param name="queueItemId">The item's id.</param>
    public async Task<QueueItem?> FindItemAsync(long queueItemId)
    {
        await using var context = _database.CreateContext(Time);

        return await context.QueueItems.AsNoTracking().FirstOrDefaultAsync(item => item.Id == queueItemId);
    }

    /// <summary>Every history row, oldest first.</summary>
    public async Task<List<HistoryItem>> HistoryAsync()
    {
        await using var context = _database.CreateContext(Time);

        return await context.History.AsNoTracking().OrderBy(item => item.Id).ToListAsync();
    }

    /// <summary>Every blocklist row.</summary>
    public async Task<List<BlocklistItem>> BlocklistAsync()
    {
        await using var context = _database.CreateContext(Time);

        return await context.Blocklist.AsNoTracking().ToListAsync();
    }

    /// <summary>The peer rows, keyed by username.</summary>
    /// <param name="username">The peer's name.</param>
    public async Task<SoulseekUser?> PeerAsync(string username)
    {
        await using var context = _database.CreateContext(Time);

        return await context.SoulseekUsers.AsNoTracking().FirstOrDefaultAsync(user => user.Username == username);
    }

    /// <summary>Asserts everything a failed grab has to leave behind.</summary>
    /// <param name="seed">The grab that failed.</param>
    /// <param name="message">The message the user reads.</param>
    public async Task ShouldHaveFailedAsync(QueueSeed seed, string message)
    {
        var item = await ItemAsync(seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Failed);
        item.Message.Should().Be(message);
        item.FinishedAt.Should().NotBeNull();

        Source.Cancels.Should().ContainSingle(handle => handle.SourceType == SourceTypes.Soulseek);
        Source.Cancels.Should().NotContain(handle => handle.Value.Length == 0);

        var history = await HistoryAsync();
        history.Should().ContainSingle();
        history[0].EventType.Should().Be(HistoryEventType.Failed);

        var data = JsonDocument.Parse(history[0].Data).RootElement;
        data.GetProperty("message").GetString().Should().Be(item.Message);
        data.GetProperty("provider").GetString().Should().Be("peer");
        data.GetProperty("attempt").GetInt32().Should().Be(1);
        data.GetProperty("nextQueueItemId").GetString().Should().Be("99");

        var peer = await PeerAsync("peer");
        peer.Should().NotBeNull();
        peer!.Failures.Should().Be(1);

        // The peer is at fault, not the file: nothing is blocklisted, and the next candidate is tried.
        (await BlocklistAsync()).Should().BeEmpty();
        SearchService.Grabs.Should().Equal((seed.SearchRunId, 2));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _database.Dispose();

        if (Directory.Exists(_downloads))
        {
            Directory.Delete(_downloads, recursive: true);
        }
    }
}

/// <summary>What one <see cref="QueueTestHost.SeedAsync"/> produced.</summary>
/// <param name="SongId">The song's id.</param>
/// <param name="QueueItemId">The queue item's id.</param>
/// <param name="SearchRunId">The run the grab belongs to.</param>
/// <param name="CandidateId">The grabbed candidate's id.</param>
/// <param name="DownloadDirectory">The folder the source downloaded into.</param>
/// <param name="DownloadPath">Where the file landed.</param>
internal sealed record QueueSeed(
    long SongId,
    long QueueItemId,
    long SearchRunId,
    long CandidateId,
    string DownloadDirectory,
    string DownloadPath);

/// <summary>The knobs one seeded grab can be given.</summary>
internal sealed class QueueSeedOptions
{
    /// <summary>The state the grab is in.</summary>
    public QueueItemState State { get; set; } = QueueItemState.Queued;

    /// <summary>How far the download has got.</summary>
    public double Progress { get; set; }

    /// <summary>How many bytes have arrived.</summary>
    public long BytesTransferred { get; set; }

    /// <summary>Which automatic attempt this grab is.</summary>
    public int Attempt { get; set; } = 1;

    /// <summary>The source's opaque handle for the grab.</summary>
    public string Handle { get; set; } = "{\"username\":\"peer\",\"id\":\"transfer\"}";

    /// <summary>The peer that served the file.</summary>
    public string Provider { get; set; } = "peer";

    /// <summary>The file's path at the peer.</summary>
    public string RemotePath { get; set; } = "Music\\Daft Punk\\08 - Get Lucky.flac";

    /// <summary>The downloaded file's name.</summary>
    public string DownloadName { get; set; } = "08 - Get Lucky.flac";

    /// <summary>The source the grab went through.</summary>
    public string SourceType { get; set; } = SourceTypes.Soulseek;

    /// <summary>When the state last changed; "now" unless the test says otherwise.</summary>
    public DateTime? StateChangedAt { get; set; }

    /// <summary>When progress was last seen; "now" unless the test says otherwise.</summary>
    public DateTime? LastProgressAt { get; set; }

    /// <summary>When the poll should look again, when the item is waiting on something.</summary>
    public DateTime? NextCheckAt { get; set; }

    /// <summary>Whether the downloaded file exists on disk.</summary>
    public bool CreateDownload { get; set; }

    /// <summary>Whether the item carries a download path even without a file.</summary>
    public bool WithDownloadPath { get; set; }
}

/// <summary>A source that answers every status question the same way, and records what it was asked.</summary>
internal sealed class FakeQueueSourceProvider : ISourceProvider
{
    private readonly Queue<TaskCompletionSource> _waiters = new();

    /// <summary>What every status call returns.</summary>
    public DownloadStatus Status { get; set; } = new(DownloadState.Downloading, 0.1, 1_000);

    /// <summary>Thrown by every status call when set.</summary>
    public Exception? StatusFailure { get; set; }

    /// <summary>Thrown by every cancel call when set.</summary>
    public Exception? CancelFailure { get; set; }

    /// <summary>How many times the status was asked for.</summary>
    public int StatusCalls { get; private set; }

    /// <summary>Every handle that was cancelled.</summary>
    public List<GrabHandle> Cancels { get; } = [];

    /// <inheritdoc />
    public string SourceType => SourceTypes.Soulseek;

    /// <summary>Waits for the next status call.</summary>
    public Task NextStatusCall()
    {
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiters)
        {
            _waiters.Enqueue(waiter);
        }

        return waiter.Task;
    }

    /// <inheritdoc />
    public Task<(bool Available, string? Reason)> GetAvailabilityAsync(CancellationToken cancellationToken) =>
        Task.FromResult((true, (string?)null));

    /// <inheritdoc />
    public Task<SourceSearchResult> SearchAsync(SongSearchRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The queue poll does not search.");

    /// <inheritdoc />
    public Task<GrabHandle> GrabAsync(Candidate candidate, string destination, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The queue poll does not grab.");

    /// <inheritdoc />
    public Task<DownloadStatus> GetStatusAsync(GrabHandle handle, CancellationToken cancellationToken)
    {
        StatusCalls++;

        lock (_waiters)
        {
            while (_waiters.Count > 0)
            {
                _waiters.Dequeue().TrySetResult();
            }
        }

        return StatusFailure is { } failure
            ? Task.FromException<DownloadStatus>(failure)
            : Task.FromResult(Status);
    }

    /// <inheritdoc />
    public Task CancelAsync(GrabHandle handle, CancellationToken cancellationToken)
    {
        Cancels.Add(handle);

        return CancelFailure is { } failure
            ? Task.FromException(failure)
            : Task.CompletedTask;
    }
}

/// <summary>An import service that records what it was handed and can act like the real one.</summary>
internal sealed class FakeQueueImportService : IImportService
{
    /// <summary>Every queue item the import was asked for.</summary>
    public List<long> Imports { get; } = [];

    /// <summary>What the import does with the item it was handed.</summary>
    public Func<long, Task<ImportOutcome>>? OnImport { get; set; }

    /// <inheritdoc />
    public Task<ImportOutcome> ImportAsync(long queueItemId, CancellationToken cancellationToken)
    {
        Imports.Add(queueItemId);

        return OnImport is { } hook
            ? hook(queueItemId)
            : Task.FromResult(ImportOutcome.Imported);
    }
}

/// <summary>A search service that records the next-attempt asks and returns scripted answers.</summary>
internal sealed class FakeQueueSearchService : ISongSearchService
{
    /// <summary>What <see cref="GrabBestAsync"/> returns.</summary>
    public long? Next { get; set; }

    /// <summary>Thrown by every <see cref="GrabBestAsync"/> call when set.</summary>
    public Exception? GrabFailure { get; set; }

    /// <summary>Every next-attempt ask, as the run and the attempt it was given.</summary>
    public List<(long SearchRunId, int Attempt)> Grabs { get; } = [];

    /// <inheritdoc />
    public Task<long?> GrabBestAsync(long searchRunId, int attempt, CancellationToken cancellationToken)
    {
        Grabs.Add((searchRunId, attempt));

        return GrabFailure is { } failure
            ? Task.FromException<long?>(failure)
            : Task.FromResult(Next);
    }

    /// <inheritdoc />
    public Task<SongSearchResult> SearchAsync(
        long songId,
        SearchTrigger trigger,
        bool grab,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The queue poll does not search.");

    /// <inheritdoc />
    public Task<long> GrabCandidateAsync(long candidateRecordId, int attempt, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The queue poll grabs through the next attempt only.");
}