using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Blocklisting;
using Wondarr.Core.Domain;
using Wondarr.Core.History;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Messaging;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Wondarr.Core.Tagging;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Core.Tests.Searching;
using Wondarr.Core.Verification;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// One test's worth of importing: a real SQLite database, a fake clock, a real download file in a temp
/// folder, and the real blocklist, reputation and history services next to fakes for everything that
/// touches the network, the tags or the library.
/// </summary>
internal sealed class ImportTestHost : IAsyncDisposable
{
    /// <summary>The album context the happy path is filed under, as the default library renders it.</summary>
    public const string DaftPunkRelativePath = "Daft Punk/Random Access Memories/08 - Get Lucky";

    private readonly SqliteTestDatabase _database;
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly string _downloads;

    private ImportTestHost(
        SqliteTestDatabase database,
        ServiceProvider services,
        IServiceScope scope,
        FakeTimeProvider time,
        string downloads)
    {
        _database = database;
        _services = services;
        _scope = scope;
        _downloads = downloads;
        Time = time;
    }

    /// <summary>The temp database the host runs on.</summary>
    public SqliteTestDatabase Database => _database;

    /// <summary>The fake clock every service reads; it starts at 2026-09-29T12:00Z.</summary>
    public FakeTimeProvider Time { get; }

    /// <summary>The search limits the import reads.</summary>
    public SearchOptions Options { get; private init; } = new();

    /// <summary>The import pipeline under test.</summary>
    public IImportService Import => _scope.ServiceProvider.GetRequiredService<IImportService>();

    /// <summary>The scoped database context the services share.</summary>
    public WondarrDbContext Context => _scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

    /// <summary>The real peer reputation and ignore list.</summary>
    public ISoulseekUserService Users => _scope.ServiceProvider.GetRequiredService<ISoulseekUserService>();

    /// <summary>The real blocklist.</summary>
    public IBlocklistService Blocklist => _scope.ServiceProvider.GetRequiredService<IBlocklistService>();

    /// <summary>The real song lifecycle log.</summary>
    public IHistoryService History => _scope.ServiceProvider.GetRequiredService<IHistoryService>();

    /// <summary>The scripted verifier.</summary>
    public FakeDownloadVerifier Verifier { get; private init; } = null!;

    /// <summary>The scripted tag writer.</summary>
    public FakeTagWriter TagWriter { get; private init; } = null!;

    /// <summary>The scripted placer.</summary>
    public FakeFilePlacer Placer { get; private init; } = null!;

    /// <summary>The scripted cover fetcher.</summary>
    public FakeCoverFetcher Covers { get; private init; } = null!;

    /// <summary>The scripted search service, which records the next-attempt asks.</summary>
    public FakeSongSearchService Search { get; private init; } = null!;

    /// <summary>The event aggregator, which records what was published.</summary>
    public RecordingEventAggregator Events { get; private init; } = null!;

