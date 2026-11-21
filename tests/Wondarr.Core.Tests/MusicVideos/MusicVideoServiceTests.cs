using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
/// Reading, removing and listing music videos, against a real migrated SQLite database, the real recycle bin and real
/// files in a temporary folder.
/// </summary>
public sealed class MusicVideoServiceTests : IDisposable
{
    private readonly TempRoot _temp = new();
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();
    private readonly MusicVideoOptions _options = new();
    private readonly QueryCounter _queries = new();

    public void Dispose()
    {
        _temp.Dispose();
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Deleting_recycles_the_file_and_its_sidecars_blocklists_the_video_and_deletes_the_row()
    {
        var (song, video, files) = await SeedWithFilesAsync();
        await using var context = _database.CreateContext(_time);

        var removal = await Service(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        removal.Should().NotBeNull();
        removal!.Path.Should().Be(video.Path);
        removal.Blocklisted.Should().BeTrue();
        removal.RecycledPaths.Should().HaveCount(3, "the video, the .nfo and the thumbnail");
        removal.RecycledPaths.Should().OnlyContain(path => path.StartsWith(_temp.Full("bin"), StringComparison.Ordinal));
        removal.RecycledPaths.Should().OnlyContain(path => File.Exists(path));
        files.Should().OnlyContain(path => !File.Exists(path), "the files left the video root");
        File.Exists(_temp.Full("videos/Daft Punk/other.mkv")).Should().BeTrue("a neighbour is not a sidecar");

        await using var fresh = _database.CreateContext(_time);
        (await fresh.MusicVideos.CountAsync()).Should().Be(0);
        (await fresh.Songs.CountAsync()).Should().Be(1, "the song and its audio are not touched");

        var blocked = await fresh.MusicVideoBlocklist.SingleAsync();
        blocked.SongId.Should().Be(song.Id);
        blocked.YouTubeVideoId.Should().Be("u7K72X4eo_s");
        blocked.Reason.Should().Be(MusicVideoService.Reason);

        var history = await fresh.History.SingleAsync();
        history.EventType.Should().Be(HistoryEventType.MusicVideoDeleted);
        history.SongId.Should().Be(song.Id);
        using var data = JsonDocument.Parse(history.Data);
        data.RootElement.GetProperty("youTubeVideoId").GetString().Should().Be("u7K72X4eo_s");
        data.RootElement.GetProperty("blocklisted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_without_the_blocklist_leaves_it_empty()
    {
        var (song, _, _) = await SeedWithFilesAsync();
        await using var context = _database.CreateContext(_time);

        var removal = await Service(context).DeleteAsync(song.Id, blocklist: false, CancellationToken.None);

        removal!.Blocklisted.Should().BeFalse();
        await using var fresh = _database.CreateContext(_time);
        (await fresh.MusicVideoBlocklist.CountAsync()).Should().Be(0);
        (await fresh.MusicVideos.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_video_that_is_already_blocklisted_is_not_blocklisted_twice()
    {
        var (song, _, _) = await SeedWithFilesAsync();
        await using (var seed = _database.CreateContext(_time))
        {
            seed.MusicVideoBlocklist.Add(new MusicVideoBlocklistItem { SongId = song.Id, YouTubeVideoId = "u7K72X4eo_s", Reason = "earlier" });
            await seed.SaveChangesAsync();
        }

        await using var context = _database.CreateContext(_time);
        var removal = await Service(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        removal!.Blocklisted.Should().BeTrue();
        await using var fresh = _database.CreateContext(_time);
        (await fresh.MusicVideoBlocklist.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_row_whose_file_is_already_gone_is_still_removed()
    {
        var (song, video, files) = await SeedWithFilesAsync();
        files.ForEach(File.Delete);
        await using var context = _database.CreateContext(_time);

        var removal = await Service(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        removal!.Path.Should().Be(video.Path);
        removal.RecycledPaths.Should().BeEmpty();
        await using var fresh = _database.CreateContext(_time);
        (await fresh.MusicVideos.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_video_inside_a_reference_library_is_left_where_it_is()
    {
        var (song, _, files) = await SeedWithFilesAsync();
        await using (var seed = _database.CreateContext(_time))
        {
            seed.ReferenceLibraries.Add(new ReferenceLibrary { Name = "Mine", RootPath = _temp.Full("videos") });
            await seed.SaveChangesAsync();
        }

        await using var context = _database.CreateContext(_time);
        var act = () => Service(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        await act.Should().ThrowAsync<MusicVideoInReferenceLibraryException>();
        files.Should().OnlyContain(path => File.Exists(path));
        await using var fresh = _database.CreateContext(_time);
        (await fresh.MusicVideos.CountAsync()).Should().Be(1);
        (await fresh.MusicVideoBlocklist.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_song_without_a_video_deletes_to_null()
    {
        await _database.MigrateAsync(_time);
        await using var context = _database.CreateContext(_time);
        var song = await SongSeed.AddAsync(context, "Teardrop");

        (await Service(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None)).Should().BeNull();
        (await Service(context).GetAsync(song.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Get_returns_the_row()
    {
        var (song, video, _) = await SeedWithFilesAsync();
        await using var context = _database.CreateContext(_time);

        var found = await Service(context).GetAsync(song.Id, CancellationToken.None);

        found.Should().NotBeNull();
        found!.YouTubeVideoId.Should().Be(video.YouTubeVideoId);
        found.Status.Should().Be(MusicVideoStatus.Present);
        found.Height.Should().Be(1080);
    }

    [Fact]
    public async Task The_states_are_null_while_the_feature_is_off_and_cost_no_query()
    {
        var (song, _, _) = await SeedWithFilesAsync();
        _options.Enabled = false;
        await using var context = _database.CreateContext(_time, _queries);
        _queries.Reset();

        var states = await Service(context).GetStatesAsync([song.Id], CancellationToken.None);

        states.Should().BeNull();
        _queries.Count.Should().Be(0);
    }

    [Fact]
    public async Task The_states_of_a_whole_page_cost_one_query()
    {
        await _database.MigrateAsync(_time);
        var ids = new List<long>();
        await using (var seed = _database.CreateContext(_time))
        {
            for (var index = 0; index < 5; index++)
            {
                var song = await SongSeed.AddAsync(seed, $"Song {index}");
                ids.Add(song.Id);

                // Three of the five have a video; the others are absent from the answer.
                if (index is 0 or 2 or 4)
                {
                    seed.MusicVideos.Add(new MusicVideo
                    {
                        SongId = song.Id,
                        Status = index == 2 ? MusicVideoStatus.Queued : MusicVideoStatus.Present,
                        YouTubeVideoId = $"video{index}",
                        FoundBy = MusicVideoFoundBy.Manual,
                        Height = index == 2 ? null : 720 + index,
                    });
                }
            }

            await seed.SaveChangesAsync();
        }

        _options.Enabled = true;
        await using var context = _database.CreateContext(_time, _queries);
        _queries.Reset();

        var states = await Service(context).GetStatesAsync(ids, CancellationToken.None);

        _queries.Count.Should().Be(1, "one query for the page, not one per row");
        states.Should().NotBeNull();
        states!.Keys.Should().BeEquivalentTo([ids[0], ids[2], ids[4]]);
        states[ids[0]].Should().BeEquivalentTo(new { Status = MusicVideoStatus.Present, Height = (int?)720, FailureReason = (string?)null });
        states[ids[2]].Should().BeEquivalentTo(new { Status = MusicVideoStatus.Queued, Height = (int?)null, FailureReason = (string?)null });
        states[ids[4]].Should().BeEquivalentTo(new { Status = MusicVideoStatus.Present, Height = (int?)724, FailureReason = (string?)null });
    }

    private MusicVideoService Service(WondarrDbContext context)
    {
        var disk = new DiskOperations();
        var import = new ImportOptions { RecycleBinPath = _temp.Full("bin") };
        var bin = new RecycleBin(
            disk,
            new TestOptionsMonitor<ImportOptions>(import),
            new WondarrPaths(_temp.Full("config")),
            _time,
            NullLogger<RecycleBin>.Instance);

        return new MusicVideoService(
            context,
            new TestOptionsMonitor<MusicVideoOptions>(_options),
            bin,
            disk,
            NullLogger<MusicVideoService>.Instance);
    }

    /// <summary>A song with a present video, its <c>.nfo</c> and thumbnail, and one unrelated neighbour file.</summary>
    private async Task<(Song Song, MusicVideo Video, List<string> Files)> SeedWithFilesAsync()
    {
        await _database.MigrateAsync(_time);

        _options.Enabled = true;
        _options.RootPath = _temp.Full("videos");

        var files = new List<string>
        {
            _temp.CreateFile("videos/Daft Punk/Daft Punk - Teardrop.mkv", "video"),
            _temp.CreateFile("videos/Daft Punk/Daft Punk - Teardrop.nfo", "<musicvideo/>"),
            _temp.CreateFile("videos/Daft Punk/Daft Punk - Teardrop.jpg", "jpeg"),
        };
        _temp.CreateFile("videos/Daft Punk/other.mkv", "someone else's video");

        await using var context = _database.CreateContext(_time);
        var song = await SongSeed.AddAsync(context, "Teardrop", "Daft Punk");
        var video = new MusicVideo
        {
            SongId = song.Id,
            Status = MusicVideoStatus.Present,
            YouTubeVideoId = "u7K72X4eo_s",
            FoundBy = MusicVideoFoundBy.MusicBrainz,
            Path = files[0],
            Width = 1920,
            Height = 1080,
            VideoCodec = "h264",
            AudioCodec = "aac",
            SizeBytes = 5,
            DurationMs = 330_000,
        };
        context.MusicVideos.Add(video);
        await context.SaveChangesAsync();

        return (song, video, files);
    }

    /// <summary>Counts the commands a context runs.</summary>
    private sealed class QueryCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);

            return base.ReaderExecuting(command, eventData, result);
        }
    }
}
