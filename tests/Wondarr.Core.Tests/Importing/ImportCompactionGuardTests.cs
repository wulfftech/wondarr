using System.Diagnostics;
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wondarr.Core.Compaction;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Core.Tests.Searching;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// The per-song file guard between imports and compaction (P5-02): an import defers while a
/// compaction has its song's file, runs once the compaction has placed it, and defers again when a
/// compaction stages while the import is mid-flight — the race the lock exists for.
/// </summary>
public sealed class ImportCompactionGuardTests : IDisposable
{
    /// <summary>How long the race test waits for the background steps before it gives up.</summary>
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "wondarr-import-compaction-tests",
        Guid.NewGuid().ToString("N"));

    /// <summary>Deletes this test's temp folders.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task An_import_defers_while_a_compaction_has_the_songs_file_staged()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var held = "/data/music/Daft Punk/Random Access Memories/08 - Get Lucky.mp3";
        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = held;
            options.CurrentFileQualityId = 29;
        });

        await InsertMoveAsync(host, seed.SongId, CompactMoveState.Staged, held);

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Deferred);

        await using var context = host.Database.CreateContext(host.Time);
        var item = await context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);

        item.State.Should().Be(QueueItemState.Completed);
        item.NextCheckAt.Should().Be(host.Time.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(1));
        item.Message.Should().Be(ImportService.CompactionWaitMessage);

        // Nothing was counted, blocklisted or written to the history, and the file row is untouched.
        (await context.Blocklist.CountAsync()).Should().Be(0);
        (await context.History.CountAsync()).Should().Be(0);
        host.Search.Grabs.Should().BeEmpty();
        (await context.SongFiles.SingleAsync()).Path.Should().Be(held);
    }

    [Fact]
    public async Task An_import_runs_normally_once_the_compaction_has_placed_the_file()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var held = "/data/music/Daft Punk/Random Access Memories/08 - Get Lucky.mp3";
        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = held;
            options.CurrentFileQualityId = 29;
        });

        await InsertMoveAsync(host, seed.SongId, CompactMoveState.Placed, held);

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Upgraded);

        await using var context = host.Database.CreateContext(host.Time);
        (await context.History.CountAsync(item => item.SongId == seed.SongId
                && item.EventType == HistoryEventType.Upgraded))
            .Should()
            .Be(1);
        (await context.SongFiles.SingleAsync()).Path.Should()
            .Be("/data/music/" + ImportTestHost.DaftPunkRelativePath + ".flac");
    }

    [Fact]
    public async Task An_import_defers_when_a_compaction_stages_while_it_verifies()
    {
        var held = Path.Combine(_root, "Daft Punk", "Single 1", "08 - Get Lucky.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(held)!);
        await File.WriteAllTextAsync(held, "the old audio bytes");

        await using var host = await ImportTestHost.CreateAsync();
        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = held;
            options.CurrentFileQualityId = 29;
        });

        // The library the compaction works on: this test's root, and no Plex link.
        var library = await host.Context.Libraries.SingleAsync(entry => entry.Id == SeedData.DefaultLibraryId);
        library.RootPath = _root;
        library.PlexSectionId = null;
        library.PlexLibraryPath = null;
        await host.Context.SaveChangesAsync();
        host.Context.ChangeTracker.Clear();

        var executor = Executor(host, seed.SongId, held);

        // The import runs until its verification, which it never leaves until the test says so.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Verifier.OnVerifyAsync = _ => gate.Task;

        var importTask = host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(host.Verifier.Requests.Count > 0));

        // The compaction plans its move and its stage step waits on the song's lock.
        var compactTask = executor.RunAsync(SeedData.DefaultLibraryId, null, CancellationToken.None);
        await WaitUntilAsync(async () => await HasMoveAsync(host, row => row.State == CompactMoveState.Planned));

        // The import's re-check sees the compaction's move and defers, which releases the lock.
        gate.SetResult();

        (await importTask).Should().Be(ImportOutcome.Deferred);

        // The compaction stages and places the file while the import waits its minute out.
        var compacted = await compactTask;
        compacted.Moved.Should().Be(1);

        var placed = Path.Combine(_root, "Daft Punk", "Random Access Memories", "08 - Get Lucky.mp3");
        File.Exists(placed).Should().BeTrue();
        (await HasMoveAsync(host, row => row.State == CompactMoveState.Placed)).Should().BeTrue();
        (await LastEventTypeAsync(host, seed.SongId)).Should().Be(HistoryEventType.Renamed);

        // The next poll, after the deferral minute, imports the upgrade.
        host.Time.Advance(TimeSpan.FromMinutes(1));

        var outcome = await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Upgraded);

        await using var context = host.Database.CreateContext(host.Time);
        PathRules
            .AreEqual(
                (await context.SongFiles.SingleAsync()).Path,
                Path.Combine(_root, ImportTestHost.DaftPunkRelativePath) + ".flac")
            .Should()
            .BeTrue("the upgrade landed where the library's template says");
        (await LastEventTypeAsync(host, seed.SongId)).Should().Be(HistoryEventType.Upgraded);
    }

    /// <summary>Inserts one compaction move row for a song, as a compaction run would have left it.</summary>
    private static async Task InsertMoveAsync(
        ImportTestHost host,
        long songId,
        CompactMoveState state,
        string fromPath)
    {
        await using var context = host.Database.CreateContext(host.Time);

        context.CompactMoves.Add(new CompactMoveRecord
        {
            LibraryId = SeedData.DefaultLibraryId,
            SongId = songId,
            FromPath = fromPath,
            StagedPath = state == CompactMoveState.Staged ? fromPath + ".staged" : null,
            ToPath = fromPath,
            Proposed = "{}",
            State = state,
        });

        await context.SaveChangesAsync();
    }

    /// <summary>The compaction executor over the same database as the import, with a planner that
    /// moves the song's file into the album the plan decides.</summary>
    private CompactExecutor Executor(ImportTestHost host, long songId, string fromPath)
    {
        var disk = new DiskOperations();
        var organizer = new MovingOrganizer(_root, disk);
        var planner = Substitute.For<ICompactPlanner>();
        var recycleBin = new RecycleBin(
            disk,
            new TestOptionsMonitor<ImportOptions>(new ImportOptions()),
            new WondarrPaths(Path.Combine(_root, "config")),
            host.Time,
            NullLogger<RecycleBin>.Instance);

        var album = ProposedAlbum(host, songId);

        planner
            .PlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new CompactPlan(
                SeedData.DefaultLibraryId,
                1,
                1,
                1,
                [
                    new CompactMove(
                        songId,
                        "Get Lucky",
                        "Daft Punk",
                        new CompactAlbum("s-1", AlbumContextKind.Single, "Single 1", "Daft Punk"),
                        new CompactAlbum("r1", AlbumContextKind.Album, "Random Access Memories", "Daft Punk"),
                        fromPath,
                        Path.Combine(_root, "Daft Punk", "Random Access Memories", Path.GetFileName(fromPath)),
                        album),
                ])));

        return new CompactExecutor(
            host.Database.CreateContext(host.Time),
            planner,
            organizer,
            disk,
            recycleBin,
            Substitute.For<IPlexConnectionService>(),
            Substitute.For<IPlexServerClient>(),
            Substitute.For<IPlexLibraryUpdater>(),
            host.Time,
            host.SongFileLock,
            NullLogger<CompactExecutor>.Instance);
    }

    /// <summary>The album context the plan proposes, as the planner hands it over.</summary>
    private static AlbumContext ProposedAlbum(ImportTestHost host, long songId)
    {
        using var context = host.Database.CreateContext(host.Time);
        var existing = context.AlbumContexts.AsNoTracking().Single(album => album.SongId == songId);

        return new AlbumContext
        {
            SongId = songId,
            Kind = AlbumContextKind.Album,
            AlbumTitle = "Random Access Memories",
            AlbumArtist = existing.AlbumArtist,
            AlbumKey = "r1",
            MbReleaseId = "r1",
            MbReleaseGroupId = "r1-group",
            TrackNo = 8,
            DiscNo = 1,
            TotalTracks = 13,
            Date = existing.Date,
            Sticky = true,
        };
    }

    /// <summary>Whether the song has a move row the predicate accepts.</summary>
    private static async Task<bool> HasMoveAsync(
        ImportTestHost host,
        Expression<Func<CompactMoveRecord, bool>> predicate)
    {
        await using var context = host.Database.CreateContext(host.Time);

        return await context.CompactMoves.AsNoTracking().AnyAsync(predicate);
    }

    /// <summary>The song's last history event type.</summary>
    private static async Task<HistoryEventType> LastEventTypeAsync(ImportTestHost host, long songId)
    {
        await using var context = host.Database.CreateContext(host.Time);

        var type = await context.History
            .AsNoTracking()
            .Where(item => item.SongId == songId)
            .OrderByDescending(item => item.Id)
            .Select(item => item.EventType)
            .FirstAsync();

        return type;
    }

    /// <summary>Waits until the condition holds, and fails the test when it never does.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var guard = Stopwatch.StartNew();

        while (!await condition())
        {
            if (guard.Elapsed > WaitLimit)
            {
                throw new TimeoutException("The condition never held.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// The organizer a compaction needs, without the tag writer and the placer: it moves the
    /// source into the folder the album names and reports success.
    /// </summary>
    private sealed class MovingOrganizer(string root, IDiskOperations disk) : ILibraryOrganizer
    {
        /// <inheritdoc />
        public Task<OrganizeResult> OrganizeAsync(OrganizeRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var directory = Path.Combine(root, request.Album.AlbumArtist, request.Album.AlbumTitle);
            var target = Path.Combine(directory, Path.GetFileName(request.SourcePath));

            disk.CreateDirectory(directory);
            disk.MoveFile(request.SourcePath, target);

            return Task.FromResult(new OrganizeResult(
                OrganizeFailure.None,
                null,
                target,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["TITLE"] = request.Song.Title }));
        }
    }
}