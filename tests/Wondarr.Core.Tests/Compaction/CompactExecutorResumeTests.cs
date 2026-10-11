using Wondarr.Core.Compaction;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Compaction;

/// <summary>
/// The compaction executor's resume rules (code review R-003, R-012, R-015) and its Plex trash rule:
/// a row that cannot be finished is given up on, a placed file is never lost for want of a database
/// write, a chosen album is kept, and Plex's trash is emptied only when that is safe. They share the
/// fixtures of <see cref="CompactExecutorTests"/>.
/// </summary>
public partial class CompactExecutorTests
{
    // --- R-003: a row that cannot be finished -------------------------------------------------------

    [Fact]
    public async Task A_staged_row_whose_song_is_gone_is_dropped_and_its_file_goes_to_the_recycle_bin()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var staged = Stage(context, song, "01 - Track 1.flac");
        await context.SaveChangesAsync();

        // The user deleted the song while its file sat in staging.
        context.Songs.Remove(await context.Songs.SingleAsync(candidate => candidate.Id == song));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        context.ChangeTracker.Clear();

        result.GivenUp.Should().Be(1);
        result.Moved.Should().Be(0);

        File.Exists(staged).Should().BeFalse();
        Directory.EnumerateFiles(_bin, "01 - Track 1.flac", SearchOption.AllDirectories).Should().HaveCount(1);

        var row = await context.CompactMoves.SingleAsync();
        row.State.Should().Be(CompactMoveState.Failed);
        row.StagedPath.Should().BeNull("a row with no staged file is finished, not unfinished");
        row.Message.Should().Contain("01 - Track 1.flac").And.Contain("no longer exists");
        (await context.CompactMoves.Where(CompactMoveRules.IsUnfinished).AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_row_past_the_retry_cap_puts_its_file_back_and_the_song_points_at_it()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac", lyrics: "the words");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        var staged = Stage(context, song, "01 - Track 1.flac");
        await context.SaveChangesAsync();
        await File.WriteAllTextAsync(Path.ChangeExtension(staged, ".lrc"), "the words");
        await FailForGoodAsync(context);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        context.ChangeTracker.Clear();

        result.GivenUp.Should().Be(1);
        result.Stranded.Should().BeNull();
        File.Exists(from).Should().BeTrue("the file is back where it came from");
        File.Exists(staged).Should().BeFalse();
        File.ReadAllText(Path.ChangeExtension(from, ".lrc")).Should().Be("the words");

        var row = await context.CompactMoves.SingleAsync();
        row.State.Should().Be(CompactMoveState.GivenUp);
        row.StagedPath.Should().BeNull();
        row.Message.Should().Contain("gave up").And.Contain(from);
        (await context.SongFiles.SingleAsync()).Path.Should().Be(from);
        (await context.AlbumContexts.SingleAsync()).AlbumKey.Should().Be("s-1", "the album never changed");
    }

    [Fact]
    public async Task A_row_past_the_retry_cap_whose_old_path_is_taken_leaves_the_file_and_names_it()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        var staged = Stage(context, song, "01 - Track 1.flac");
        await context.SaveChangesAsync();
        await FailForGoodAsync(context);

        // Something else took the file's old place since: it is never overwritten.
        await File.WriteAllTextAsync(from, "someone else's file");

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        context.ChangeTracker.Clear();

        result.GivenUp.Should().Be(1);
        result.Stranded.Should().Equal(staged);
        File.ReadAllText(from).Should().Be("someone else's file");
        File.Exists(staged).Should().BeTrue();

        // The song points at the file where it is, and the row no longer holds anything unfinished.
        (await context.SongFiles.SingleAsync()).Path.Should().Be(staged);

