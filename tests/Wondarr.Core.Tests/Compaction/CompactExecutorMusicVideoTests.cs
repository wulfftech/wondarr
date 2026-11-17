using Wondarr.Core.Compaction;
using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Wondarr.Core.Tests.Compaction;

/// <summary>
/// The Compact library task and the music videos that sit beside their track (ADR-0014): the video, its
/// <c>.nfo</c> and its thumbnail travel with the audio file, the <c>music_video</c> row follows, and a move that
/// is given up puts everything back. They share the fixtures of <see cref="CompactExecutorTests"/>.
/// </summary>
public partial class CompactExecutorTests
{
    private static readonly string[] VideoSuffixes = [".mkv", ".nfo", "-thumb.jpg"];

    [Fact]
    public async Task A_song_moves_with_its_beside_track_video_nfo_and_thumbnail_and_the_row_follows()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac", lyrics: "the words");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        await SeedVideoAsync(context, song, from, "-clip");
        await SeedCoverAsync("Daft Punk/Single 1");

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Should().Be(new CompactResult(1, 0, 0, 0));

        var folder = Path.Combine(_root, "Daft Punk", "Random Access Memories");

        foreach (var suffix in VideoSuffixes)
        {
            File.Exists(Path.Combine(folder, "01 - Track 1-clip" + suffix)).Should().BeTrue(suffix);
            File.Exists(Path.ChangeExtension(from, null) + "-clip" + suffix).Should().BeFalse(suffix);
        }

        File.ReadAllText(Path.Combine(folder, "01 - Track 1.lrc")).Should().Be("the words");