    /// <summary>Builds a host over a fresh database and a fresh downloads folder.</summary>
    public static async Task<ImportTestHost> CreateAsync(Action<SearchOptions>? configure = null)
    {
        var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        await database.MigrateAsync(time);

        var options = new SearchOptions();
        configure?.Invoke(options);

        var downloads = Path.Combine(Path.GetTempPath(), "wondarr-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloads);

        var verifier = new FakeDownloadVerifier();
        var tagWriter = new FakeTagWriter();
        var placer = new FakeFilePlacer();
        var covers = new FakeCoverFetcher();
        var search = new FakeSongSearchService();
        var events = new RecordingEventAggregator();

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IOptionsMonitor<SearchOptions>>(new TestOptionsMonitor<SearchOptions>(options));
        services.AddSingleton<IDownloadVerifier>(verifier);
        services.AddSingleton<ITagWriter>(tagWriter);
        services.AddSingleton<IFilePlacer>(placer);
        services.AddSingleton<ICoverFetcher>(covers);
        services.AddSingleton<ISongSearchService>(search);
        services.AddSingleton<IEventAggregator>(events);
        services.AddDbContext<WondarrDbContext>(builder => builder
            .UseSqlite($"Data Source={database.FilePath}")
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IBlocklistService, BlocklistService>();
        services.AddScoped<ISoulseekUserService, SoulseekUserService>();
        services.AddScoped<IHistoryService, HistoryService>();
        services.AddScoped<IImportService, ImportService>();

        var built = services.BuildServiceProvider();
        var scope = built.CreateScope();

        return new ImportTestHost(database, built, scope, time, downloads)
        {
            Options = options,
            Verifier = verifier,
            TagWriter = tagWriter,
            Placer = placer,
            Covers = covers,
            Search = search,
            Events = events,
        };
    }

    /// <summary>Inserts one complete grab, and the download it produced.</summary>
    public async Task<ImportSeed> SeedAsync(Action<ImportSeedOptions>? configure = null)
    {
        var options = new ImportSeedOptions();
        configure?.Invoke(options);

        await using var context = _database.CreateContext(Time);

        var artist = new Artist
        {
            Name = options.ArtistName,
            SortName = options.ArtistName,
            MbArtistId = options.MbArtistId,
        };

        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var song = new Song
        {
            Title = options.Title,
            ArtistCredit = options.ArtistCredit ?? options.ArtistName,
            PrimaryArtistId = artist.Id,
            MbRecordingId = options.MbRecordingId,
            DurationMs = options.DurationMs,
            VersionFlags = [.. options.VersionFlags],
            Monitored = true,
            QualityProfileId = options.QualityProfileId,
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

        var albumKey = options.AlbumKey ?? string.Concat("album-", song.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        context.AlbumContexts.Add(new AlbumContext
        {
            SongId = song.Id,
            Kind = options.Kind,
            AlbumTitle = options.AlbumTitle,
            AlbumArtist = options.AlbumArtist,
            AlbumKey = albumKey,
            MbReleaseId = options.MbReleaseId,
            MbReleaseGroupId = options.MbReleaseGroupId,
            TrackNo = options.TrackNo,
            DiscNo = options.DiscNo,
            TotalTracks = options.TotalTracks,
            Date = options.Date,
            OriginalDate = options.OriginalDate,
            CoverUrl = options.CoverUrl,
            IsVariousArtists = options.VariousArtists,
        });

        long? songFileId = null;

        if (options.CurrentFilePath is { } currentPath)
        {
            var file = new SongFile
            {
                SongId = song.Id,
                Path = currentPath,
                Size = 10_000_000,
                Codec = "mp3",
                Container = "mp3",
                QualityId = options.CurrentFileQualityId,
                SourceType = SourceTypes.Soulseek,
                ImportedAt = Time.GetUtcNow().UtcDateTime.AddDays(-1),
            };

            context.SongFiles.Add(file);
            await context.SaveChangesAsync();

            songFileId = file.Id;
        }

        var run = new SearchRun
        {
            SongId = song.Id,
            Trigger = options.Trigger,
            StartedAt = Time.GetUtcNow().UtcDateTime,
        };

        context.SearchRuns.Add(run);
        await context.SaveChangesAsync();

        var remotePath = string.Concat("Music\\Daft Punk\\", options.DownloadName);

        var candidate = new CandidateRecord
        {
            SearchRunId = run.Id,
            SongId = song.Id,
            SourceType = options.SourceType,
            BlocklistKey = BlocklistKeys.Soulseek(options.Provider, remotePath),
            DisplayName = options.DownloadName,
            RemotePath = remotePath,
            Provider = options.Provider,
            QualityId = 36,
            SizeBytes = 30_000_000,
            DurationMs = 369_000,
            Score = 900,
            Accepted = true,
            Grabbed = true,
        };

        context.Candidates.Add(candidate);
        await context.SaveChangesAsync();

        // Exactly the folder the item's Destination names: the source downloads into <downloads>/wondarr/<guid>.
        var directory = Path.Combine(_downloads, "wondarr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var downloadPath = Path.Combine(directory, options.DownloadName);

        if (options.CreateDownload)
        {
            await File.WriteAllBytesAsync(downloadPath, new byte[64]);
        }

        var item = new QueueItem
        {
            SongId = song.Id,
            CandidateId = candidate.Id,
            SearchRunId = run.Id,
            SourceType = options.SourceType,
            Destination = "wondarr/" + Path.GetFileName(directory),
            State = options.State,
            Progress = 0.5,
            Attempt = options.Attempt,
            SizeBytes = 30_000_000,
            DownloadPath = options.WithDownloadPath ? downloadPath : null,
        };

        context.QueueItems.Add(item);
        await context.SaveChangesAsync();

        return new ImportSeed(
            song.Id,
            item.Id,
            run.Id,
            candidate.Id,
            remotePath,
            candidate.BlocklistKey,
            directory,
            downloadPath,
            songFileId);
    }

    /// <summary>Points one queue item at a download path of the test's choosing.</summary>
    public async Task SetDownloadPathAsync(long queueItemId, string? downloadPath)
    {
        await using var context = _database.CreateContext(Time);

        var item = await context.QueueItems.SingleAsync(entry => entry.Id == queueItemId);
        item.DownloadPath = downloadPath;

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Inserts a <c>song_file</c> row for a song through its own context — behind the back of the
    /// import under test, whose save then collides with the one-file-per-song index.
    /// </summary>
    public async Task InsertSongFileBehindAsync(long songId, string path)
    {
        await using var context = _database.CreateContext(Time);

        context.SongFiles.Add(new SongFile
        {
            SongId = songId,
            Path = path,
            Codec = "flac",
            Container = "flac",
            QualityId = 36,
            SourceType = SourceTypes.Soulseek,
            ImportedAt = Time.GetUtcNow().UtcDateTime,
        });

        await context.SaveChangesAsync();
    }

    /// <summary>Inserts a second song that already holds the given recording id.</summary>
    public async Task<long> SeedOtherSongAsync(string mbRecordingId)
    {
        await using var context = _database.CreateContext(Time);

        var artist = new Artist { Name = "Aphex Twin", SortName = "Aphex Twin" };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var song = new Song
        {
            Title = "Xtal",
            ArtistCredit = artist.Name,
            PrimaryArtistId = artist.Id,
            MbRecordingId = mbRecordingId,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };

        context.Songs.Add(song);
        await context.SaveChangesAsync();

        return song.Id;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _services.DisposeAsync();
        _database.Dispose();

        if (Directory.Exists(_downloads))
        {
            Directory.Delete(_downloads, recursive: true);
        }
    }
}

/// <summary>What one <see cref="ImportTestHost.SeedAsync"/> produced.</summary>
/// <param name="SongId">The song's id.</param>
/// <param name="QueueItemId">The queue item's id, which is what the import is handed.</param>
/// <param name="SearchRunId">The run the grab belongs to.</param>
/// <param name="CandidateId">The grabbed candidate's id.</param>
/// <param name="RemotePath">The candidate's remote path.</param>
/// <param name="BlocklistKey">The candidate's blocklist key.</param>
/// <param name="DownloadDirectory">The folder the source downloaded into.</param>
/// <param name="DownloadPath">The downloaded file.</param>
/// <param name="SongFileId">The file the song already held, when it had one.</param>
internal sealed record ImportSeed(
    long SongId,
    long QueueItemId,
    long SearchRunId,
    long CandidateId,
    string RemotePath,
    string BlocklistKey,
    string DownloadDirectory,
    string DownloadPath,
    long? SongFileId);

/// <summary>The knobs one seeded grab can be given.</summary>
internal sealed class ImportSeedOptions
{
    /// <summary>The track title.</summary>
    public string Title { get; set; } = "Get Lucky";

    /// <summary>The artist name.</summary>
    public string ArtistName { get; set; } = "Daft Punk";

    /// <summary>The artist's MusicBrainz id.</summary>
    public string? MbArtistId { get; set; } = "056e4f3e-d505-4dad-8ec1-d04f521cbb56";

    /// <summary>The display credit, when it differs from the artist name.</summary>
    public string? ArtistCredit { get; set; }

    /// <summary>The album title.</summary>
    public string AlbumTitle { get; set; } = "Random Access Memories";

    /// <summary>The album artist.</summary>
    public string AlbumArtist { get; set; } = "Daft Punk";

    /// <summary>The folder grouping key; a synthetic UUID unless a test says otherwise.</summary>
    public string? AlbumKey { get; set; }

    /// <summary>The real release MBID, when the context has one.</summary>
    public string? MbReleaseId { get; set; }

    /// <summary>The release-group MBID.</summary>
    public string? MbReleaseGroupId { get; set; } = "4b3d5e6a-1c2b-4a5f-8e9d-0a1b2c3d4e5f";

    /// <summary>The album context's shape.</summary>
    public AlbumContextKind Kind { get; set; } = AlbumContextKind.Album;

    /// <summary>Whether the album artist is "Various Artists".</summary>
    public bool VariousArtists { get; set; }

    /// <summary>The album context date.</summary>
    public string? Date { get; set; } = "2013-05-17";

    /// <summary>The recording's first release date.</summary>
    public string? OriginalDate { get; set; }

    /// <summary>The track number.</summary>
    public int? TrackNo { get; set; } = 8;

    /// <summary>The disc number.</summary>
    public int? DiscNo { get; set; }

    /// <summary>The track total.</summary>
    public int? TotalTracks { get; set; } = 13;

    /// <summary>The recording's MusicBrainz id, when it has one.</summary>
    public string? MbRecordingId { get; set; }

    /// <summary>The song's known duration.</summary>
    public int? DurationMs { get; set; } = 369_000;

    /// <summary>The song's version flags, as wire names.</summary>
    public List<string> VersionFlags { get; } = [];

    /// <summary>The quality profile the song is judged against.</summary>
    public long QualityProfileId { get; set; } = SeedData.StandardProfileId;

    /// <summary>The cover URL the album context carries.</summary>
    public string? CoverUrl { get; set; }

    /// <summary>The file the song already holds, when it holds one.</summary>
    public string? CurrentFilePath { get; set; }

    /// <summary>The quality of the file the song already holds.</summary>
    public long CurrentFileQualityId { get; set; } = 29;

    /// <summary>The queue item's state.</summary>
    public QueueItemState State { get; set; } = QueueItemState.Completed;

    /// <summary>The downloaded file's name, extension included.</summary>
    public string DownloadName { get; set; } = "08 - Get Lucky.flac";

    /// <summary>Which automatic attempt this grab is.</summary>
    public int Attempt { get; set; } = 1;

    /// <summary>What started the search run.</summary>
    public SearchTrigger Trigger { get; set; } = SearchTrigger.Automatic;

    /// <summary>The source the grab went through.</summary>
    public string SourceType { get; set; } = SourceTypes.Soulseek;

    /// <summary>The peer that served the file.</summary>
    public string Provider { get; set; } = "peer";

    /// <summary>Whether the downloaded file exists on disk.</summary>
    public bool CreateDownload { get; set; } = true;

    /// <summary>Whether the item carries a download path at all.</summary>
    public bool WithDownloadPath { get; set; } = true;
}

/// <summary>A verifier that returns one scripted verdict and records what it was asked.</summary>
internal sealed class FakeDownloadVerifier : IDownloadVerifier
{
    /// <summary>The verdict every call returns.</summary>
    public VerificationResult Result { get; set; } = Passed();

    /// <summary>Every request the verifier was handed.</summary>
    public List<VerificationRequest> Requests { get; } = [];

    /// <summary>A FLAC the probe would have measured.</summary>
    public static MediaInfo Flac(long sizeBytes = 30_000_000) =>
        new("flac", "flac", 1000, 44_100, 16, 2, 369_000, true, sizeBytes);

    /// <summary>A passing verdict at the given measured quality.</summary>
    public static VerificationResult Passed(long qualityId = 36, MediaInfo? media = null) =>
        new(
            VerificationOutcome.Passed,
            "Fingerprint matches the song's MusicBrainz recording (score 0.95)",
            media ?? Flac(),
            qualityId,
            "acoustid-1",
            0.95,
            null,
            null,
            true);

    /// <inheritdoc />
    public Task<VerificationResult> VerifyAsync(VerificationRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        return Task.FromResult(Result);
    }
}

/// <summary>A tag writer that records the tag sets it was handed.</summary>
internal sealed class FakeTagWriter : ITagWriter
{
    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether the write succeeds.</summary>
    public bool Success { get; set; } = true;

    /// <summary>Why it failed, when it does.</summary>
    public string? Error { get; set; } = "The file is not readable audio.";

    /// <summary>The fields a successful write reports.</summary>
    public IReadOnlyDictionary<string, string> Written { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["Title"] = "Get Lucky" };

    /// <summary>Every write the writer was asked for.</summary>
    public List<CapturedTagWrite> Writes { get; } = [];

    /// <inheritdoc />
    public Task<TagWriteResult> WriteAsync(string path, TagSet tags, CancellationToken cancellationToken = default)
    {
        Writes.Add(new CapturedTagWrite(path, tags));

        return Task.FromResult(Success
            ? new TagWriteResult(true, null, Written)
            : new TagWriteResult(false, Error, Empty));
    }
}

/// <summary>One tag write a <see cref="FakeTagWriter"/> was asked for.</summary>
/// <param name="Path">The file it was asked to tag.</param>
/// <param name="Tags">The tags it was asked to write.</param>
internal sealed record CapturedTagWrite(string Path, TagSet Tags);

/// <summary>A placer that computes where the file would go and records the request.</summary>
internal sealed class FakeFilePlacer : IFilePlacer
{
    /// <summary>Whether the placement succeeds.</summary>
    public bool Success { get; set; } = true;

    /// <summary>Why it failed, when it does.</summary>
    public string? Error { get; set; } = "The library root is not writable.";

    /// <summary>Run just before a placement is reported, to move the world under the import's feet.</summary>
    public Func<PlacementRequest, Task>? OnPlaceAsync { get; set; }

    /// <summary>Every placement the placer was asked for.</summary>
    public List<PlacementRequest> Requests { get; } = [];

    /// <summary>Where the file would land, derived from the request the caller made.</summary>
    public static string TargetOf(PlacementRequest request) =>
        request.LibraryRoot.TrimEnd('/')
        + "/"
        + request.RelativePathWithoutExtension
        + "."
        + request.Extension;

    /// <inheritdoc />
    public async Task<PlacementResult> PlaceAsync(PlacementRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (OnPlaceAsync is { } hook)
        {
            await hook(request);
        }

        return Success
            ? new PlacementResult(true, TargetOf(request), null, request.Mode, null)
            : new PlacementResult(false, null, null, null, Error);
    }
}

/// <summary>A cover fetcher that returns scripted bytes and records the URLs it was asked for.</summary>
internal sealed class FakeCoverFetcher : ICoverFetcher
{
    /// <summary>A minimal JPEG, which is what the fetcher hands back for any URL.</summary>
    public byte[] Bytes { get; set; } = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    /// <summary>Every URL the fetcher was handed.</summary>
    public List<string?> Urls { get; } = [];

    /// <inheritdoc />
    public Task<byte[]?> FetchAsync(string? url, CancellationToken cancellationToken)
    {
        Urls.Add(url);

        return Task.FromResult(url is null ? null : Bytes);
    }
}

/// <summary>A search service that records the next-attempt asks and returns scripted answers.</summary>
internal sealed class FakeSongSearchService : ISongSearchService
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
        throw new NotSupportedException("The import does not search.");

    /// <inheritdoc />
    public Task<long> GrabCandidateAsync(long candidateRecordId, int attempt, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The import grabs through the next attempt only.");
}

/// <summary>An event aggregator that records everything published through it.</summary>
internal sealed class RecordingEventAggregator : IEventAggregator
{
    /// <summary>Every event published, in order.</summary>
    public List<IEvent> Published { get; } = [];

    /// <summary>The published events of one type, in order.</summary>
    public IReadOnlyList<TEvent> Of<TEvent>()
        where TEvent : IEvent => [.. Published.OfType<TEvent>()];

    /// <inheritdoc />
    public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : IEvent
    {
        Published.Add(message);

        return Task.CompletedTask;
    }
}
