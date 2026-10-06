using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
using FluentAssertions;
using Xunit;

namespace Wondarr.Core.Tests.Searching;

/// <summary>
/// The bot-check backoff (P4-04): a grab that failed because the source had a bad moment — a bot check,
/// a rate limit — fails the item and stops the run, never re-grabbing in the same run; a grab that
/// failed because the candidate is dead — geo-restricted, private — blocklists the video and tries the
/// next candidate.
/// </summary>
public class BotCheckBackoffTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [Fact]
    public async Task A_retry_later_failure_fails_the_item_and_never_regrabs_in_the_same_run()
    {
        await using var host = await BackoffTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();

        // Two candidates, both bot-checked: the first grab fails, and the run must stop — the second
        // candidate is not tried.
        host.Provider.GrabFailure = new StubGrabFailure(
            "youtube:abc123",
            blocklistCandidate: false,
            "Sign in to confirm you're not a bot");

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

        result.Outcome.Should().Be(SearchOutcome.NoAcceptableCandidate);
        host.Provider.Grabs.Should().ContainSingle("a bot check must not loop into a second grab");

        var items = await host.Context.QueueItems.AsNoTracking().OrderBy(item => item.Id).ToListAsync(Token);
        items.Should().ContainSingle().Which.State.Should().Be(QueueItemState.Failed);
        items[0].Message.Should().Contain("not a bot");

        // No blocklist row: the candidate is not dead, the source just had a bad moment.
        (await host.Context.Blocklist.AsNoTracking().ToListAsync(Token)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_blocklist_failure_blocklists_the_video_and_tries_the_next_candidate()
    {
        await using var host = await BackoffTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();

        // The first candidate is geo-restricted (dead); the second is fine.
        host.Provider.GrabFailure = new StubGrabFailure(
            "youtube:abc123",
            blocklistCandidate: true,
            "The uploader has not made this video available in your country");

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

        result.Message.Should().Contain("Grabbed", result.Message ?? "");
        result.Outcome.Should().Be(SearchOutcome.Grabbed);
        host.Provider.Grabs.Should().HaveCount(2, "a dead video is skipped for the next candidate");

        var blocked = await host.Context.Blocklist.AsNoTracking().ToListAsync(Token);
        blocked.Should().ContainSingle();
        blocked[0].BlocklistKey.Should().Be("youtube:abc123");
        blocked[0].Reason.Should().Contain("not made this video available");
    }
}

/// <summary>
/// One test's worth of searching against a YouTube-shaped source whose grab can be told to fail with a
/// classified failure.
/// </summary>
internal sealed class BackoffTestHost : IAsyncDisposable
{
    private readonly SearchTestHost _inner;
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;

    private BackoffTestHost(
        SearchTestHost inner,
        ServiceProvider services,
        IServiceScope scope,
        BackoffFakeProvider provider,
        WondarrDbContext context)
    {
        _inner = inner;
        _services = services;
        _scope = scope;
        Provider = provider;
        Context = context;
    }

    public BackoffFakeProvider Provider { get; }

    public WondarrDbContext Context { get; }

    public ISongSearchService Search => _scope.ServiceProvider.GetRequiredService<ISongSearchService>();

    public Task<long> SeedSongAsync() => _inner.SeedSongAsync();

    public static async Task<BackoffTestHost> CreateAsync()
    {
        var inner = await SearchTestHost.CreateAsync();
        var provider = new BackoffFakeProvider();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(inner.Time);
        services.AddSingleton(inner.Monitor);
        services.AddSingleton<DecisionEngine>();
        services.AddSingleton<ISourceProvider>(provider);
        services.AddDbContext<WondarrDbContext>(builder => builder
            .UseSqlite($"Data Source={inner.Database.FilePath}")
            .UseSnakeCaseNamingConvention());
        services.AddScoped<ISearchRunService, SearchRunService>();
        services.AddScoped<IQueueService, QueueService>();
        services.AddScoped<IBlocklistService, BlocklistService>();
        services.AddScoped<ISoulseekUserService, SoulseekUserService>();
        services.AddScoped<IHistoryService, HistoryService>();
        services.AddSingleton<IEventAggregator, EventAggregator>();
        services.AddScoped<ISongSearchService, SongSearchService>();
        var built = services.BuildServiceProvider();
        var scope = built.CreateScope();

        return new BackoffTestHost(
            inner,
            built,
            scope,
            provider,
            scope.ServiceProvider.GetRequiredService<WondarrDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _services.DisposeAsync();
        await _inner.DisposeAsync();
    }
}

/// <summary>
/// A YouTube-shaped source with two candidates: the first (best) one can be told to fail its grab with
/// a classified failure; the second always succeeds.
/// </summary>
internal sealed class BackoffFakeProvider : ISourceProvider
{
    public List<GrabbedCandidate> Grabs { get; } = [];

    public StubGrabFailure? GrabFailure { get; set; }

    public string SourceType => SourceTypes.YouTube;

    public Task<(bool Available, string? Reason)> GetAvailabilityAsync(CancellationToken cancellationToken) =>
        Task.FromResult<(bool, string?)>((true, null));

    public Task<SourceSearchResult> SearchAsync(SongSearchRequest request, CancellationToken cancellationToken)
    {
        // Two Art-Track-shaped candidates: the first is the one a grab is told to fail on.
        var first = YouTubeCandidate("youtube:abc123", "abc123");
        var second = YouTubeCandidate("youtube:def456", "def456");

        return Task.FromResult(new SourceSearchResult([first, second], ["aphex twin alpha"]));
    }

    public Task<GrabHandle> GrabAsync(Candidate candidate, string destination, CancellationToken cancellationToken)
    {
        Grabs.Add(new GrabbedCandidate(candidate, destination));

        if (GrabFailure is { } failure && candidate.BlocklistKey == failure.Key)
        {
            GrabFailure = null;

            // Thrown the way the real runner throws YtDlpException: the search service's wrapper adds
            // the GrabFailedException layer, and matches the classification on the inner exception.
            throw failure;
        }

        return Task.FromResult(new GrabHandle(SourceType, $"{{\"videoId\":\"{candidate.RemotePath}\"}}"));
    }

    public Task<DownloadStatus> GetStatusAsync(GrabHandle handle, CancellationToken cancellationToken) =>
        Task.FromResult(new DownloadStatus(DownloadState.Completed, 1, 1));

    public Task CancelAsync(GrabHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;

    private static Candidate YouTubeCandidate(string key, string videoId) => new()
    {
        SourceType = SourceTypes.YouTube,
        BlocklistKey = key,
        DisplayName = "Alpha",
        RemotePath = videoId,
        Provider = "Daft Punk",
        Parsed = new ParsedName(
            "Aphex Twin",
            "Alpha",
            "Selected Ambient Works",
            1,
            VersionFlags.None,
            [],
            VersionFlags.None,
            [],
            false),
        DurationMs = 200_000,
        Extension = "opus",
        QualityId = 28,
        Availability = new CandidateAvailability(FreeUploadSlot: true, QueueLength: 0),
        Query = "aphex twin alpha",
    };
}

/// <summary>A grab failure carrying the classification the search service matches on.</summary>
internal sealed class StubGrabFailure : Exception, ISourceGrabFailure
{
    /// <summary>The blocklist key of the candidate that failed.</summary>
    public StubGrabFailure(string key, bool blocklistCandidate, string message)
        : base(message)
    {
        Key = key;
        BlocklistCandidate = blocklistCandidate;
    }

    /// <summary>The blocklist key of the candidate that failed.</summary>
    public string Key { get; }

    /// <summary>Whether the candidate itself is dead.</summary>
    public bool BlocklistCandidate { get; }
}
