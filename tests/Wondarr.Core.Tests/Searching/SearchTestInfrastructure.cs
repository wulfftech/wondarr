using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Blocklisting;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.History;
using Wondarr.Core.Importing;
using Wondarr.Core.Messaging;
using Wondarr.Core.Metadata;
using Wondarr.Core.Persistence;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Testing;

namespace Wondarr.Core.Tests.Searching;

/// <summary>
/// One test's worth of searching: a real SQLite database, a fake clock, one scripted source, and the
/// real services wired the way <c>AddWondarrCore</c> wires them.
/// </summary>
internal sealed class SearchTestHost : IAsyncDisposable
{
    private readonly SqliteTestDatabase _database;
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;

    private SearchTestHost(
        SqliteTestDatabase database,
        ServiceProvider services,
        IServiceScope scope,
        TrackingTimeProvider time,
        FakeSourceProvider provider)
    {
        _database = database;
        _services = services;
        _scope = scope;
        Time = time;
        Provider = provider;
    }

    /// <summary>The temp database the host runs on, for the tests that seed rows of their own.</summary>
    public SqliteTestDatabase Database => _database;

    /// <summary>The search limits the services read.</summary>
    public SearchOptions Options { get; private init; } = new();

    /// <summary>The monitor the services read <see cref="Options"/> through.</summary>
    public IOptionsMonitor<SearchOptions> Monitor { get; private init; } = null!;

    /// <summary>The fake clock every service reads.</summary>
    public TrackingTimeProvider Time { get; }

    /// <summary>The single scripted source the song is searched against.</summary>
    public FakeSourceProvider Provider { get; }

    /// <summary>The scope factory the missing-search handler builds its per-song scopes from.</summary>
    public IServiceScopeFactory Scopes => _services.GetRequiredService<IServiceScopeFactory>();

    /// <summary>
    /// Opens a scope of its own, with its own <c>DbContext</c>: what a second caller of the same
    /// service (another request, another worker) would have.
    /// </summary>
    public IServiceScope CreateScope() => _services.CreateScope();

    /// <summary>The real command queue; only on a host made with <c>withCommands</c>.</summary>
    public Wondarr.Core.Jobs.ICommandQueue Commands => _services.GetRequiredService<Wondarr.Core.Jobs.ICommandQueue>();