        var row = await context.CompactMoves.SingleAsync();
        row.StagedPath.Should().BeNull();
        row.Message.Should().Contain(staged);
    }

    [Fact]
    public async Task A_stuck_row_does_not_block_planning_the_rest_of_the_library()
    {
        await using var context = await ContextAsync(section: Section);
        var stuck = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var other = await SeedSongAsync(context, "Track 2", "02 - Track 2.flac");
        var stuckFrom = (await context.SongFiles.AsNoTracking().SingleAsync(file => file.SongId == stuck)).Path;
        Stage(context, stuck, "01 - Track 1.flac");
        await context.SaveChangesAsync();
        await FailForGoodAsync(context);

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [other]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        context.ChangeTracker.Clear();

        result.GivenUp.Should().Be(1);
        result.Moved.Should().Be(1, "the rest of the library was planned and compacted in the same run");

        File.Exists(stuckFrom).Should().BeTrue();
        File.Exists(Path.Combine(_root, "Daft Punk", "Random Access Memories", "02 - Track 2.flac")).Should().BeTrue();
    }

    [Fact]
    public async Task A_move_that_keeps_failing_is_retried_three_times_then_given_up()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;

        _organizer.FailFor = "Track 1";
        Plan(context, [song]);

        // Run 1 plans and fails; runs 2 and 3 resume the row and fail again.
        (await RunPumpedAsync(SeedData.DefaultLibraryId)).Failed.Should().Be(1);
        (await context.CompactMoves.AsNoTracking().SingleAsync()).Attempts.Should().Be(1);

        (await RunPumpedAsync(SeedData.DefaultLibraryId)).Resumed.Should().Be(1);
        (await RunPumpedAsync(SeedData.DefaultLibraryId)).Resumed.Should().Be(1);
        context.ChangeTracker.Clear();
        (await context.CompactMoves.AsNoTracking().SingleAsync()).Attempts.Should().Be(CompactMoveRules.MaxAttempts);

        // The fourth run gives up instead of retrying.
        var last = await RunPumpedAsync(SeedData.DefaultLibraryId);
        context.ChangeTracker.Clear();

        last.GivenUp.Should().Be(1);
        last.Resumed.Should().Be(0);
        File.Exists(from).Should().BeTrue("the file is back where it came from");
        (await context.SongFiles.AsNoTracking().SingleAsync()).Path.Should().Be(from);
        (await context.CompactMoves.Where(CompactMoveRules.IsUnfinished).AnyAsync()).Should().BeFalse();
    }

    // --- R-012: a placed file is recorded -----------------------------------------------------------

    [Fact]
    public async Task A_staged_row_whose_file_was_placed_before_the_run_stopped_is_recorded_there()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var staged = Stage(context, song, "01 - Track 1.flac");
        await context.SaveChangesAsync();

        // The run placed the file and died before it could save: nothing is in staging.
        var placed = Path.Combine(_root, "Daft Punk", "Random Access Memories", "01 - Track 1.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(placed)!);
        File.Move(staged, placed);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        context.ChangeTracker.Clear();

        result.Moved.Should().Be(1);
        result.Failed.Should().Be(0);

        var row = await context.CompactMoves.SingleAsync();
        row.State.Should().Be(CompactMoveState.Placed);
        row.FinalPath.Should().Be(placed);
        (await context.SongFiles.SingleAsync()).Path.Should().Be(placed);
        (await context.AlbumContexts.SingleAsync()).AlbumKey.Should().Be("r1");
        _organizer.Requests.Should().BeEmpty("nothing was placed twice");
    }

    [Fact]
    public async Task A_placed_file_that_took_a_collision_variant_is_found_at_the_variant()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var other = await SeedSongAsync(context, "Track 2", "02 - Track 2.flac");
        var staged = Stage(context, song, "01 - Track 1.flac");
        await context.SaveChangesAsync();

        // Another song's file already holds the planned name, so the placer took "(2)".
        var taken = Path.Combine(_root, "Daft Punk", "Random Access Memories", "01 - Track 1.flac");
        var variant = Path.Combine(_root, "Daft Punk", "Random Access Memories", "01 - Track 1 (2).flac");
        Directory.CreateDirectory(Path.GetDirectoryName(taken)!);
        await File.WriteAllTextAsync(taken, "the other song's audio");
        File.Move(staged, variant);

        var otherFile = await context.SongFiles.SingleAsync(file => file.SongId == other);
        otherFile.Path = taken;
        await context.SaveChangesAsync();

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        context.ChangeTracker.Clear();

        result.Moved.Should().Be(1);
        (await context.SongFiles.SingleAsync(file => file.SongId == song)).Path.Should().Be(variant);
        (await context.SongFiles.SingleAsync(file => file.SongId == other)).Path.Should().Be(taken);
    }

    [Fact]
    public async Task A_placement_that_reports_an_error_after_putting_the_file_down_is_recorded_as_placed()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        _organizer.ErrorAfterMove = "chmod failed";
        Plan(context, [song]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        context.ChangeTracker.Clear();

        var target = Path.Combine(_root, "Daft Punk", "Random Access Memories", "01 - Track 1.flac");

        result.Should().Be(new CompactResult(1, 0, 0, 0));
        File.Exists(target).Should().BeTrue();

        var row = await context.CompactMoves.SingleAsync();
        row.State.Should().Be(CompactMoveState.Placed);
        row.Message.Should().Contain("chmod failed");
        (await context.SongFiles.SingleAsync()).Path.Should().Be(target);
    }

    [Fact]
    public async Task A_shutdown_after_the_file_is_placed_still_records_it()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        Plan(context, [song]);

        using var shutdown = new CancellationTokenSource();
        _organizer.AfterMove = shutdown.Cancel;

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId, shutdown.Token);

        context.ChangeTracker.Clear();

        var target = Path.Combine(_root, "Daft Punk", "Random Access Memories", "01 - Track 1.flac");

        shutdown.IsCancellationRequested.Should().BeTrue();
        result.Moved.Should().Be(1);
        (await context.CompactMoves.SingleAsync()).State.Should().Be(CompactMoveState.Placed);
        (await context.SongFiles.SingleAsync()).Path.Should().Be(target);
    }

    // --- R-015: a chosen album is kept ---------------------------------------------------------------

    [Fact]
    public async Task An_album_the_user_chose_after_the_plan_is_kept_when_the_file_goes_back()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        Plan(context, [song]);

        // The user picked an album after the plan was made.
        var album = await context.AlbumContexts.SingleAsync();
        album.Pinned = true;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        context.ChangeTracker.Clear();

        result.Moved.Should().Be(1);
        (await context.AlbumContexts.SingleAsync()).AlbumKey.Should().Be("s-1", "the plan's album was not applied");
        (await context.AlbumContexts.SingleAsync()).Pinned.Should().BeTrue();

        // The file went back under the user's album, not the plan's.
        File.Exists(Path.Combine(_root, "Daft Punk", "Single 1", "01 - Track 1.flac")).Should().BeTrue();
        _organizer.Requests.Single().Album.AlbumKey.Should().Be("s-1");
    }

    // --- Owner question 7: Plex's trash is emptied only when it is safe -----------------------------

    [Fact]
    public async Task The_trash_is_left_alone_when_the_scan_was_not_seen_to_finish()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        Plan(context, [song]);

        // The section reports a scan that never ends.
        for (var poll = 0; poll < 2_000; poll++)
        {
            _refreshing.Enqueue(true);
        }

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        context.ChangeTracker.Clear();

        result.Moved.Should().Be(1, "the files still go back");
        result.TrashNote.Should().Contain("not seen to finish").And.Contain("until you empty its trash");
        _log.Should().NotContain("empty trash");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public async Task The_trash_is_left_alone_when_plex_empties_it_itself(string autoEmptyTrash)
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        _autoEmptyTrash = autoEmptyTrash;
        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        context.ChangeTracker.Clear();

        result.Moved.Should().Be(1);
        result.TrashNote.Should().Contain("autoEmptyTrash");
        _log.Should().NotContain("empty trash");
    }

    [Fact]
    public async Task The_trash_is_left_alone_when_a_section_folder_maps_to_no_root_wondarr_knows()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        _locations = ["/music", "/somewhere/else"];
        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        context.ChangeTracker.Clear();

        result.TrashNote.Should().Contain("/somewhere/else").And.Contain("cannot be confirmed");
        _log.Should().NotContain("empty trash");
    }

    [Fact]
    public async Task The_trash_is_left_alone_when_a_second_folder_is_missing_or_empty_like_an_unmounted_share()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var share = Path.Combine(Path.GetDirectoryName(_root)!, "share");
        Directory.CreateDirectory(share);

        context.ReferenceLibraries.Add(new ReferenceLibrary { Name = "Share", RootPath = share });
        await context.SaveChangesAsync();

        _locations = ["/music", share];
        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        // The mount point exists, and holds nothing.
        var empty = await RunPumpedAsync(SeedData.DefaultLibraryId);
        context.ChangeTracker.Clear();

        empty.TrashNote.Should().Contain(share).And.Contain("unmounted");
        _log.Should().NotContain("empty trash");

        // The same folder, once it holds a file, is confirmed: a second run empties the trash.
        await File.WriteAllTextAsync(Path.Combine(share, "a.flac"), "audio");
        var again = await SeedSongAsync(context, "Track 2", "02 - Track 2.flac");
        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [again]);

        var confirmed = await RunPumpedAsync(SeedData.DefaultLibraryId);

        context.ChangeTracker.Clear();

        confirmed.TrashNote.Should().BeNull();
        _log.Should().Contain("empty trash");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData(null)]
    public async Task The_trash_is_emptied_when_the_scan_finished_the_preference_is_off_or_unknown_and_every_folder_is_there(
        string? autoEmptyTrash)
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        _autoEmptyTrash = autoEmptyTrash;
        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        context.ChangeTracker.Clear();

        result.Should().Be(new CompactResult(1, 0, 0, 0));
        _log.Should().Contain("empty trash");
    }

    [Fact]
    public async Task An_unreadable_preference_counts_as_unknown_and_does_not_stop_the_emptying()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");

        _plex
            .GetPreferenceAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => throw new HttpRequestException("no prefs"));

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        context.ChangeTracker.Clear();

        result.TrashNote.Should().BeNull();
        _log.Should().Contain("empty trash");
    }

    [Fact]
    public async Task The_command_handler_says_why_the_trash_was_not_emptied()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        _autoEmptyTrash = "1";
        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        var command = new Wondarr.Core.Jobs.CommandContext(
            1,
            $"{{\"name\":\"CompactLibrary\",\"libraryId\":{SeedData.DefaultLibraryId}}}",
            Wondarr.Core.Jobs.CommandTrigger.Manual,
            _ => Task.CompletedTask);

        var message = await Handler().ExecuteAsync(command, CancellationToken.None);

        message.Should().StartWith("Compacted Music: 1 files moved");
        message.Should().Contain("Plex's trash was not emptied").And.Contain("autoEmptyTrash");
    }

    // --- A stranded file stays given up -------------------------------------------------------------

    [Fact]
    public async Task A_stranded_file_is_not_given_up_on_again_on_the_next_run()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        var staged = Stage(context, song, "01 - Track 1.flac");
        await context.SaveChangesAsync();
        await FailForGoodAsync(context);
        await File.WriteAllTextAsync(from, "someone else's file");

        var first = await RunPumpedAsync(SeedData.DefaultLibraryId);
        context.ChangeTracker.Clear();

        first.GivenUp.Should().Be(1);
        first.Stranded.Should().Equal(staged);

        var second = await RunPumpedAsync(SeedData.DefaultLibraryId);
        context.ChangeTracker.Clear();

        second.GivenUp.Should().Be(0);
        second.Stranded.Should().BeNull();
        second.Resumed.Should().Be(0);
        File.Exists(staged).Should().BeTrue();
        (await context.SongFiles.SingleAsync()).Path.Should().Be(staged);

        var row = await context.CompactMoves.SingleAsync();
        row.State.Should().Be(CompactMoveState.GivenUp);
        row.FinalPath.Should().Be(staged);
    }

    [Fact]
    public async Task A_file_an_import_put_in_place_of_a_stranded_one_stays_the_songs_file()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        var staged = Stage(context, song, "01 - Track 1.flac");
        await context.SaveChangesAsync();
        await FailForGoodAsync(context);
        await File.WriteAllTextAsync(from, "someone else's file");

        await RunPumpedAsync(SeedData.DefaultLibraryId);
        context.ChangeTracker.Clear();

        // An import then files its own copy and repoints the song at it.
        var imported = Path.Combine(_root, "Daft Punk", "Random Access Memories", "imported.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(imported)!);
        await File.WriteAllTextAsync(imported, "the import");
        await context.SongFiles.ExecuteUpdateAsync(update => update.SetProperty(file => file.Path, imported));

        var again = await RunPumpedAsync(SeedData.DefaultLibraryId);
        context.ChangeTracker.Clear();

        again.GivenUp.Should().Be(0);
        (await context.SongFiles.SingleAsync()).Path.Should().Be(imported);
        File.Exists(staged).Should().BeTrue("the parked file is left alone");
    }

    [Fact]
    public async Task Giving_up_on_a_song_that_points_at_another_file_leaves_the_parked_file_and_the_row_alone()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        var staged = Stage(context, song, "01 - Track 1.flac");
        await context.SaveChangesAsync();
        await FailForGoodAsync(context);

        // An import replaced the song's file while the parked one waited.
        var imported = Path.Combine(_root, "Daft Punk", "Random Access Memories", "imported.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(imported)!);
        await File.WriteAllTextAsync(imported, "the import");
        await context.SongFiles.ExecuteUpdateAsync(update => update.SetProperty(file => file.Path, imported));

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);
        context.ChangeTracker.Clear();

        result.GivenUp.Should().Be(1);
        result.Stranded.Should().Equal(staged);
        File.Exists(staged).Should().BeTrue();
        File.Exists(from).Should().BeFalse("nothing is put back for a song that has moved on");
        (await context.SongFiles.SingleAsync()).Path.Should().Be(imported);
        (await context.CompactMoves.SingleAsync()).Message.Should().Contain("different file");
    }

    // --- The variant search never picks blindly -----------------------------------------------------

    [Fact]
    public async Task A_foreign_file_at_the_target_is_not_adopted_when_ours_sits_at_a_collision_variant()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var staged = Stage(context, song, "01 - Track 1.flac");
        await context.SaveChangesAsync();

        // An untracked 4 MiB file of the user's sits at the planned name; ours took "(2)".
        var folder = Path.Combine(_root, "Daft Punk", "Random Access Memories");
        var foreign = Path.Combine(folder, "01 - Track 1.flac");
        var ours = Path.Combine(folder, "01 - Track 1 (2).flac");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(foreign, new byte[4 * 1024 * 1024]);
        File.Move(staged, ours);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);
        context.ChangeTracker.Clear();

        result.Moved.Should().Be(1);
        (await context.SongFiles.SingleAsync()).Path.Should().Be(ours);
    }

    [Fact]
    public async Task Two_candidates_that_look_alike_fail_the_row_and_are_named()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var staged = Stage(context, song, "01 - Track 1.flac");
        await context.SaveChangesAsync();

        var folder = Path.Combine(_root, "Daft Punk", "Random Access Memories");
        var first = Path.Combine(folder, "01 - Track 1.flac");
        var second = Path.Combine(folder, "01 - Track 1 (2).flac");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(first, "the audio bytes");
        File.Move(staged, second);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);
        context.ChangeTracker.Clear();

        result.Failed.Should().Be(1);

        var row = await context.CompactMoves.SingleAsync();
        row.State.Should().Be(CompactMoveState.Failed);
        row.Message.Should().Contain(first).And.Contain(second);
        (await context.SongFiles.SingleAsync()).Path.Should().NotBe(first).And.NotBe(second);
    }

    // --- Resume only (the start-up path) ------------------------------------------------------------

    [Fact]
    public async Task A_resume_only_run_never_plans_but_still_gives_up_a_row_past_the_cap()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        Stage(context, song, "01 - Track 1.flac");
        await context.SaveChangesAsync();
        await FailForGoodAsync(context);

        _planner
            .PlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<Task<CompactPlan>>(_ => throw new InvalidOperationException("the run planned"));

        var result = await _executor.RunAsync(SeedData.DefaultLibraryId, null, CancellationToken.None, resumeOnly: true);

        result.GivenUp.Should().Be(1);
        File.Exists(from).Should().BeTrue();
        await _planner.DidNotReceive().PlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_resume_only_run_with_nothing_to_resume_does_nothing()
    {
        await using var context = await ContextAsync(section: Section);
        await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");

        var result = await _executor.RunAsync(SeedData.DefaultLibraryId, null, CancellationToken.None, resumeOnly: true);

        result.Should().Be(new CompactResult(0, 0, 0, 0));
        await _planner.DidNotReceive().PlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        _log.Should().BeEmpty();
    }

    [Fact]
    public async Task The_command_handler_passes_the_resume_only_flag_on()
    {
        await using var context = await ContextAsync(section: null);
        await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");

        var command = new Wondarr.Core.Jobs.CommandContext(
            1,
            $"{{\"name\":\"CompactLibrary\",\"libraryId\":{SeedData.DefaultLibraryId},\"resumeOnly\":true}}",
            Wondarr.Core.Jobs.CommandTrigger.Scheduled,
            _ => Task.CompletedTask);

        await Handler().ExecuteAsync(command, CancellationToken.None);

        await _planner.DidNotReceive().PlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // --- An unreadable folder is a reason, not a crash ----------------------------------------------

    [Fact]
    public async Task A_section_folder_that_cannot_be_listed_leaves_the_trash_alone_with_a_reason()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var share = Path.Combine(Path.GetDirectoryName(_root)!, "share");
        Directory.CreateDirectory(share);
        await File.WriteAllTextAsync(Path.Combine(share, "a.flac"), "audio");

        context.ReferenceLibraries.Add(new ReferenceLibrary { Name = "Share", RootPath = share });
        await context.SaveChangesAsync();

        _locations = ["/music", share];
        _faulty.ThrowFor = share;
        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Moved.Should().Be(1);
        result.TrashNote.Should().Contain("could not be read");
        _log.Should().NotContain("empty trash");
    }

    /// <summary>Marks the one carried row as having failed for good: past the retry cap, with its file in staging.</summary>
    private static async Task FailForGoodAsync(WondarrDbContext context)
    {
        await context.CompactMoves.ExecuteUpdateAsync(update => update
            .SetProperty(row => row.State, CompactMoveState.Failed)
            .SetProperty(row => row.Attempts, CompactMoveRules.MaxAttempts)
            .SetProperty(row => row.Message, "the tag writer refused"));
        context.ChangeTracker.Clear();
    }
}
