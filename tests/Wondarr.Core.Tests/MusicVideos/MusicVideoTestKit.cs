using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Media;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Organizer;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Core.Tests.Songs;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>A downloader that writes real files into a work folder under the temp root, and is told what to do.</summary>
internal sealed class FakeVideoDownloader : IMusicVideoDownloader
{
    private readonly string _workRoot;

    public FakeVideoDownloader(string workRoot) => _workRoot = workRoot;

    /// <summary>Gets the calls: video id and height cap.</summary>
    public List<(string VideoId, int? MaxHeight)> Calls { get; } = [];

    /// <summary>Gets the downloads that were discarded.</summary>
    public List<MusicVideoDownload> Discarded { get; } = [];

    /// <summary>Gets or sets what runs while the "download" is in flight, before it returns.</summary>
    public Func<Task>? DuringDownload { get; set; }

    /// <summary>Gets or sets the exception the download throws, or <see langword="null"/> for none.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Gets or sets the length the download reports.</summary>
    public int? ReportedDurationMs { get; set; } = 284_000;

    /// <summary>Gets or sets whether a thumbnail is written.</summary>
    public bool WriteThumbnail { get; set; } = true;

    /// <summary>Gets the work folders made.</summary>
    public List<string> WorkFolders { get; } = [];

    /// <summary>Gets or sets what runs while the "download" waits for its turn, before the gate is held.</summary>
    public Func<Task>? WaitingForGate { get; set; }

    public async Task<MusicVideoDownload> DownloadAsync(string videoId, int? maxHeight, CancellationToken cancellationToken, Func<Task>? started = null)
    {
        Calls.Add((videoId, maxHeight));

        if (WaitingForGate is not null)
        {
            await WaitingForGate().ConfigureAwait(false);
        }

        if (started is not null)
        {
            await started().ConfigureAwait(false);
        }

        var work = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        WorkFolders.Add(work);

        if (DuringDownload is not null)
        {
            await DuringDownload().ConfigureAwait(false);
        }

        if (Failure is not null)
        {
            Directory.Delete(work, recursive: true);

            throw Failure;
        }

        var video = Path.Combine(work, videoId + ".mkv");
        await File.WriteAllTextAsync(video, "matroska bytes", cancellationToken).ConfigureAwait(false);

        string? thumbnail = null;

        if (WriteThumbnail)
        {
            thumbnail = Path.Combine(work, videoId + ".jpg");
            await File.WriteAllTextAsync(thumbnail, "jpeg bytes", cancellationToken).ConfigureAwait(false);
        }

        return new MusicVideoDownload(video, thumbnail, ReportedDurationMs);
    }