    /// <summary>Builds the command executor over this host's services; only on a host made with <c>withCommands</c>.</summary>
    public Wondarr.Core.Jobs.CommandExecutor CreateExecutor() => new(
        _services.GetRequiredService<IServiceScopeFactory>(),
        _services.GetRequiredService<IEventAggregator>(),
        _services.GetRequiredService<System.Threading.Channels.Channel<long>>(),
        Time,
        _services.GetRequiredService<Wondarr.Core.Jobs.ActiveCommands>(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<Wondarr.Core.Jobs.CommandExecutor>.Instance);

    /// <summary>The search-and-grab service under test.</summary>
    public ISongSearchService Search => _scope.ServiceProvider.GetRequiredService<ISongSearchService>();

    /// <summary>What a command lends a search that has to wait for a download slot.</summary>
    public SlotWaitContext SlotWait => _scope.ServiceProvider.GetRequiredService<SlotWaitContext>();

    /// <summary>The queue the grabs land in.</summary>
    public IQueueService Queue => _scope.ServiceProvider.GetRequiredService<IQueueService>();

    /// <summary>The run store the searches are recorded in.</summary>
    public ISearchRunService Runs => _scope.ServiceProvider.GetRequiredService<ISearchRunService>();

    /// <summary>The blocklist the search reads.</summary>
    public IBlocklistService Blocklist => _scope.ServiceProvider.GetRequiredService<IBlocklistService>();

    /// <summary>The song lifecycle log the grabs write to.</summary>
    public IHistoryService History => _scope.ServiceProvider.GetRequiredService<IHistoryService>();

    /// <summary>The Soulseek peer reputation and ignore list the search judges candidates with.</summary>
    public ISoulseekUserService Users => _scope.ServiceProvider.GetRequiredService<ISoulseekUserService>();

    /// <summary>The scoped database context the services share.</summary>
    public WondarrDbContext Context => _scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

    /// <summary>Every <see cref="SongGrabbedEvent"/> the search published, in order.</summary>
    public GrabbedEventRecorder Events => _services.GetRequiredService<GrabbedEventRecorder>();

    /// <summary>Builds a host over a fresh database.</summary>
    /// <param name="configure">Changes the search limits before the services read them.</param>
    /// <param name="withCommands">
    /// Whether the real command queue and the <c>SongSearch</c> handler are registered too, so a test can
    /// run the search through <see cref="CreateExecutor"/> as the application does.
    /// </param>
    public static async Task<SearchTestHost> CreateAsync(Action<SearchOptions>? configure = null, bool withCommands = false)
    {
        var database = new SqliteTestDatabase();
        var time = new TrackingTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        await database.MigrateAsync(time);

        var options = new SearchOptions();
        configure?.Invoke(options);

        var provider = new FakeSourceProvider();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IOptionsMonitor<SearchOptions>>(new TestOptionsMonitor<SearchOptions>(options));
        services.AddSingleton<ISourceProvider>(provider);
        services.AddSingleton<DecisionEngine>();
        services.AddDbContext<WondarrDbContext>(builder => builder
            .UseSqlite($"Data Source={database.FilePath}")
            .UseSnakeCaseNamingConvention());
        services.AddScoped<ISearchRunService, SearchRunService>();
        services.AddScoped<IQueueService, QueueService>();
        services.AddScoped<IBlocklistService, BlocklistService>();
        services.AddScoped<ISoulseekUserService, SoulseekUserService>();
        services.AddScoped<IHistoryService, HistoryService>();
        services.AddSingleton<IEventAggregator, EventAggregator>();
        services.AddSingleton<GrabbedEventRecorder>();
        services.AddSingleton<IHandle<SongGrabbedEvent>>(provider => provider.GetRequiredService<GrabbedEventRecorder>());
        services.AddScoped<SlotWaitContext>();
        services.AddSingleton<SlotWaiters>();
        services.AddScoped<ISongSearchService, SongSearchService>();

        if (withCommands)
        {
            services.AddSingleton(System.Threading.Channels.Channel.CreateUnbounded<long>());
            services.AddSingleton<Wondarr.Core.Jobs.ActiveCommands>();
            services.AddSingleton<Wondarr.Core.Jobs.ICommandQueue, Wondarr.Core.Jobs.CommandQueue>();
            services.AddScoped<Wondarr.Core.Jobs.ICommandHandler, SongSearchCommandHandler>();
        }

        var built = services.BuildServiceProvider();
        var scope = built.CreateScope();

        var host = new SearchTestHost(database, built, scope, time, provider)
        {
            Options = options,
            Monitor = built.GetRequiredService<IOptionsMonitor<SearchOptions>>(),
        };

        return host;
    }

    /// <summary>Inserts one wanted song and returns its id.</summary>
    public async Task<long> SeedSongAsync(
        string title = "Alpha",
        int? durationMs = 200_000,
        IReadOnlyList<string>? versionFlags = null,
        bool monitored = true,
        long? artistId = null,
        AlbumContextKind albumKind = AlbumContextKind.Album)
    {
        await using var context = _database.CreateContext(Time);

        var artist = artistId is { } id
            ? await context.Artists.SingleAsync(row => row.Id == id)
            : new Artist { Name = "Aphex Twin", SortName = "Aphex Twin" };

        if (artistId is null)
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }

        var song = new Song
        {
            Title = title,
            ArtistCredit = artist.Name,
            PrimaryArtistId = artist.Id,
            DurationMs = durationMs,
            VersionFlags = [.. versionFlags ?? []],
            Monitored = monitored,
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
            Kind = albumKind,
            AlbumTitle = albumKind == AlbumContextKind.PseudoSingles ? "Singles" : "Selected Ambient Works",
            AlbumArtist = artist.Name,
            AlbumKey = $"aphex-twin-{song.Id}",
            TrackNo = 1,
        });

        await context.SaveChangesAsync();

