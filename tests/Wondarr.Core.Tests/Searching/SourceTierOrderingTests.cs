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
/// The source-tier ordering (MATCHING_ENGINE §6.4): Soulseek first, YouTube only when Soulseek found
/// nothing the engine accepts, torrents last; a manual search fans out over every tier.
/// </summary>
public class SourceTierOrderingTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [Fact]
    public async Task Soulseek_first_youtube_only_when_soulseek_yields_nothing_acceptable()
    {
        // Soulseek answers with one accepted candidate: the YouTube tier is never asked.
        await using var host = await TierTestHost.CreateAsync(soulseekAccepted: true);
        var songId = await host.SeedSongAsync();

        await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);

        host.Soulseek.Requests.Should().ContainSingle();
        host.YouTube.Searches.Should().Be(0, "Soulseek's accepted candidate stops the tier sequence");

        // Soulseek answers with only rejected candidates: the YouTube tier runs.
        await using var second = await TierTestHost.CreateAsync(soulseekAccepted: false);
        songId = await second.SeedSongAsync();

        await second.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: false, Token);

        second.Soulseek.Requests.Should().ContainSingle();
        second.YouTube.Searches.Should().Be(1, "nothing Soulseek found was accepted, so YouTube runs");
    }

    [Fact]
    public async Task Manual_search_fans_out()
    {
        await using var host = await TierTestHost.CreateAsync(soulseekAccepted: true);
        var songId = await host.SeedSongAsync();

        await host.Search.SearchAsync(songId, SearchTrigger.Manual, grab: false, Token);

        host.Soulseek.Requests.Should().ContainSingle();
        host.YouTube.Searches.Should().Be(1, "a manual search shows the user the whole pool");
    }
}

/// <summary>
/// One test's worth of searching with two sources in different tiers: a Soulseek fake and a YouTube
/// fake, both scripted from the test. The search service is built over the inner host's database with
/// the two fakes as its providers.
/// </summary>
internal sealed class TierTestHost : IAsyncDisposable
{
    private readonly SearchTestHost _inner;
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;

    private TierTestHost(
        SearchTestHost inner,
        ServiceProvider services,
        IServiceScope scope,
        TierFakeSoulseek soulseek,
        TierFakeYouTube youtube)
    {
        _inner = inner;
        _services = services;
        _scope = scope;
        Soulseek = soulseek;
        YouTube = youtube;
    }

    public TierFakeSoulseek Soulseek { get; }

    public TierFakeYouTube YouTube { get; }

    public ISongSearchService Search => _scope.ServiceProvider.GetRequiredService<ISongSearchService>();

    public Task<long> SeedSongAsync() => _inner.SeedSongAsync();

    public static async Task<TierTestHost> CreateAsync(bool soulseekAccepted)
    {
        var inner = await SearchTestHost.CreateAsync();
        var soulseek = new TierFakeSoulseek(soulseekAccepted);
        var youtube = new TierFakeYouTube();

        // A second service collection over the same database, with the two tier fakes as its
        // providers — the inner host's own single scripted source is not used for searching.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(inner.Time);
        services.AddSingleton(inner.Monitor);
        services.AddSingleton<DecisionEngine>();
        services.AddSingleton<ISourceProvider>(soulseek);
        services.AddSingleton<ISourceProvider>(youtube);
        services.AddDbContext<WondarrDbContext>(builder => builder
            .UseSqlite($"Data Source={inner.Database.FilePath}")
            .UseSnakeCaseNamingConvention());
        services.AddScoped<ISearchRunService, SearchRunService>();
        services.AddScoped<IQueueService, QueueService>();
        services.AddScoped<IBlocklistService, BlocklistService>();
        services.AddScoped<ISoulseekUserService, SoulseekUserService>();
        services.AddScoped<IHistoryService, HistoryService>();
        services.AddSingleton<IEventAggregator, EventAggregator>();
        services.AddScoped<SlotWaitContext>();
        services.AddSingleton<SlotWaiters>();
        services.AddScoped<ISongSearchService, SongSearchService>();
        var built = services.BuildServiceProvider();
        var scope = built.CreateScope();

        return new TierTestHost(inner, built, scope, soulseek, youtube);
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _services.DisposeAsync();
        await _inner.DisposeAsync();
    }
}

/// <summary>A Soulseek-shaped fake whose search answers with one accepted or one rejected candidate.</summary>
internal sealed class TierFakeSoulseek(bool accepted) : ISourceProvider
{
    public List<SongSearchRequest> Requests { get; } = [];

    public string SourceType => SourceTypes.Soulseek;

    public Task<(bool Available, string? Reason)> GetAvailabilityAsync(CancellationToken cancellationToken) =>
        Task.FromResult<(bool, string?)>((true, null));

    public Task<SourceSearchResult> SearchAsync(SongSearchRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        // One candidate: a clean fast peer when accepted, a live radio edit when not.
        var candidate = accepted
            ? SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac")
            : SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha (Live).flac", VersionFlags.Live);

        return Task.FromResult(new SourceSearchResult([candidate], ["aphex twin alpha"]));
    }

    public Task<GrabHandle> GrabAsync(Candidate candidate, string destination, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The tier tests never grab.");

    public Task<DownloadStatus> GetStatusAsync(GrabHandle handle, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The tier tests never grab.");

    public Task CancelAsync(GrabHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A YouTube-shaped fake that records whether it was searched.</summary>
internal sealed class TierFakeYouTube : ISourceProvider
{
    public int Searches { get; private set; }

    public string SourceType => SourceTypes.YouTube;

    public Task<(bool Available, string? Reason)> GetAvailabilityAsync(CancellationToken cancellationToken) =>
        Task.FromResult<(bool, string?)>((true, null));

    public Task<SourceSearchResult> SearchAsync(SongSearchRequest request, CancellationToken cancellationToken)
    {
        Searches++;
        return Task.FromResult(new SourceSearchResult([], ["aphex twin alpha"]));
    }

    public Task<GrabHandle> GrabAsync(Candidate candidate, string destination, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The tier tests never grab.");

    public Task<DownloadStatus> GetStatusAsync(GrabHandle handle, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The tier tests never grab.");

    public Task CancelAsync(GrabHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;
}