        // Staging is empty, and the old folder held only its cover, so it was cleaned up as before.
        Directory.EnumerateFileSystemEntries(Path.Combine(_root, ".wondarr-compact")).Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, "Daft Punk", "Single 1")).Should().BeFalse();

        await using var fresh = _database.CreateContext(_timeProvider);
        (await fresh.SongFiles.SingleAsync()).Path.Should().Be(Path.Combine(folder, "01 - Track 1.flac"));
        (await fresh.MusicVideos.SingleAsync()).Path.Should().Be(Path.Combine(folder, "01 - Track 1-clip.mkv"));
    }

    [Fact]
    public async Task A_song_with_no_video_moves_as_before()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Should().Be(new CompactResult(1, 0, 0, 0));
        (await _database.CreateContext(_timeProvider).MusicVideos.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_video_in_the_video_root_is_left_alone_when_the_song_moves()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");

        var videoRoot = Path.Combine(Path.GetDirectoryName(_root)!, "videos");
        Directory.CreateDirectory(videoRoot);
        var video = Path.Combine(videoRoot, "Daft Punk - Track 1.mkv");
        await File.WriteAllTextAsync(video, "matroska bytes");
        context.MusicVideos.Add(new MusicVideo { SongId = song, Status = MusicVideoStatus.Present, YouTubeVideoId = "abc", FoundBy = MusicVideoFoundBy.Manual, Path = video });
        await context.SaveChangesAsync();

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        await RunPumpedAsync(SeedData.DefaultLibraryId);

        File.Exists(video).Should().BeTrue();
        (await _database.CreateContext(_timeProvider).MusicVideos.SingleAsync()).Path.Should().Be(video);
    }

    [Fact]
    public async Task A_failed_placement_keeps_the_video_in_staging_and_the_row_where_the_song_file_row_is()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        await SeedVideoAsync(context, song, from, "-video");

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        _organizer.FailFor = "Track 1";
        Plan(context, [song]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Failed.Should().Be(1);

        await using var fresh = _database.CreateContext(_timeProvider);
        var staged = (await fresh.CompactMoves.SingleAsync()).StagedPath!;

        // The audio file and everything that travelled with it are parked together, and the row names the staged
        // video, so it never names a file that is not there during the run.
        foreach (var suffix in VideoSuffixes)
        {
            File.Exists(Path.ChangeExtension(staged, null) + "-video" + suffix).Should().BeTrue(suffix);
            File.Exists(Path.ChangeExtension(from, null) + "-video" + suffix).Should().BeFalse(suffix);
        }

        (await fresh.SongFiles.SingleAsync()).Path.Should().Be(from);
        (await fresh.MusicVideos.SingleAsync()).Path.Should().Be(Path.ChangeExtension(staged, null) + "-video.mkv");
    }

    [Fact]
    public async Task A_row_given_up_on_puts_the_audio_its_lyrics_and_the_whole_video_back_and_the_row_unchanged()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac", lyrics: "the words");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        await SeedVideoAsync(context, song, from, "-video");
        var staged = StageWithVideo(context, song, "01 - Track 1.flac", "-video");
        await context.SaveChangesAsync();
        await File.WriteAllTextAsync(Path.ChangeExtension(staged, ".lrc"), "the words");
        await FailForGoodAsync(context);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.GivenUp.Should().Be(1);
        result.Stranded.Should().BeNull();

        File.Exists(from).Should().BeTrue();
        File.ReadAllText(Path.ChangeExtension(from, ".lrc")).Should().Be("the words");

        foreach (var suffix in VideoSuffixes)
        {
            File.Exists(Path.ChangeExtension(from, null) + "-video" + suffix).Should().BeTrue(suffix);
            File.Exists(Path.ChangeExtension(staged, null) + "-video" + suffix).Should().BeFalse(suffix);
        }

        context.ChangeTracker.Clear();
        (await context.SongFiles.SingleAsync()).Path.Should().Be(from);
        (await context.MusicVideos.SingleAsync()).Path.Should().Be(Path.ChangeExtension(from, null) + "-video.mkv");
    }

    [Fact]
    public async Task A_row_given_up_on_whose_old_place_is_taken_leaves_the_video_in_staging_and_the_row_names_it()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        await SeedVideoAsync(context, song, from, "-video");
        var staged = StageWithVideo(context, song, "01 - Track 1.flac", "-video");
        await context.SaveChangesAsync();
        await FailForGoodAsync(context);

        // Something else took the audio file's old place: it is never overwritten, so nothing goes back.
        await File.WriteAllTextAsync(from, "someone else's file");

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Stranded.Should().Equal(staged);
        File.Exists(Path.ChangeExtension(staged, null) + "-video.mkv").Should().BeTrue();

        context.ChangeTracker.Clear();
        (await context.SongFiles.SingleAsync()).Path.Should().Be(staged);
        (await context.MusicVideos.SingleAsync()).Path.Should().Be(Path.ChangeExtension(staged, null) + "-video.mkv");
    }

    [Fact]
    public async Task A_video_whose_new_name_is_taken_stays_in_staging_and_is_not_replaced()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        await SeedVideoAsync(context, song, from, "-video");

        // The new folder already holds a file under the video's new name.
        var folder = Path.Combine(_root, "Daft Punk", "Random Access Memories");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "01 - Track 1-video.mkv"), "somebody else's video");

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Moved.Should().Be(1);
        File.ReadAllText(Path.Combine(folder, "01 - Track 1-video.mkv")).Should().Be("somebody else's video");
        File.Exists(Path.Combine(folder, "01 - Track 1-video.nfo")).Should().BeFalse("the sidecars stay with the video that could not move");

        // The video, with its sidecars, is where the row says: in the staging folder.
        await using var fresh = _database.CreateContext(_timeProvider);
        var video = await fresh.MusicVideos.SingleAsync();
        video.Path.Should().Contain(".wondarr-compact");
        File.Exists(video.Path).Should().BeTrue();
        File.Exists(Path.ChangeExtension(video.Path, ".nfo")).Should().BeTrue();
    }

    [Fact]
    public async Task A_staged_row_whose_song_is_gone_sends_the_staged_video_to_the_recycle_bin_too()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        await SeedVideoAsync(context, song, from, "-video");
        var staged = StageWithVideo(context, song, "01 - Track 1.flac", "-video");
        await context.SaveChangesAsync();

        context.Songs.Remove(await context.Songs.SingleAsync(candidate => candidate.Id == song));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.GivenUp.Should().Be(1);

        Directory.Exists(Path.GetDirectoryName(staged)!).Should().BeFalse("everything in the staging folder went to the bin, so the folder went too");
        Directory.EnumerateFiles(_bin, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Should()
            .Contain(["01 - Track 1.flac", "01 - Track 1-video.mkv", "01 - Track 1-video.nfo", "01 - Track 1-video-thumb.jpg"]);
    }

    [Fact]
    public async Task A_run_that_stopped_after_placing_the_audio_finds_the_video_and_records_it()
    {
        await using var context = await ContextAsync(section: null);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        await SeedVideoAsync(context, song, from, "-video");
        var staged = StageWithVideo(context, song, "01 - Track 1.flac", "-video");
        await context.SaveChangesAsync();

        // The earlier run placed the audio file and stopped before it moved the video or recorded anything.
        var folder = Path.Combine(_root, "Daft Punk", "Random Access Memories");
        Directory.CreateDirectory(folder);
        File.Move(staged, Path.Combine(folder, "01 - Track 1.flac"));

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.Moved.Should().Be(1);

        foreach (var suffix in VideoSuffixes)
        {
            File.Exists(Path.Combine(folder, "01 - Track 1-video" + suffix)).Should().BeTrue(suffix);
        }

        context.ChangeTracker.Clear();
        (await context.MusicVideos.SingleAsync()).Path.Should().Be(Path.Combine(folder, "01 - Track 1-video.mkv"));
    }

    [Fact]
    public async Task A_row_staged_by_a_run_is_given_up_on_and_the_video_row_goes_back_to_the_old_name()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        await SeedVideoAsync(context, song, from, "-video");

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        _organizer.FailFor = "Track 1";
        Plan(context, [song]);
        await RunPumpedAsync(SeedData.DefaultLibraryId);

        // The run staged the video and its row followed. The row never gets past the retry cap, so give up by hand.
        context.ChangeTracker.Clear();
        (await context.MusicVideos.SingleAsync()).Path.Should().Contain(".wondarr-compact");
        await FailForGoodAsync(context);

        var result = await RunPumpedAsync(SeedData.DefaultLibraryId);

        result.GivenUp.Should().Be(1);
        File.Exists(Path.ChangeExtension(from, null) + "-video.mkv").Should().BeTrue();

        context.ChangeTracker.Clear();
        (await context.MusicVideos.SingleAsync()).Path.Should().Be(Path.ChangeExtension(from, null) + "-video.mkv");
    }

    [Fact]
    public async Task A_sibling_audio_files_video_is_left_untouched_when_the_other_file_moves()
    {
        await using var context = await ContextAsync(section: Section);
        var first = await SeedSongAsync(context, "Track 1", "01 - Song.flac");
        var second = await SeedSongAsync(context, "Track 2", "01 - Song (2).flac");

        // Both land in "Single 1" and "Single 2": put them in one folder, as two takes of one song would be.
        var firstFile = (await context.SongFiles.AsNoTracking().SingleAsync(file => file.SongId == first)).Path;
        var secondFile = (await context.SongFiles.AsNoTracking().SingleAsync(file => file.SongId == second)).Path;
        var shared = Path.GetDirectoryName(firstFile)!;
        var moved = Path.Combine(shared, Path.GetFileName(secondFile));
        File.Move(secondFile, moved);
        await context.SongFiles.Where(file => file.SongId == second).ExecuteUpdateAsync(update => update.SetProperty(file => file.Path, moved));
        await SeedVideoAsync(context, second, moved, "-video");

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [first]);

        await RunPumpedAsync(SeedData.DefaultLibraryId);

        // "01 - Song.flac" moved; "01 - Song (2)-video.mkv" starts with its name but is the other song's.
        File.Exists(Path.Combine(shared, "01 - Song (2)-video.mkv")).Should().BeTrue();
        File.Exists(Path.Combine(shared, "01 - Song (2)-video.nfo")).Should().BeTrue();

        await using var fresh = _database.CreateContext(_timeProvider);
        (await fresh.MusicVideos.SingleAsync()).Path.Should().Be(Path.Combine(shared, "01 - Song (2)-video.mkv"));
    }

    [Fact]
    public async Task A_failed_save_after_the_move_still_records_the_audio_and_the_video_where_they_are()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        await SeedVideoAsync(context, song, from, "-video");

        // Every history insert fails, which is what the place step's one save contains.
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail_history BEFORE INSERT ON history BEGIN SELECT RAISE(ABORT, 'forced failure'); END");

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        await RunPumpedAsync(SeedData.DefaultLibraryId);

        var folder = Path.Combine(_root, "Daft Punk", "Random Access Memories");

        await using var fresh = _database.CreateContext(_timeProvider);
        (await fresh.SongFiles.SingleAsync()).Path.Should().Be(Path.Combine(folder, "01 - Track 1.flac"));
        (await fresh.MusicVideos.SingleAsync()).Path.Should().Be(Path.Combine(folder, "01 - Track 1-video.mkv"));
        File.Exists(Path.Combine(folder, "01 - Track 1-video.mkv")).Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_save_after_the_move_names_a_video_that_could_not_move_where_it_stayed()
    {
        await using var context = await ContextAsync(section: Section);
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        await SeedVideoAsync(context, song, from, "-video");

        var folder = Path.Combine(_root, "Daft Punk", "Random Access Memories");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "01 - Track 1-video.mkv"), "somebody else's video");

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail_history BEFORE INSERT ON history BEGIN SELECT RAISE(ABORT, 'forced failure'); END");

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        await RunPumpedAsync(SeedData.DefaultLibraryId);

        await using var fresh = _database.CreateContext(_timeProvider);
        (await fresh.SongFiles.SingleAsync()).Path.Should().Be(Path.Combine(folder, "01 - Track 1.flac"));

        var video = await fresh.MusicVideos.SingleAsync();
        video.Path.Should().Contain(".wondarr-compact");
        File.Exists(video.Path).Should().BeTrue();
    }

    [Fact]
    public async Task A_video_under_the_folder_layout_is_never_staged_or_moved()
    {
        await using var context = await ContextAsync(section: Section);
        _videoOptions.Layout = MusicVideoOptions.FolderLayout;
        var song = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        var from = (await context.SongFiles.AsNoTracking().SingleAsync()).Path;
        await SeedVideoAsync(context, song, from, "-video");

        _refreshing.Enqueue(true);
        _refreshing.Enqueue(false);
        Plan(context, [song]);

        await RunPumpedAsync(SeedData.DefaultLibraryId);

        File.Exists(Path.ChangeExtension(from, null) + "-video.mkv").Should().BeTrue();
        await using var fresh = _database.CreateContext(_timeProvider);
        (await fresh.MusicVideos.SingleAsync()).Path.Should().Be(Path.ChangeExtension(from, null) + "-video.mkv");
    }

    /// <summary>A song's video next to its audio file: the <c>.mkv</c>, its <c>.nfo</c> and its thumbnail, and the row.</summary>
    private static async Task SeedVideoAsync(WondarrDbContext context, long songId, string audioPath, string suffix)
    {
        var baseName = Path.ChangeExtension(audioPath, null) + suffix;

        foreach (var extension in VideoSuffixes)
        {
            await File.WriteAllTextAsync(baseName + extension, "video group " + extension);
        }

        context.MusicVideos.Add(new MusicVideo
        {
            SongId = songId,
            Status = MusicVideoStatus.Present,
            YouTubeVideoId = "yt" + songId,
            FoundBy = MusicVideoFoundBy.Manual,
            Path = baseName + ".mkv",
        });

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    /// <summary>Stages a song's audio file and, with it, the video group beside it, as a run's staging step does.</summary>
    private string StageWithVideo(WondarrDbContext context, long songId, string fileName, string suffix)
    {
        var current = context.SongFiles.AsNoTracking().Single(file => file.SongId == songId).Path;
        var staged = Stage(context, songId, fileName);

        foreach (var extension in VideoSuffixes)
        {
            File.Move(
                Path.ChangeExtension(current, null) + suffix + extension,
                Path.ChangeExtension(staged, null) + suffix + extension);
        }

        return staged;
    }
}
