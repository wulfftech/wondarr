using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Organizer;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Core.Tests.Songs;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>
/// Removing a music video must never act on a path a row merely claims, never leave half the files moved, and never
/// touch a song's audio or the audio's own sidecars.
/// </summary>
public sealed class MusicVideoServiceSafetyTests : IDisposable
{
    private readonly TempRoot _temp = new();
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();
    private readonly MusicVideoOptions _options = new();

    public void Dispose()
    {
        _temp.Dispose();
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_path_outside_the_video_root_is_refused_and_nothing_is_touched()
    {
        var (song, _) = await SeedAsync(_temp.Full("elsewhere/Stolen.mkv"));
        var neighbour = _temp.CreateFile("elsewhere/Stolen.nfo");
        _temp.CreateFile("elsewhere/Stolen.mkv");
        await using var context = _database.CreateContext(_time);

        var act = () => Service(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        (await act.Should().ThrowAsync<MusicVideoPathException>()).Which.Message
            .Should().Be("The video's path is outside the music video folder");
        File.Exists(_temp.Full("elsewhere/Stolen.mkv")).Should().BeTrue();
        File.Exists(neighbour).Should().BeTrue();
        await AssertUntouchedAsync();
    }

    [Fact]
    public async Task The_video_root_itself_and_a_relative_path_are_refused()
    {
        var (song, video) = await SeedAsync(_temp.Full("videos"));
        await using var context = _database.CreateContext(_time);

        var act = () => Service(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        await act.Should().ThrowAsync<MusicVideoPathException>("the root is not strictly inside itself");

        await using (var edit = _database.CreateContext(_time))
        {
            var row = await edit.MusicVideos.SingleAsync(v => v.Id == video.Id);
            row.Path = "videos/relative.mkv";
            await edit.SaveChangesAsync();
        }

        await using var again = _database.CreateContext(_time);
        var relative = () => Service(again).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        await relative.Should().ThrowAsync<MusicVideoPathException>();
    }

    [Fact]
    public async Task A_path_equal_to_the_songs_audio_file_is_refused_even_inside_the_root()
    {
        var audio = _temp.CreateFile("videos/Daft Punk/Teardrop.flac", "audio");
        var (song, _) = await SeedAsync(audio);
        await using (var seed = _database.CreateContext(_time))
        {
            seed.SongFiles.Add(AudioRow(song.Id, audio));
            await seed.SaveChangesAsync();
        }

        await using var context = _database.CreateContext(_time);
        var act = () => Service(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        await act.Should().ThrowAsync<MusicVideoPathException>();
        File.Exists(audio).Should().BeTrue("the audio is never the video's to remove");
        await AssertUntouchedAsync();
    }

    [Fact]
    public async Task A_path_equal_to_another_songs_audio_file_is_refused_too()
    {
        var shared = _temp.CreateFile("videos/Daft Punk/Other.flac", "audio");
        var (song, _) = await SeedAsync(shared);
        await using (var seed = _database.CreateContext(_time))
        {
            var other = await SongSeed.AddAsync(seed, "Other");
            seed.SongFiles.Add(AudioRow(other.Id, shared));
            await seed.SaveChangesAsync();
        }

        await using var context = _database.CreateContext(_time);
        var act = () => Service(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        await act.Should().ThrowAsync<MusicVideoPathException>();
        File.Exists(shared).Should().BeTrue();
    }

    [Fact]
    public async Task Beside_track_a_path_in_a_reference_library_is_refused_because_it_is_no_library_root()
    {
        _options.Layout = MusicVideoOptions.BesideTrackLayout;
        var video = _temp.CreateFile("mine/Artist/Song-video.mkv");
        var (song, _) = await SeedAsync(video);
        await using (var seed = _database.CreateContext(_time))
        {
            seed.ReferenceLibraries.Add(new ReferenceLibrary { Name = "Mine", RootPath = _temp.Full("mine") });
            await seed.SaveChangesAsync();
        }

        await using var context = _database.CreateContext(_time);
        var act = () => Service(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        await act.Should().ThrowAsync<MusicVideoPathException>();
        File.Exists(video).Should().BeTrue();
    }

    [Fact]
    public async Task Beside_track_a_video_inside_a_library_root_is_removed_and_the_audio_and_its_sidecars_stay()
    {
        _options.Layout = MusicVideoOptions.BesideTrackLayout;
        var audio = _temp.CreateFile("music/Artist/Song.flac", "audio");
        var audioNfo = _temp.CreateFile("music/Artist/Song.nfo", "audio's");
        var audioJpg = _temp.CreateFile("music/Artist/Song.jpg", "cover");
        var video = _temp.CreateFile("music/Artist/Song-video.mkv", "video");
        var videoNfo = _temp.CreateFile("music/Artist/Song-video.nfo", "video's");
        var (song, _) = await SeedAsync(video);
        await using (var seed = _database.CreateContext(_time))
        {
            (await seed.Libraries.SingleAsync()).RootPath = _temp.Full("music");
            seed.SongFiles.Add(AudioRow(song.Id, audio));
            await seed.SaveChangesAsync();
        }

        await using var context = _database.CreateContext(_time);
        var removal = await Service(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        removal!.RecycledPaths.Should().HaveCount(2);
        File.Exists(video).Should().BeFalse();
        File.Exists(videoNfo).Should().BeFalse();
        new[] { audio, audioNfo, audioJpg }.Should().OnlyContain(path => File.Exists(path));
    }

    [Fact]
    public async Task A_video_that_shares_the_audios_name_takes_no_sidecar_with_it()
    {
        // A row written before an empty suffix was refused: Song.mkv beside Song.flac, Song.nfo and Song.jpg.
        _options.Layout = MusicVideoOptions.BesideTrackLayout;
        var audio = _temp.CreateFile("music/Artist/Song.flac", "audio");
        var audioNfo = _temp.CreateFile("music/Artist/Song.nfo", "audio's");
        var audioJpg = _temp.CreateFile("music/Artist/Song.jpg", "cover");
        var video = _temp.CreateFile("music/Artist/Song.mkv", "video");
        var (song, _) = await SeedAsync(video);
        await using (var seed = _database.CreateContext(_time))
        {
            (await seed.Libraries.SingleAsync()).RootPath = _temp.Full("music");
            seed.SongFiles.Add(AudioRow(song.Id, audio));
            await seed.SaveChangesAsync();
        }

        await using var context = _database.CreateContext(_time);
        var removal = await Service(context).DeleteAsync(song.Id, blocklist: false, CancellationToken.None);

        removal!.RecycledPaths.Should().ContainSingle();
        File.Exists(video).Should().BeFalse();
        new[] { audio, audioNfo, audioJpg }.Should().OnlyContain(path => File.Exists(path), "they are the audio's, not the video's");
    }

    [Fact]
    public async Task A_failure_part_way_puts_the_moved_files_back_and_names_the_file()
    {
        var (song, video) = await SeedWithFilesAsync();
        await using var context = _database.CreateContext(_time);
        var nfo = Path.ChangeExtension(video.Path!, ".nfo");
        var service = Service(context, bin => new FailingBin(bin, failOnCall: 2, new IOException("locked by another process")));

        var act = () => service.DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<MusicVideoFileException>()).Which;
        failure.Busy.Should().BeTrue();
        failure.Path.Should().Be(nfo);
        failure.Message.Should().Contain(nfo);
        File.Exists(video.Path).Should().BeTrue("the first file was put back");
        File.Exists(nfo).Should().BeTrue();
        await AssertUntouchedAsync();
    }

    [Fact]
    public async Task Rolling_back_never_overwrites_a_file_that_appeared_in_the_meantime()
    {
        var (song, video) = await SeedWithFilesAsync();
        await using var context = _database.CreateContext(_time);
        var service = Service(
            context,
            bin => new FailingBin(
                bin,
                failOnCall: 2,
                new IOException("locked"),
                beforeFailure: () => File.WriteAllText(video.Path!, "someone else's new file")));

        var act = () => service.DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        await act.Should().ThrowAsync<MusicVideoFileException>();
        File.ReadAllText(video.Path!).Should().Be("someone else's new file");
    }

    [Fact]
    public async Task A_failure_that_is_not_an_io_failure_is_not_busy()
    {
        var (song, video) = await SeedWithFilesAsync();
        await using var context = _database.CreateContext(_time);
        var service = Service(context, bin => new FailingBin(bin, failOnCall: 1, new InvalidOperationException("the bin is inside the library")));

        var act = () => service.DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        (await act.Should().ThrowAsync<MusicVideoFileException>()).Which.Busy.Should().BeFalse();
        File.Exists(video.Path).Should().BeTrue();
    }

    [Fact]
    public async Task A_database_failure_after_the_moves_logs_the_recycled_paths_and_rethrows_without_moving_them_back()
    {
        var (song, video) = await SeedWithFilesAsync();
        var logger = new ListLogger();
        await using var context = _database.CreateContext(_time, new ThrowOnSave());
        var service = Service(context, logger: logger);

        var act = () => service.DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(video.Path).Should().BeFalse("the files stay in the bin");
        var error = logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error).Which;
        error.Message.Should().Contain(_temp.Full("bin"));
    }

    [Fact]
    public async Task A_concurrent_duplicate_blocklist_row_counts_as_already_blocklisted_and_the_removal_still_succeeds()
    {
        var (song, video) = await SeedWithFilesAsync();
        var race = new DuplicateOnSave(_database, _time, song.Id, video.YouTubeVideoId);
        await using var context = _database.CreateContext(_time, race);

        var removal = await Service(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        race.Fired.Should().BeTrue();
        removal!.Blocklisted.Should().BeTrue();
        await using var fresh = _database.CreateContext(_time);
        (await fresh.MusicVideos.CountAsync()).Should().Be(0, "the row is removed");
        (await fresh.MusicVideoBlocklist.CountAsync()).Should().Be(1, "the other writer's row, not a second one");
        (await fresh.History.CountAsync(item => item.EventType == HistoryEventType.MusicVideoDeleted)).Should().Be(1);
    }

    [Fact]
    public async Task A_listing_that_hangs_on_the_disk_times_out()
    {
        var (song, _) = await SeedWithFilesAsync();
        using var release = new ManualResetEventSlim();
        await using var context = _database.CreateContext(_time);
        var service = Service(context, disk: new HangingDisk(release));

        var act = () => service.DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        (await act.Should().ThrowAsync<MusicVideoTimeoutException>()).Which.Message.Should().Be("The music video folder did not respond");
        release.Set();
        await AssertUntouchedAsync();
    }

    [Fact]
    public async Task Deleting_a_song_deletes_its_video_row_and_its_blocklist_rows()
    {
        var (song, video) = await SeedWithFilesAsync();
        await using (var seed = _database.CreateContext(_time))
        {
            seed.MusicVideoBlocklist.Add(new MusicVideoBlocklistItem { SongId = song.Id, YouTubeVideoId = "x", Reason = "r" });
            await seed.SaveChangesAsync();
        }

        await using (var context = _database.CreateContext(_time))
        {
            context.Songs.Remove(await context.Songs.SingleAsync(row => row.Id == song.Id));
            await context.SaveChangesAsync();
        }

        await using var fresh = _database.CreateContext(_time);
        (await fresh.MusicVideos.AnyAsync(row => row.Id == video.Id)).Should().BeFalse();
        (await fresh.MusicVideoBlocklist.AnyAsync()).Should().BeFalse();
    }

    private static SongFile AudioRow(long songId, string path) => new()
    {
        SongId = songId,
        Path = path,
        Codec = "flac",
        Container = "flac",
        Size = 5,
        QualityId = SongSeed.HighQuality,
        SourceType = "soulseek",
    };

    private MusicVideoService Service(
        WondarrDbContext context,
        Func<IRecycleBin, IRecycleBin>? wrap = null,
        ILogger<MusicVideoService>? logger = null,
        IDiskOperations? disk = null)
    {
        disk ??= new DiskOperations();
        IRecycleBin bin = new RecycleBin(
            new DiskOperations(),
            new TestOptionsMonitor<ImportOptions>(new ImportOptions { RecycleBinPath = _temp.Full("bin") }),
            new WondarrPaths(_temp.Full("config")),
            _time,
            NullLogger<RecycleBin>.Instance);

        return new MusicVideoService(
            context,
            new TestOptionsMonitor<MusicVideoOptions>(_options),
            wrap is null ? bin : wrap(bin),
            disk,
            logger ?? NullLogger<MusicVideoService>.Instance);
    }

    /// <summary>A song with a present video at <paramref name="path"/> (no files are created).</summary>
    private async Task<(Song Song, MusicVideo Video)> SeedAsync(string path)
    {
        await _database.MigrateAsync(_time);

        _options.Enabled = true;
        _options.RootPath = _temp.Full("videos");

        await using var context = _database.CreateContext(_time);
        var song = await SongSeed.AddAsync(context, "Teardrop", "Daft Punk");
        var video = new MusicVideo
        {
            SongId = song.Id,
            Status = MusicVideoStatus.Present,
            YouTubeVideoId = "u7K72X4eo_s",
            FoundBy = MusicVideoFoundBy.MusicBrainz,
            Path = path,
        };
        context.MusicVideos.Add(video);
        await context.SaveChangesAsync();

        return (song, video);
    }

    /// <summary>A song whose video, <c>.nfo</c> and thumbnail sit inside the video root.</summary>
    private async Task<(Song Song, MusicVideo Video)> SeedWithFilesAsync()
    {
        var path = _temp.CreateFile("videos/Daft Punk/Daft Punk - Teardrop.mkv", "video");
        _temp.CreateFile("videos/Daft Punk/Daft Punk - Teardrop.nfo", "<musicvideo/>");
        _temp.CreateFile("videos/Daft Punk/Daft Punk - Teardrop.jpg", "jpeg");

        return await SeedAsync(path);
    }

    private async Task AssertUntouchedAsync()
    {
        await using var fresh = _database.CreateContext(_time);
        (await fresh.MusicVideos.CountAsync()).Should().Be(1);
        (await fresh.MusicVideoBlocklist.CountAsync()).Should().Be(0);
        (await fresh.History.CountAsync()).Should().Be(0);
    }

    /// <summary>A bin that fails on its Nth call, as a locked file does.</summary>
    private sealed class FailingBin(IRecycleBin inner, int failOnCall, Exception failure, Action? beforeFailure = null) : IRecycleBin
    {
        private int _calls;

        public string? Refusal(string libraryRoot) => inner.Refusal(libraryRoot);

        public Task<string> RecycleAsync(string path, string libraryRoot, CancellationToken cancellationToken)
        {
            if (++_calls == failOnCall)
            {
                beforeFailure?.Invoke();

                throw failure;
            }

            return inner.RecycleAsync(path, libraryRoot, cancellationToken);
        }

        public Task<int> CleanupAsync(CancellationToken cancellationToken) => inner.CleanupAsync(cancellationToken);
    }

    /// <summary>A disk whose existence checks never return until released.</summary>
    private sealed class HangingDisk(ManualResetEventSlim release) : IDiskOperations
    {
        private readonly DiskOperations _real = new();

        public bool FileExists(string path)
        {
            release.Wait(TimeSpan.FromSeconds(30));

            return _real.FileExists(path);
        }

        public bool DirectoryExists(string path) => _real.DirectoryExists(path);

        public void CreateDirectory(string path) => _real.CreateDirectory(path);

        public void CreateEmptyFile(string path) => _real.CreateEmptyFile(path);

        public void WriteNewFile(string path, byte[] bytes) => _real.WriteNewFile(path, bytes);

        public bool HasAnyEntry(string directory) => _real.HasAnyEntry(directory);

        public long GetFileSize(string path) => _real.GetFileSize(path);

        public void MoveFile(string source, string target) => _real.MoveFile(source, target);

        public void CopyFile(string source, string target) => _real.CopyFile(source, target);

        public bool TryCreateHardLink(string source, string target) => _real.TryCreateHardLink(source, target);

        public void WriteAllBytes(string path, byte[] bytes) => _real.WriteAllBytes(path, bytes);

        public void DeleteFile(string path) => _real.DeleteFile(path);

        public void SetUnixFileMode(string path, UnixFileMode mode) => _real.SetUnixFileMode(path, mode);

        public bool AreSameFile(string first, string second) => _real.AreSameFile(first, second);

        public IEnumerable<string> EnumerateFiles(string directory) => _real.EnumerateFiles(directory);

        public DateTime GetLastWriteTimeUtc(string path) => _real.GetLastWriteTimeUtc(path);

        public void SetLastWriteTimeUtc(string path, DateTime utc) => _real.SetLastWriteTimeUtc(path, utc);

        public void DeleteEmptyDirectory(string path) => _real.DeleteEmptyDirectory(path);
    }

    private sealed class ThrowOnSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the database is gone");
    }

    /// <summary>Inserts the same blocklist row from another connection just before the first save, as a racing delete would.</summary>
    private sealed class DuplicateOnSave(SqliteTestDatabase database, TimeProvider time, long songId, string videoId) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;

                await using var other = database.CreateContext(time);
                other.MusicVideoBlocklist.Add(new MusicVideoBlocklistItem { SongId = songId, YouTubeVideoId = videoId, Reason = "other writer" });
                await other.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class ListLogger : ILogger<MusicVideoService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