        return song.Id;
    }

    /// <summary>
    /// Gives a seeded song a held file, as an import would: the quality the file was matched to,
    /// where it came from, and the stored candidate that produced it (when one did).
    /// </summary>
    public async Task SeedFileAsync(
        long songId,
        long qualityId = 29,
        string sourceType = "soulseek",
        string? sourceRef = null,
        DateTime? importedAt = null)
    {
        await using var context = _database.CreateContext(Time);

        context.SongFiles.Add(new SongFile
        {
            SongId = songId,
            Path = $"/data/music/{songId}.mp3",
            Size = 14_850_000,
            Codec = "mp3",
            Container = "mpeg",
            QualityId = qualityId,
            SourceType = sourceType,
            SourceRef = sourceRef,
            ImportedAt = importedAt ?? Time.GetUtcNow().UtcDateTime,
        });

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Records that a reference file in the given state identifies the song, without any
    /// <c>song_file</c> row: the state a song is in before (or without) its reference file being linked.
    /// </summary>
    /// <param name="songId">The song.</param>
    /// <param name="state">The row's state.</param>
    /// <param name="withSongFile">
    /// Also gives the song the reference <c>song_file</c> a scan leaves while the row is Missing (the
    /// song keeps its file through the grace period).
    /// </param>
    /// <param name="released">
    /// Leaves the row without a song, as a scan does when it lets the song go: a Missing row that no
    /// longer owns anything, and a song with no file.
    /// </param>
    public async Task SeedReferenceFileAsync(
        long songId,
        ReferenceFileState state,
        bool withSongFile = false,
        bool released = false)
    {
        await using var context = _database.CreateContext(Time);

        var library = new ReferenceLibrary { Name = "Music", RootPath = "/reference/music" };
        context.ReferenceLibraries.Add(library);
        await context.SaveChangesAsync();

        var row = new ReferenceFile
        {
            ReferenceLibraryId = library.Id,
            RelativePath = $"{songId}.flac",
            Size = 25_000_000,
            ModifiedAt = Time.GetUtcNow().UtcDateTime,
            LastSeenAt = Time.GetUtcNow().UtcDateTime,
            SongId = released ? null : songId,
            State = state,
        };

        context.ReferenceFiles.Add(row);
        await context.SaveChangesAsync();

        if (withSongFile)
        {
            context.SongFiles.Add(new SongFile
            {
                SongId = songId,
                Path = $"/reference/music/{songId}.flac",
                Size = 25_000_000,
                Codec = "flac",
                Container = "flac",
                QualityId = 29,
                SourceType = SourceTypes.Reference,
                SourceRef = $"{{\"referenceLibraryId\":{library.Id},\"referenceFileId\":{row.Id}}}",
                ImportedAt = Time.GetUtcNow().UtcDateTime,
            });

            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Gives a song the reference file a scan links: an Identified row in a reference library of its own,
    /// and the reference <c>song_file</c> pointing at it, at the given quality.
    /// </summary>
    /// <param name="songId">The song.</param>
    /// <param name="qualityId">The quality the file is rated at; 28 is OPUS-160, a likely YouTube rip.</param>
    /// <param name="replace">Whether the row was settled with Replace in the Match queue.</param>
    /// <param name="libraryReplaces">Whether the library replaces files below the cutoff.</param>
    /// <returns>The reference row's id.</returns>
    public async Task<long> SeedHeldReferenceFileAsync(
        long songId,
        long qualityId = 28,
        bool replace = false,
        bool libraryReplaces = false)
    {
        await using var context = _database.CreateContext(Time);

        var library = new ReferenceLibrary
        {
            Name = $"Reference {songId}",
            RootPath = $"/reference/{songId}",
            ReplaceBelowCutoff = libraryReplaces,
        };
        context.ReferenceLibraries.Add(library);
        await context.SaveChangesAsync();

        var row = new ReferenceFile
        {
            ReferenceLibraryId = library.Id,
            RelativePath = $"{songId}.mp3",
            Size = 7_800_000,
            ModifiedAt = Time.GetUtcNow().UtcDateTime,
            LastSeenAt = Time.GetUtcNow().UtcDateTime,
            SongId = songId,
            State = ReferenceFileState.Identified,
            LikelyYouTubeRip = qualityId == SeedData.Opus160QualityId,
            Replace = replace,
        };
        context.ReferenceFiles.Add(row);
        await context.SaveChangesAsync();

        context.SongFiles.Add(new SongFile
        {
            SongId = songId,
            Path = $"/reference/{songId}/{songId}.mp3",
            Size = 7_800_000,
            Codec = "mp3",
            Container = "mp3",
            SampleRate = 48_000,
            QualityId = qualityId,
            SourceType = SourceTypes.Reference,
            SourceRef = $"{{\"referenceLibraryId\":{library.Id},\"referenceFileId\":{row.Id}}}",
            ImportedAt = Time.GetUtcNow().UtcDateTime,
        });
        await context.SaveChangesAsync();

        return row.Id;
    }

    /// <summary>Inserts one finished run and one stored candidate, and returns the candidate's id.</summary>
    public async Task<long> SeedCandidateAsync(long songId, string scoreBreakdown)
    {
        await using var context = _database.CreateContext(Time);

        var run = new SearchRun
        {
            SongId = songId,
            Trigger = SearchTrigger.Automatic,
            StartedAt = Time.GetUtcNow().UtcDateTime,
            FinishedAt = Time.GetUtcNow().UtcDateTime,
            Outcome = SearchOutcome.Grabbed,
        };

        context.SearchRuns.Add(run);
        await context.SaveChangesAsync();

        var candidate = new CandidateRecord
        {
            SearchRunId = run.Id,
            SongId = songId,
            SourceType = SourceTypes.Soulseek,
            BlocklistKey = "peer\u001fMusic\\held.flac",
            DisplayName = "held.flac",
            RemotePath = "Music\\held.flac",
            Provider = "peer",
            ScoreBreakdown = scoreBreakdown,
        };

        context.Candidates.Add(candidate);
        await context.SaveChangesAsync();

        return candidate.Id;
    }

    /// <summary>A candidate that only needs a path and its version flags named.</summary>
    public static Candidate Candidate(
        string remotePath,
        VersionFlags flags = VersionFlags.None,
        long qualityId = 36,
        string provider = "peer",
        long? sizeBytes = 30_000_000,
        int? durationMs = 200_000,
        string? extension = "flac",
        bool? freeSlot = true,
        long? speed = 2_000_000)
    {
        var fileName = remotePath[(remotePath.LastIndexOf('\\') + 1)..];

        return new Candidate
        {
            SourceType = SourceTypes.Soulseek,
            BlocklistKey = BlocklistKeys.Soulseek(provider, remotePath),
            DisplayName = fileName,
            RemotePath = remotePath,
            Provider = provider,
            Extension = extension,
            QualityId = qualityId,
            SizeBytes = sizeBytes,
            DurationMs = durationMs,
            Availability = new CandidateAvailability(freeSlot, 0, speed),
            Parsed = new ParsedName(
                "Aphex Twin",
                "Alpha",
                "Selected Ambient Works",
                1,
                flags,
                [],
                VersionFlags.None,
                [],
                false),
            Query = "aphex twin alpha",
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _services.DisposeAsync();
        _database.Dispose();
    }
}

/// <summary>Keeps every grab the search announced, so a test can tell "published" from "published twice".</summary>
internal sealed class GrabbedEventRecorder : IHandle<SongGrabbedEvent>
{
    private readonly Lock _gate = new();
    private readonly List<SongGrabbedEvent> _grabs = [];

    /// <summary>The announcements received so far.</summary>
    public IReadOnlyList<SongGrabbedEvent> Grabs
    {
        get
        {
            lock (_gate)
            {
                return [.. _grabs];
            }
        }
    }

    /// <inheritdoc />
    public Task HandleAsync(SongGrabbedEvent message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _grabs.Add(message);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Returns one fixed value; the search reads its options once per call.</summary>
/// <typeparam name="T">The options type.</typeparam>
internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly T _value;

    public TestOptionsMonitor(T value) => _value = value;

    public T CurrentValue => _value;

    public T Get(string? name) => _value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>
/// A source that hands back scripted candidates, records what it was asked and what it grabbed, and
/// can be told to be unavailable or to fail a grab or a search.
/// </summary>
internal sealed class FakeSourceProvider : ISourceProvider
{
    /// <inheritdoc />
    public string SourceType => SourceTypes.Soulseek;

    /// <summary>Whether the source can take a search right now.</summary>
    public bool Available { get; set; } = true;

    /// <summary>Why it cannot, when it cannot.</summary>
    public string UnavailableReason { get; set; } = "Soulseek: not logged in";

    /// <summary>What a search returns.</summary>
    public List<Candidate> Candidates { get; } = [];

    /// <summary>Every request the source was handed, in order.</summary>
    public List<SongSearchRequest> Requests { get; } = [];

    /// <summary>Every grab the source was asked for, in order.</summary>
    public List<GrabbedCandidate> Grabs { get; } = [];

    /// <summary>Runs inside every grab, before it answers; lets a test change the world mid-grab.</summary>
    public Action<Candidate>? OnGrab { get; set; }

    /// <summary>Awaited inside every grab, after <see cref="OnGrab"/>; lets a test hold a grab open the way a long download does.</summary>
    public Func<Candidate, Task>? GrabHook { get; set; }

    /// <summary>The queries a search reports.</summary>
    public List<string> Queries { get; } = ["aphex twin alpha"];

    /// <summary>The note a search reports.</summary>
    public string? Message { get; set; }

    /// <summary>Candidates whose key is in here throw on grab, as an offline peer would.</summary>
    public HashSet<string> FailingKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>Candidates whose key is in here cancel the grab, as a shutdown or a stopped grab would.</summary>
    public HashSet<string> CancellingKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// When set, a search or grab that throws <see cref="OperationCanceledException"/> cancels this source
    /// first, so the exception is the caller's own cancellation and not a timeout.
    /// </summary>
    public CancellationTokenSource? CallerToCancel { get; set; }

    /// <summary>Thrown by the next search when set; cleared after it is thrown once.</summary>
    public Exception? SearchFailure { get; set; }

    /// <summary>Titles whose search throws; every other title searches normally.</summary>
    public HashSet<string> FailingTitles { get; } = new(StringComparer.Ordinal);

    /// <summary>Titles whose search times out the way an <c>HttpClient</c> does: a <see cref="TaskCanceledException"/> while the caller's token is untouched.</summary>
    public HashSet<string> TimingOutTitles { get; } = new(StringComparer.Ordinal);

    /// <summary>Candidates whose key is in here time out on grab, with the caller's token untouched.</summary>
    public HashSet<string> TimingOutKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>The last pool verdict the search asked for, or null when it asked for none.</summary>
    public bool? PoolVerdict { get; private set; }

    /// <summary>
    /// What the early-stop callback is shown, when a test needs the pool at that moment to be smaller
    /// than what the search ends up returning. Null means "the whole result".
    /// </summary>
    public IReadOnlyList<Candidate>? PoolCandidates { get; set; }

    /// <inheritdoc />
    public Task<(bool Available, string? Reason)> GetAvailabilityAsync(CancellationToken cancellationToken) =>
        Task.FromResult((Available, Available ? null : UnavailableReason));

    /// <inheritdoc />
    public Task<SourceSearchResult> SearchAsync(SongSearchRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (SearchFailure is { } failure)
        {
            SearchFailure = null;
            CallerToCancel?.Cancel();
            throw failure;
        }

        if (TimingOutTitles.Contains(request.Title))
        {
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
        }

        if (FailingTitles.Contains(request.Title))
        {
            throw new InvalidOperationException($"The source cannot search for '{request.Title}' right now.");
        }

        PoolVerdict = request.IsPoolGoodEnough?.Invoke(PoolCandidates ?? Candidates);

        return Task.FromResult(new SourceSearchResult(Candidates, Queries, Message));
    }

    /// <inheritdoc />
    public async Task<GrabHandle> GrabAsync(Candidate candidate, string destination, CancellationToken cancellationToken)
    {
        Grabs.Add(new GrabbedCandidate(candidate, destination));
        OnGrab?.Invoke(candidate);

        if (GrabHook is { } hook)
        {
            await hook(candidate);
        }

        if (TimingOutKeys.Contains(candidate.BlocklistKey))
        {
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
        }

        if (CancellingKeys.Contains(candidate.BlocklistKey))
        {
            CallerToCancel?.Cancel();
            throw new OperationCanceledException("The grab was stopped.");
        }

        if (FailingKeys.Contains(candidate.BlocklistKey))
        {
            throw new InvalidOperationException("The peer went offline.");
        }

        return new GrabHandle(SourceType, "{\"username\":\"peer\"}");
    }

    /// <inheritdoc />
    public Task<DownloadStatus> GetStatusAsync(GrabHandle handle, CancellationToken cancellationToken) =>
        Task.FromResult(new DownloadStatus(DownloadState.Queued, 0, 0));

    /// <inheritdoc />
    public Task CancelAsync(GrabHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>One grab a <see cref="FakeSourceProvider"/> was asked for.</summary>
/// <param name="Candidate">What was grabbed.</param>
/// <param name="Destination">The per-grab folder the source was told to download into.</param>
internal sealed record GrabbedCandidate(Candidate Candidate, string Destination);
