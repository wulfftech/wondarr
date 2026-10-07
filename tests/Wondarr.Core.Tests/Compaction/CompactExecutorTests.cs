using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using Wondarr.Core.Compaction;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Jobs;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Organizer;
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
/// The Compact library executor against a real SQLite database and a real temp folder as the library
/// root: files are moved out to the hidden staging folder, Plex is made to forget them, and the same
/// organizer an import uses puts them back. The organizer and Plex are fakes — the organizer moves the
/// source to <c>&lt;root&gt;/&lt;album artist&gt;/&lt;album title&gt;/&lt;file name&gt;</c> and reports
/// success, which is exactly what the real one does for the files these tests seed, without the tag
/// writer's and the placer's own tests being repeated here.
/// </summary>
[Collection(CompactExecutorTests.Serial)]
public class CompactExecutorTests : IDisposable
{
    /// <summary>
    /// The pumped runs move a fake clock against real timers and a real database; with the rest of the
    /// assembly running beside them they were starved now and then, so they run on their own.
    /// </summary>
    public const string Serial = "compaction-serial";

    private const string Section = "1";

    /// <summary>The seeded FLAC quality, which the executor reads off the file row.</summary>
    private const long FlacQualityId = 36;

    private static readonly CompactPlan EmptyPlan = new(SeedData.DefaultLibraryId, 0, 0, 0, []);

    /// <summary>The shape the executor writes a move's proposed album in.</summary>
    private static readonly JsonSerializerOptions ProposedJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _timeProvider = new() { AutoAdvanceAmount = TimeSpan.FromSeconds(2) };
    private readonly IDiskOperations _disk = new DiskOperations();
    private readonly RecordingOrganizer _organizer;
    private readonly ICompactPlanner _planner = Substitute.For<ICompactPlanner>();
    private readonly IPlexConnectionService _connection = Substitute.For<IPlexConnectionService>();
    private readonly IPlexServerClient _plex = Substitute.For<IPlexServerClient>();
    private readonly IPlexLibraryUpdater _updater = Substitute.For<IPlexLibraryUpdater>();

    /// <summary>Everything that happened, in order, across the fakes that can be told apart.</summary>
    private readonly List<string> _log = [];

    /// <summary>What the Plex section reports for <c>refreshing</c>, in order, one poll at a time.</summary>
    private readonly Queue<bool> _refreshing = new();

    private readonly string _root;
    private readonly string _bin;
    private readonly string _config;
    private readonly CompactExecutor _executor;

    private Func<long, CancellationToken, Task<CompactPlan>> _planHandler = (_, _) => Task.FromResult(EmptyPlan);