    public void Discard(MusicVideoDownload download)
    {
        Discarded.Add(download);
        var directory = Path.GetDirectoryName(download.VideoPath);

        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

/// <summary>A media probe that answers a video probe with what the test says.</summary>
internal sealed class FakeVideoProbe : IMediaProbe
{
    /// <summary>Gets or sets what the next probe returns.</summary>
    public VideoProbeResult Result { get; set; } = Ok();

    /// <summary>Gets the paths probed.</summary>
    public List<string> Probed { get; } = [];

    public static VideoProbeResult Ok(
        int videoStreams = 1,
        int audioStreams = 1,
        int durationMs = 284_000) =>
        new(true, new VideoInfo(videoStreams, audioStreams, 1920, 1080, "h264", "aac", durationMs, 14), null);

    public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<VideoProbeResult> ProbeVideoAsync(string path, CancellationToken cancellationToken)
    {
        Probed.Add(path);

        return Task.FromResult(Result);
    }
}

/// <summary>A finder that returns what the test says and remembers the query.</summary>
internal sealed class FakeVideoFinder : IMusicVideoFinder
{
    /// <summary>Gets or sets the candidates returned.</summary>
    public List<MusicVideoCandidate> Candidates { get; set; } = [];

    /// <summary>Gets or sets what the finder throws, or <see langword="null"/>.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Gets or sets an answer that depends on the query; when set, it wins over <see cref="Candidates"/>.</summary>
    public Func<MusicVideoQuery, List<MusicVideoCandidate>>? Answer { get; set; }

    /// <summary>Gets the queries made.</summary>
    public List<MusicVideoQuery> Queries { get; } = [];

    /// <summary>Gets or sets whether the result says MusicBrainz failed to answer.</summary>
    public bool Partial { get; set; }

    public Task<MusicVideoFindResult> FindAsync(MusicVideoQuery query, CancellationToken cancellationToken)
    {
        Queries.Add(query);

        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult(new MusicVideoFindResult(Answer is null ? Candidates : Answer(query), Partial));
    }

    public static MusicVideoCandidate Candidate(
        string videoId,
        bool confident,
        MusicVideoFoundBy foundBy = MusicVideoFoundBy.YouTubeSearch,
        int score = 100) =>
        new(
            videoId,
            "Teardrop",
            ["Massive Attack"],
            MusicVideoCandidateKind.OfficialVideo,
            284_000,
            MusicVideoCandidate.ThumbnailFor(videoId),
            confident,
            foundBy,
            score);
}

/// <summary>
/// A migrated database, a temporary disk with a marked video root, and the music video services over them, with the
/// downloader, the probe, the finder and the command queue faked.
/// </summary>
internal sealed class MusicVideoHarness : IDisposable
{
    public MusicVideoHarness()
    {
        Downloader = new FakeVideoDownloader(Temp.Full("work"));
        Import = new ImportOptions { RecycleBinPath = Temp.Full("bin") };

        Commands = Substitute.For<ICommandQueue>();
        Commands
            .EnqueueAsync(default!, default, default, default)
            .ReturnsForAnyArgs(call => Task.FromResult(new CommandRecord
            {
                Id = ++CommandCount,
                Name = call.ArgAt<string>(0),
                Body = call.ArgAt<string?>(1),
                Trigger = call.ArgAt<CommandTrigger>(2),
            }));
    }

    public TempRoot Temp { get; } = new();

    public SqliteTestDatabase Database { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));

    public MusicVideoOptions Options { get; } = new() { Enabled = true };

    public ImportOptions Import { get; }

    public FakeVideoDownloader Downloader { get; }

    public FakeVideoProbe Probe { get; } = new();

    public FakeVideoFinder Finder { get; } = new();

    public ICommandQueue Commands { get; }

    /// <summary>Gets or sets a disk for the importer to use instead of the real one, to make a step fail.</summary>
    public IDiskOperations? Disk { get; set; }

    public int CommandCount { get; private set; }

    public string VideoRoot => Temp.Full("videos");

    public string MusicRoot => Temp.Full("music");

    /// <summary>Migrates, and makes and marks the video root and the library root.</summary>
    public async Task InitAsync()
    {
        await Database.MigrateAsync(Time);

        Options.RootPath = VideoRoot;
        var roots = new LibraryRoots(new DiskOperations());
        roots.Mark(VideoRoot);
        roots.Mark(MusicRoot);

        await using var context = NewContext();
        var library = await context.Libraries.FirstAsync();
        library.RootPath = MusicRoot;
        await context.SaveChangesAsync();
    }

    public WondarrDbContext NewContext() => Database.CreateContext(Time);

    public MusicVideoService Videos(WondarrDbContext context)
    {
        var disk = new DiskOperations();
        var bin = new RecycleBin(
            disk,
            new TestOptionsMonitor<ImportOptions>(Import),
            new WondarrPaths(Temp.Full("config")),
            Time,
            NullLogger<RecycleBin>.Instance);

        return new MusicVideoService(
            context,
            new TestOptionsMonitor<MusicVideoOptions>(Options),
            bin,
            disk,
            NullLogger<MusicVideoService>.Instance);
    }

    public MusicVideoPathResolver Resolver(WondarrDbContext context) =>
        new(
            context,
            new TestOptionsMonitor<MusicVideoOptions>(Options),
            new TestOptionsMonitor<ImportOptions>(Import),
            new LibraryRoots(new DiskOperations()),
            new WondarrPaths(Temp.Full("config")));

    public MusicVideoImporter Importer(WondarrDbContext context) =>
        new(
            context,
            new TestOptionsMonitor<MusicVideoOptions>(Options),
            Resolver(context),
            Downloader,
            Probe,
            Videos(context),
            Disk ?? new DiskOperations(),
            new TestOptionsMonitor<ImportOptions>(Import),
            NullLogger<MusicVideoImporter>.Instance);

    public MusicVideoSearchService Search(WondarrDbContext context) => new(context, Finder, Commands, Time);

    /// <summary>A song with a file at <paramref name="audioPath"/> (or none), credited to Massive Attack.</summary>
    public async Task<Song> AddSongAsync(string title = "Teardrop", string? audioPath = null, string sourceType = "soulseek", string artist = "Massive Attack")
    {
        await using var context = NewContext();
        var song = await SongSeed.AddAsync(context, title, artist);

        if (audioPath is not null)
        {
            var created = await context.Songs.Include(row => row.File).FirstAsync(row => row.Id == song.Id);
            created.File = new SongFile
            {
                Path = audioPath,
                Codec = "flac",
                Container = "flac",
                Size = 1,
                QualityId = SongSeed.HighQuality,
                SourceType = sourceType,
                DurationMs = 330_026,
            };
            await context.SaveChangesAsync();
        }

        return song;
    }

    /// <summary>Adds a command row the way the queue would, so a download counts as active for the song.</summary>
    public async Task AddActiveDownloadAsync(long songId, string videoId = "u7K72X4eo_s", CommandStatus status = CommandStatus.Started)
    {
        await using var context = NewContext();
        context.Commands.Add(new CommandRecord
        {
            Name = MusicVideoDownloadCommandHandler.CommandName,
            Body = MusicVideoCommandBodies.Write(new MusicVideoCommandBodies.Download(songId, videoId, MusicVideoFoundBy.Manual)),
            Status = status,
        });
        await context.SaveChangesAsync();
    }

    /// <summary>Back-dates (or forward-dates) the moment a song was added, which the scheduled task orders by.</summary>
    public async Task SetAddedAsync(long songId, DateTimeOffset when)
    {
        await using var context = NewContext();
        var utc = when.UtcDateTime;
        await context.Songs
            .Where(song => song.Id == songId)
            .ExecuteUpdateAsync(set => set.SetProperty(song => song.CreatedAt, utc));
    }

    /// <summary>Adds a row for a song.</summary>
    public async Task<MusicVideo> AddRowAsync(
        long songId,
        MusicVideoStatus status,
        string videoId = "u7K72X4eo_s",
        string? path = null,
        MusicVideoFoundBy foundBy = MusicVideoFoundBy.MusicBrainz)
    {
        await using var context = NewContext();
        var row = new MusicVideo { SongId = songId, Status = status, YouTubeVideoId = videoId, FoundBy = foundBy, Path = path };
        context.MusicVideos.Add(row);
        await context.SaveChangesAsync();

        return row;
    }

    public void Dispose()
    {
        Temp.Dispose();
        Database.Dispose();
    }
}