    public CompactExecutorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wondarr-compact-tests", Guid.NewGuid().ToString("N"), "music");
        _bin = Path.Combine(Path.GetTempPath(), "wondarr-compact-tests", Guid.NewGuid().ToString("N"), "recycle");
        _config = Path.Combine(Path.GetTempPath(), "wondarr-compact-tests", Guid.NewGuid().ToString("N"), "config");

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_bin);
        Directory.CreateDirectory(_config);

        _organizer = new RecordingOrganizer(_root, _disk, _log);

        _planner
            .PlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call => _planHandler(call.ArgAt<long>(0), call.ArgAt<CancellationToken>(1)));

        _connection.GetServerContextAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<(Uri Server, string Token)?>(new(new Uri("http://plex.example:32400/"), "token")));

        _plex
            .RefreshPathAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _log.Add("refresh " + call.ArgAt<string>(3));
                return Task.CompletedTask;
            });

        _plex
            .IsRefreshingAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _log.Add("refreshing");
                return Task.FromResult(_refreshing.Count > 0 && _refreshing.Dequeue());
            });

        _plex
            .EmptyTrashAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _log.Add("empty trash");
                return Task.CompletedTask;
            });

        _updater
            .When(updater => updater.RequestFolder(Arg.Any<long>(), Arg.Any<string>()))
            .Do(call => _log.Add("scan " + call.ArgAt<string>(1)));

        var recycleBin = new RecycleBin(
            _disk,
            new TestOptionsMonitor<ImportOptions>(new ImportOptions { RecycleBinPath = _bin }),
            new WondarrPaths(_config),
            _timeProvider,
            NullLogger<RecycleBin>.Instance);

        _executor = new CompactExecutor(
            _database.CreateContext(_timeProvider),
            _planner,
            _organizer,
            _disk,
            recycleBin,
            _connection,
            _plex,
            _updater,
            _timeProvider,
            new SongFileLock(),
            NullLogger<CompactExecutor>.Instance);
    }

    [Fact]
    public async Task Two_files_of_one_album_are_moved_out_and_back_with_their_lyrics()
    {
        await using var context = await ContextAsync(section: Section);
        var first = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac", lyrics: "the first track");
        var second = await SeedSongAsync(context, "Track 2", "02 - Track 2.flac", lyrics: "the second track");
        await SeedCoverAsync("Daft Punk/Single 1");

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [first, second]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Should().Be(new CompactResult(2, 0, 0, 0));

        var folder = Path.Combine(_root, "Daft Punk", "Random Access Memories");
        File.Exists(Path.Combine(folder, "01 - Track 1.flac")).Should().BeTrue();
        File.Exists(Path.Combine(folder, "02 - Track 2.flac")).Should().BeTrue();
        File.ReadAllText(Path.Combine(folder, "01 - Track 1.lrc")).Should().Be("the first track");
        File.ReadAllText(Path.Combine(folder, "02 - Track 2.lrc")).Should().Be("the second track");

        // Nothing is left in the staging folder, and the old album folder went with its cover.
        Directory.EnumerateFileSystemEntries(Path.Combine(_root, ".wondarr-compact")).Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, "Daft Punk", "Single 1")).Should().BeFalse();
        Directory.EnumerateFiles(_bin, "cover.jpg", SearchOption.AllDirectories).Should().HaveCount(1);

        // The files and the albums say where the songs are now, and every move is done.
        await using var fresh = _database.CreateContext(_timeProvider);
        (await fresh.SongFiles.OrderBy(file => file.Id).Select(file => file.Path).ToListAsync())
            .Should()
            .Equal(
                Path.Combine(folder, "01 - Track 1.flac"),
                Path.Combine(folder, "02 - Track 2.flac"));
        (await fresh.AlbumContexts.OrderBy(album => album.SongId).Select(album => album.AlbumKey).ToListAsync())
            .Should()
            .Equal("r1", "r1");
        (await fresh.CompactMoves.Select(row => row.State.ToString()).ToListAsync())
            .Should()
            .AllBe("Placed");

        // The organizer was asked for the album the plan decided, and never for lyrics.
        _organizer.Requests.Should().HaveCount(2);
        _organizer.Requests.Should().OnlyContain(request => !request.LookUpLyrics);
        _organizer.Requests.Should().OnlyContain(request => request.Album.AlbumTitle == "Random Access Memories");

        // Plex forgot the old folder before the files went back, and the new one is scanned after.
        _log.Should().Equal(
            "refresh /music/Daft Punk/Single 1",
            "refresh /music/Daft Punk/Single 2",
            "refreshing",
            "refreshing",
            "empty trash",
            "organize Track 1",
            "scan " + folder,
            "organize Track 2",
            "scan " + folder);
    }

    [Fact]
    public async Task More_than_twenty_five_old_folders_scan_the_library_root_once()
    {
        await using var context = await ContextAsync(section: Section);

        var songs = new List<long>();

        for (var index = 1; index <= 26; index++)
        {
            songs.Add(await SeedSongAsync(context, "Track " + index, $"0{index} - Track {index}.flac"));
        }

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [.. songs]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Moved.Should().Be(26);

        _log.Where(entry => entry.StartsWith("refresh ", StringComparison.Ordinal))
            .Should()
            .Equal("refresh /music");
    }

    [Fact]
    public async Task An_unlinked_library_is_not_scanned_and_its_files_still_move()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");

        Plan(context, [song]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        await using var fresh = _database.CreateContext(_timeProvider);
        var row = await fresh.CompactMoves.SingleAsync();
        row.Message.Should().BeNull();
        row.State.Should().Be(CompactMoveState.Placed);

        result.Moved.Should().Be(1);
        File.Exists(Path.Combine(_root, "Daft Punk", "Random Access Memories", "01 - Track 1.flac"))
            .Should()
            .BeTrue();
        _log.Should().NotContain(entry => entry.StartsWith("refresh", StringComparison.Ordinal) || entry == "empty trash");
        await _connection.DidNotReceive().GetServerContextAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_scan_that_never_reports_itself_is_not_waited_for_forever()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");

        // The section never says it is refreshing: the wait gives up and the files still go back.
        Plan(context, [song]);

        var started = _timeProvider.GetUtcNow();
        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);
        var waited = _timeProvider.GetUtcNow() - started;

        result.Should().Be(new CompactResult(1, 0, 0, 0));
        _log.Should().Contain("empty trash");

        // It waited for the scan to start for the whole start window, and not for the finish window.
        waited.Should().BeGreaterThanOrEqualTo(CompactExecutor.ScanStartTimeout);
        waited.Should().BeLessThan(CompactExecutor.ScanFinishTimeout);
    }

    [Fact]
    public async Task A_plex_failure_does_not_stop_the_files_going_back()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");

        _plex
            .RefreshPathAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HttpRequestException("plex is down"));

        Plan(context, [song]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Should().Be(new CompactResult(1, 0, 0, 0));
        File.Exists(Path.Combine(_root, "Daft Punk", "Random Access Memories", "01 - Track 1.flac"))
            .Should()
            .BeTrue();
        _log.Should().NotContain("empty trash");
    }

    [Fact]
    public async Task An_organizer_failure_leaves_the_file_in_staging_and_the_album_as_it_was()
    {
        await using var context = await ContextAsync(section: Section);
        var failing = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var placed = await SeedSongAsync(context, "Track 2", "02 - Track 2.flac");

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        _organizer.FailFor = "Track 1";
        Plan(context, [failing, placed]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Should().Be(new CompactResult(1, 0, 1, 0));

        await using var fresh = _database.CreateContext(_timeProvider);

        var failedRow = await fresh.CompactMoves.SingleAsync(row => row.SongId == failing);
        failedRow.State.Should().Be(CompactMoveState.Failed);
        failedRow.Message.Should().Be("the tag writer refused");
        File.Exists(failedRow.StagedPath).Should().BeTrue("the file is parked, not lost");
        failedRow.StagedPath.Should().Contain(".wondarr-compact");

        // The album the organizer had aligned was never saved: the song still holds the album it had.
        (await fresh.AlbumContexts.SingleAsync(album => album.SongId == failing)).AlbumKey.Should().Be("s-1");

        var placedRow = await fresh.CompactMoves.SingleAsync(row => row.SongId == placed);
        placedRow.State.Should().Be(CompactMoveState.Placed);
        File.Exists(Path.Combine(_root, "Daft Punk", "Random Access Memories", "02 - Track 2.flac"))
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task A_run_finishes_the_staged_rows_an_interrupted_run_left()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");

        // The first run staged the file and then died before placing it: that is the state on disk and
        // in the rows now. The second run must finish it, and must not plan again.
        var staged = Stage(context, song, "01 - Track 1.flac");
        await context.SaveChangesAsync();

        _planner
            .PlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<Task<CompactPlan>>(_ => throw new InvalidOperationException("the run re-planned"));

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Should().Be(new CompactResult(1, 0, 0, 1));

        File.Exists(Path.Combine(_root, "Daft Punk", "Random Access Memories", "01 - Track 1.flac"))
            .Should()
            .BeTrue();
        File.Exists(staged).Should().BeFalse();

        await using var fresh = _database.CreateContext(_timeProvider);
        (await fresh.CompactMoves.SingleAsync()).State.Should().Be(CompactMoveState.Placed);
        (await fresh.SongFiles.SingleAsync()).Path.Should().Contain("Random Access Memories");
    }

    [Fact]
    public async Task A_file_an_interrupted_run_moved_but_did_not_record_is_adopted_from_staging()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac", lyrics: "the words");

        // The first run moved the file (not yet its sidecar) and died before saving the row as Staged:
        // the row still says Planned, and the file is only in the staging folder.
        var current = context.SongFiles.AsNoTracking().Single(file => file.SongId == song).Path!;
        context.CompactMoves.Add(new CompactMoveRecord
        {
            LibraryId = SeedData.DefaultLibraryId,
            SongId = song,
            FromPath = current,
            ToPath = Path.Combine(_root, "Daft Punk", "Random Access Memories", "01 - Track 1.flac"),
            Proposed = ProposedJson(context, song, "r1", "Random Access Memories"),
            State = CompactMoveState.Planned,
        });
        await context.SaveChangesAsync();

        var row = context.CompactMoves.AsNoTracking().Single();
        var parked = Path.Combine(_root, ".wondarr-compact", row.Id.ToString(CultureInfo.InvariantCulture), "01 - Track 1.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(parked)!);
        File.Move(current, parked);

        _planner
            .PlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<Task<CompactPlan>>(_ => throw new InvalidOperationException("the run re-planned"));

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Should().Be(new CompactResult(1, 0, 0, 1));

        var folder = Path.Combine(_root, "Daft Punk", "Random Access Memories");
        File.Exists(Path.Combine(folder, "01 - Track 1.flac")).Should().BeTrue();
        File.ReadAllText(Path.Combine(folder, "01 - Track 1.lrc")).Should().Be("the words");
        File.Exists(parked).Should().BeFalse();

        await using var fresh = _database.CreateContext(_timeProvider);
        (await fresh.CompactMoves.SingleAsync()).State.Should().Be(CompactMoveState.Placed);
        (await fresh.SongFiles.SingleAsync()).Path.Should().Be(Path.Combine(folder, "01 - Track 1.flac"));
    }

    [Fact]
    public async Task A_move_that_failed_with_its_file_where_it_was_does_not_block_the_next_compaction()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");

        // An earlier run could not move the file: the row is Failed and nothing is in staging.
        context.CompactMoves.Add(new CompactMoveRecord
        {
            LibraryId = SeedData.DefaultLibraryId,
            SongId = song,
            FromPath = context.SongFiles.AsNoTracking().Single(file => file.SongId == song).Path,
            Proposed = ProposedJson(context, song, "r1", "Random Access Memories"),
            State = CompactMoveState.Failed,
            Message = "the file was locked",
        });
        await context.SaveChangesAsync();

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Should().Be(new CompactResult(1, 0, 0, 0));
        File.Exists(Path.Combine(_root, "Daft Punk", "Random Access Memories", "01 - Track 1.flac")).Should().BeTrue();

        await using var fresh = _database.CreateContext(_timeProvider);
        (await fresh.CompactMoves.Select(move => move.State).ToListAsync())
            .Should()
            .Equal(CompactMoveState.Placed);
    }

    [Fact]
    public async Task A_file_merely_ending_in_cover_jpg_keeps_its_folder()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var folder = Path.GetDirectoryName(context.SongFiles.AsNoTracking().Single(file => file.SongId == song).Path)!;
        await File.WriteAllTextAsync(Path.Combine(folder, "discover.jpg"), "not a cover");

        Plan(context, [song]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Moved.Should().Be(1);
        File.Exists(Path.Combine(folder, "discover.jpg")).Should().BeTrue();
        Directory.EnumerateFiles(_bin, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task A_context_only_move_writes_the_album_and_touches_no_file()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", file: false);

        _planHandler = (_, _) => Task.FromResult(
            new CompactPlan(
                SeedData.DefaultLibraryId,
                1,
                1,
                1,
                [
                    new CompactMove(
                        song,
                        "Track 1",
                        "Daft Punk",
                        Album("s-1", "Single 1"),
                        Album("r1", "Random Access Memories"),
                        FromPath: null,
                        ToPath: null,
                        Proposed(context, song, "r1", "Random Access Memories")),
                ]));

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Should().Be(new CompactResult(0, 1, 0, 0));

        await using var fresh = _database.CreateContext(_timeProvider);
        (await fresh.AlbumContexts.SingleAsync(album => album.SongId == song)).AlbumKey.Should().Be("r1");
        (await fresh.CompactMoves.SingleAsync()).State.Should().Be(CompactMoveState.Placed);
        (await fresh.SongFiles.AnyAsync()).Should().BeFalse();
        _organizer.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_folder_holding_something_beside_the_cover_is_left_alone()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");

        await SeedCoverAsync("Daft Punk/Single 1");
        await File.WriteAllTextAsync(Path.Combine(_root, "Daft Punk", "Single 1", "notes.txt"), "the user's notes");

        Plan(context, [song]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Moved.Should().Be(1);

        var folder = Path.Combine(_root, "Daft Punk", "Single 1");
        Directory.Exists(folder).Should().BeTrue();
        File.Exists(Path.Combine(folder, "cover.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(folder, "notes.txt")).Should().BeTrue();
        Directory.EnumerateFiles(_bin, "cover.jpg", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_run_of_the_same_library_while_one_is_running_throws()
    {
        await using var context = await ContextAsync(section: Section);
        await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        _planHandler = async (_, token) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);

            return EmptyPlan;
        };

        var running = _executor.RunAsync(SeedData.DefaultLibraryId, null, CancellationToken.None);

        await started.Task;

        var second = async () =>
            await _executor.RunAsync(SeedData.DefaultLibraryId, null, CancellationToken.None);

        await second.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A compaction of this library is already running");

        release.SetResult();
        await running;
    }

    [Fact]
    public async Task Compacting_an_unknown_library_throws()
    {
        await ContextAsync(section: Section);

        var run = async () => await _executor.RunAsync(987654, null, CancellationToken.None);

        await run.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task The_command_handler_compacts_the_library_its_body_names()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        var handler = Handler();
        var command = new CommandContext(
            1,
            $"{{\"name\":\"CompactLibrary\",\"libraryId\":{SeedData.DefaultLibraryId}}}",
            CommandTrigger.Manual,
            _ => Task.CompletedTask);

        var message = await handler.ExecuteAsync(command, CancellationToken.None);

        message.Should().Be("Compacted Music: 1 files moved, 0 album changes without files, 0 failed");
        File.Exists(Path.Combine(_root, "Daft Punk", "Random Access Memories", "01 - Track 1.flac"))
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task The_command_handler_fails_the_command_when_the_library_is_unknown()
    {
        await ContextAsync(section: Section);

        var handler = Handler();
        var command = new CommandContext(1, "{\"libraryId\":987654}", CommandTrigger.Manual, _ => Task.CompletedTask);

        var run = async () => await handler.ExecuteAsync(command, CancellationToken.None);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("Library 987654 was not found.");
    }

    [Fact]
    public async Task The_command_handler_needs_a_library_id()
    {
        await ContextAsync(section: Section);

        var handler = Handler();
        var command = new CommandContext(1, "{\"name\":\"CompactLibrary\"}", CommandTrigger.Manual, _ => Task.CompletedTask);

        var run = async () => await handler.ExecuteAsync(command, CancellationToken.None);

        await run.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_move_whose_songs_file_changed_since_the_plan_fails_and_moves_nothing()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        Plan(context, [song]);

        // The song's file was replaced after the plan was made.
        var file = await context.SongFiles.SingleAsync(candidate => candidate.SongId == song);
        file.Path = Path.Combine(_root, "Daft Punk", "Elsewhere.flac");
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Should().Be(new CompactResult(0, 0, 1, 0));

        var row = await context.CompactMoves.SingleAsync();
        row.State.Should().Be(CompactMoveState.Failed);
        row.Message.Should().Be("the song's file changed since the plan");
        row.StagedPath.Should().BeNull();

        File.Exists(Path.Combine(_root, "Daft Punk", "Single 1", "01 - Track 1.flac")).Should().BeTrue();
        Directory.Exists(Path.Combine(_root, ".wondarr-compact")).Should().BeFalse();
    }

    [Fact]
    public async Task A_move_whose_song_is_being_imported_fails_and_moves_nothing()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        Plan(context, [song]);

        var run = new SearchRun
        {
            SongId = song,
            Trigger = SearchTrigger.Automatic,
            StartedAt = _timeProvider.GetUtcNow().UtcDateTime,
        };

        context.SearchRuns.Add(run);
        await context.SaveChangesAsync();

        var candidate = new CandidateRecord
        {
            SearchRunId = run.Id,
            SongId = song,
            SourceType = SourceTypes.Soulseek,
            BlocklistKey = "peer\u001fMusic\\inflight.flac",
            DisplayName = "inflight.flac",
            RemotePath = "Music\\inflight.flac",
            Provider = "peer",
        };

        context.Candidates.Add(candidate);
        await context.SaveChangesAsync();

        context.QueueItems.Add(new QueueItem
        {
            SongId = song,
            CandidateId = candidate.Id,
            SearchRunId = run.Id,
            SourceType = SourceTypes.Soulseek,
            Destination = $"wondarr/{run.Id}",
            State = QueueItemState.Importing,
            Attempt = 1,
        });

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Should().Be(new CompactResult(0, 0, 1, 0));

        var row = await context.CompactMoves.SingleAsync();
        row.State.Should().Be(CompactMoveState.Failed);
        row.Message.Should().Be("the song is being imported");
        row.StagedPath.Should().BeNull();

        File.Exists(Path.Combine(_root, "Daft Punk", "Single 1", "01 - Track 1.flac")).Should().BeTrue();
        Directory.Exists(Path.Combine(_root, ".wondarr-compact")).Should().BeFalse();
    }

    /// <summary>Deletes this test's temp database and folders.</summary>
    /// <summary>
    /// Runs a compaction while moving the fake clock on: the executor's polls wait on
    /// <see cref="TimeProvider"/> timers, which fire only when the clock moves, so a bare await of the
    /// run would wait forever for a scan that a fake Plex never finishes.
    /// </summary>
    private async Task<CompactResult> RunPumpedAsync(long libraryId)
    {
        var run = _executor.RunAsync(libraryId, null, CancellationToken.None);
        var guard = Stopwatch.StartNew();

        while (!run.IsCompleted)
        {
            // Wall-clock, only to stop a hang: generous, because a loaded machine (a full solution
            // test run beside a build) can starve this loop for many seconds.
            if (guard.Elapsed > TimeSpan.FromSeconds(120))
            {
                throw new TimeoutException("The compaction did not finish while the clock was moved on.");
            }

            _timeProvider.Advance(CompactExecutor.ScanPollInterval);
            await Task.WhenAny(run, Task.Delay(5));
        }

        return await run;
    }

    public void Dispose()
    {
        _database.Dispose();

        foreach (var directory in new[] { Path.GetDirectoryName(_root), Path.GetDirectoryName(_bin), Path.GetDirectoryName(_config) })
        {
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A database with this test's library pointed at its temp root, and its Plex link set.</summary>
    private async Task<WondarrDbContext> ContextAsync(string? section)
    {
        await _database.MigrateAsync(_timeProvider);

        var context = _database.CreateContext(_timeProvider);
        var library = await context.Libraries.SingleAsync(candidate => candidate.Id == SeedData.DefaultLibraryId);

        library.RootPath = _root;
        library.NamingTemplate = "{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}";
        library.PlexSectionId = section;
        library.PlexLibraryPath = section is null ? null : "/music";

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return context;
    }

    /// <summary>The command handler over a container that holds the executor this test built.</summary>
    private CompactLibraryCommandHandler Handler()
    {
        var options = new DbContextOptionsBuilder<WondarrDbContext>()
            .UseSqlite($"Data Source={_database.FilePath}")
            .UseSnakeCaseNamingConvention()
            .Options;

        var services = new ServiceCollection();
        services.AddScoped(_ => new WondarrDbContext(options, _timeProvider));
        services.AddSingleton<ICompactExecutor>(_executor);

        var provider = services.BuildServiceProvider();

        return new CompactLibraryCommandHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CompactLibraryCommandHandler>.Instance);
    }

    /// <summary>
    /// Plans the moves of the given songs, all of them into one album, reading each move's current
    /// path, title and album off the database — as the planner does.
    /// </summary>
    private void Plan(WondarrDbContext context, IReadOnlyList<long> songIds)
    {
        var moves = songIds
            .Select(songId =>
            {
                var song = context.Songs.AsNoTracking().Single(candidate => candidate.Id == songId);
                var album = context.AlbumContexts.AsNoTracking().Single(candidate => candidate.SongId == songId);
                var file = context.SongFiles.AsNoTracking().SingleOrDefault(candidate => candidate.SongId == songId);
                var from = file?.Path;

                return new CompactMove(
                    songId,
                    song.Title,
                    song.ArtistCredit,
                    Album(album.AlbumKey, album.AlbumTitle),
                    Album("r1", "Random Access Memories"),
                    from,
                    from is null
                        ? null
                        : Path.Combine(_root, "Daft Punk", "Random Access Memories", Path.GetFileName(from)),
                    Proposed(context, songId, "r1", "Random Access Memories"));
            })
            .ToList();

        _planHandler = (_, _) => Task.FromResult(
            new CompactPlan(SeedData.DefaultLibraryId, songIds.Count, 1, songIds.Count, moves));
    }

    /// <summary>Writes the row and the staged file of a move an earlier run left staged.</summary>
    private string Stage(WondarrDbContext context, long songId, string fileName)
    {
        var directory = Path.Combine(_root, ".wondarr-compact", "1");
        var staged = Path.Combine(directory, fileName);

        Directory.CreateDirectory(directory);

        var current = context.SongFiles.AsNoTracking().Single(file => file.SongId == songId).Path!;
        File.Move(current, staged);

        context.CompactMoves.Add(new CompactMoveRecord
        {
            LibraryId = SeedData.DefaultLibraryId,
            SongId = songId,
            FromPath = current,
            StagedPath = staged,
            ToPath = Path.Combine(_root, "Daft Punk", "Random Access Memories", fileName),
            Proposed = ProposedJson(context, songId, "r1", "Random Access Memories"),
            State = CompactMoveState.Staged,
        });

        return staged;
    }

    /// <summary>The proposed album context of a move, as the planner hands it over: new and untracked.</summary>
    private static AlbumContext Proposed(WondarrDbContext context, long songId, string albumKey, string albumTitle)
    {
        var existing = context.AlbumContexts.AsNoTracking().Single(album => album.SongId == songId);

        return new AlbumContext
        {
            SongId = songId,
            Kind = AlbumContextKind.Album,
            AlbumTitle = albumTitle,
            AlbumArtist = existing.AlbumArtist,
            AlbumKey = albumKey,
            MbReleaseId = albumKey,
            MbReleaseGroupId = albumKey + "-group",
            TrackNo = 1,
            DiscNo = 1,
            TotalTracks = 2,
            Date = "2013-05-17",
            Sticky = true,
        };
    }

    /// <summary>The same context as the executor's own JSON, for the rows a test writes by hand.</summary>
    private static string ProposedJson(WondarrDbContext context, long songId, string albumKey, string albumTitle) =>
        JsonSerializer.Serialize(Proposed(context, songId, albumKey, albumTitle), ProposedJsonOptions);

    private static CompactAlbum Album(string key, string title) =>
        new(key, AlbumContextKind.Album, title, "Daft Punk");

    /// <summary>Puts an album folder's <c>cover.jpg</c> where a move-out would leave it behind.</summary>
    private async Task SeedCoverAsync(string relativeFolder)
    {
        var folder = Path.Combine(_root, relativeFolder);
        Directory.CreateDirectory(folder);

        await File.WriteAllTextAsync(Path.Combine(folder, "cover.jpg"), "jpeg bytes");
    }

    /// <summary>
    /// One song of one album folder, with a real file (and its lyrics sidecar) where the row says it is.
    /// </summary>
    private async Task<long> SeedSongAsync(
        WondarrDbContext context,
        string title,
        string? fileName = null,
        bool file = true,
        string? lyrics = null)
    {
        var index = await context.Songs.CountAsync() + 1;
        var artist = await context.Artists.FirstOrDefaultAsync(candidate => candidate.Name == "Daft Punk")
            ?? context.Artists.Add(new Artist { Name = "Daft Punk", SortName = "Daft Punk", MbArtistId = "a1" }).Entity;

        await context.SaveChangesAsync();

        var song = new Song
        {
            Title = title,
            ArtistCredit = "Daft Punk",
            PrimaryArtistId = artist.Id,
            MbRecordingId = "m-" + index,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };

        context.Songs.Add(song);
        await context.SaveChangesAsync();

        context.SongArtists.Add(new SongArtist
        {
            SongId = song.Id,
            ArtistId = artist.Id,
            Role = ArtistRole.Main,
            Position = 1,
        });

        context.AlbumContexts.Add(new AlbumContext
        {
            SongId = song.Id,
            Kind = AlbumContextKind.Single,
            AlbumTitle = "Single " + index,
            AlbumArtist = "Daft Punk",
            AlbumKey = "s-" + index,
            MbReleaseId = "s-" + index,
            MbReleaseGroupId = "s-" + index + "-group",
            TrackNo = 1,
            DiscNo = 1,
            TotalTracks = 1,
            Date = "2013-05-17",
        });

        if (file)
        {
            var relative = Path.Combine("Daft Punk", "Single " + index, fileName ?? $"0{index} - {title}.flac");
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "the audio bytes");

            if (lyrics is not null)
            {
                await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), lyrics);
            }

            context.SongFiles.Add(new SongFile
            {
                SongId = song.Id,
                Path = path,
                Size = 30_000_000,
                Codec = "flac",
                Container = "flac",
                BitrateKbps = 1000,
                SampleRate = 44_100,
                BitDepth = 16,
                Channels = 2,
                DurationMs = 369_000,
                QualityId = FlacQualityId,
                SourceType = SourceTypes.Soulseek,
                ImportedAt = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc),
            });
        }

        await context.SaveChangesAsync();

        return song.Id;
    }

    /// <summary>
    /// The organizer an import uses, without the tag writer and the placer: it moves the source to the
    /// folder the album names and reports success, or refuses the song it was told to refuse.
    /// </summary>
    private sealed class RecordingOrganizer(string root, IDiskOperations disk, List<string> log) : ILibraryOrganizer
    {
        /// <summary>The song title this organizer refuses, or <see langword="null"/> to refuse nothing.</summary>
        public string? FailFor { get; set; }

        /// <summary>Every request the executor made, in order.</summary>
        public List<OrganizeRequest> Requests { get; } = [];

        /// <inheritdoc />
        public Task<OrganizeResult> OrganizeAsync(OrganizeRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            Requests.Add(request);
            log.Add("organize " + request.Song.Title);

            if (FailFor is not null && request.Song.Title == FailFor)
            {
                // The real organizer leaves the source exactly where it was when tagging fails.
                return Task.FromResult(
                    new OrganizeResult(OrganizeFailure.Tagging, "the tag writer refused", null, null, new Dictionary<string, string>()));
            }

            var directory = Path.Combine(root, request.Album.AlbumArtist, request.Album.AlbumTitle);
            var target = Path.Combine(directory, Path.GetFileName(request.SourcePath));

            disk.CreateDirectory(directory);
            disk.MoveFile(request.SourcePath, target);

            return Task.FromResult(
                new OrganizeResult(
                    OrganizeFailure.None,
                    null,
                    target,
                    null,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["TITLE"] = request.Song.Title }));
        }
    }
}

/// <summary>Runs the compaction executor's tests after the parallel ones, alone.</summary>
[CollectionDefinition(CompactExecutorTests.Serial, DisableParallelization = true)]
public sealed class CompactExecutorSerialGroup
{
}
