using Wondarr.Core.Sources;
using Wondarr.Sources.YouTube;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// The provider over stubbed client and runner: the query strategy (ISRC first, then songs, then
/// videos only when allowed), the availability toggle and the grab-handle round-trip. No network, no
/// process — the interfaces are stubbed.
/// </summary>
public class YouTubeSourceProviderTests
{
    private static readonly string[] NoIsrcs = [];

    [Fact]
    public async Task Availability_follows_the_toggle()
    {
        var provider = Build(options: new YouTubeOptions { Enabled = false });

        var (available, reason) = await provider.GetAvailabilityAsync(CancellationToken.None);

        available.Should().BeFalse();
        reason.Should().Be("YouTube is not enabled");

        provider = Build(options: new YouTubeOptions { Enabled = true });

        (available, reason) = await provider.GetAvailabilityAsync(CancellationToken.None);

        available.Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public async Task Searches_isrc_then_songs()
    {
        // The ISRC query answers with the card: the verified result, so the songs query never runs.
        var card = Card("HzdD8kbDzZA", "Take on Me", ["a-ha"], 226_000);
        var search = new StubInnertubeClient()
            .Respond(InnertubeSearchFilter.None, new InnertubeSearchResult(
                "USWB19901214",
                InnertubeSearchFilter.None,
                TopResult: card,
                [card]))
            .Respond(InnertubeSearchFilter.Songs, Shelf("4D7u5KF7SP8", "Get Lucky", ["Daft Punk"], 370_000));

        var provider = Build(search: search, isrcs: ["USWB19901214"]);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        search.Requests.Should().ContainSingle().Which.Filter.Should().Be(InnertubeSearchFilter.None);
        search.Requests[0].Query.Should().Be("USWB19901214");
        result.Candidates.Should().ContainSingle().Which.RemotePath.Should().Be("HzdD8kbDzZA");
        result.Queries.Should().Equal("USWB19901214");
    }

    [Fact]
    public async Task Runs_the_songs_query_when_the_isrc_query_returned_nothing()
    {
        var search = new StubInnertubeClient()
            .Respond(InnertubeSearchFilter.None, InnertubeSearchResult.Empty("USWB19901214", InnertubeSearchFilter.None))
            .Respond(InnertubeSearchFilter.Songs, Shelf("4D7u5KF7SP8", "Get Lucky", ["Daft Punk"], 370_000));

        var provider = Build(search: search, isrcs: ["USWB19901214"]);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        search.Requests.Should().HaveCount(2);
        search.Requests[0].Filter.Should().Be(InnertubeSearchFilter.None);
        search.Requests[1].Filter.Should().Be(InnertubeSearchFilter.Songs);
        search.Requests[1].Query.Should().Contain("Aphex Twin").And.Contain("Alpha");
        result.Candidates.Should().ContainSingle().Which.RemotePath.Should().Be("4D7u5KF7SP8");
    }

    [Fact]
    public async Task Videos_query_only_when_allowed()
    {
        // allow_videos false: the videos query is never sent.
        var search = new StubInnertubeClient()
            .Respond(InnertubeSearchFilter.None, InnertubeSearchResult.Empty("USWB19901214", InnertubeSearchFilter.None))
            .Respond(InnertubeSearchFilter.Songs, InnertubeSearchResult.Empty("aphex twin alpha", InnertubeSearchFilter.Songs))
            .Respond(InnertubeSearchFilter.Videos, Shelf("CCHdMIEGaaM", "Alpha", ["convar HUN"], 248_000));

        var provider = Build(search: search, isrcs: NoIsrcs, options: new YouTubeOptions { Enabled = true, AllowVideos = false });

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        // No ISRCs were served, so exactly one query ran: the songs shelf. The videos query is never
        // sent while allow_videos is false.
        search.Requests.Should().ContainSingle().Which.Filter.Should().Be(InnertubeSearchFilter.Songs);
        result.Candidates.Should().BeEmpty();

        // allow_videos true: the videos query runs after songs.
        var allowedSearch = new StubInnertubeClient()
            .Respond(InnertubeSearchFilter.Songs, InnertubeSearchResult.Empty("aphex twin alpha", InnertubeSearchFilter.Songs))
            .Respond(InnertubeSearchFilter.Videos, Shelf("CCHdMIEGaaM", "Alpha", ["convar HUN"], 248_000));

        provider = Build(search: allowedSearch, isrcs: NoIsrcs, options: new YouTubeOptions { Enabled = true, AllowVideos = true });

        result = await provider.SearchAsync(Request(), CancellationToken.None);

        allowedSearch.Requests.Should().HaveCount(2);
        allowedSearch.Requests[1].Filter.Should().Be(InnertubeSearchFilter.Videos);
        result.Candidates.Should().ContainSingle().Which.RemotePath.Should().Be("CCHdMIEGaaM");
    }

    [Fact]
    public async Task Honours_the_pool_callback_between_queries()
    {
        // The songs query's pool is good enough: the videos query never runs even though videos are allowed.
        var search = new StubInnertubeClient()
            .Respond(InnertubeSearchFilter.Songs, Shelf("4D7u5KF7SP8", "Get Lucky", ["Daft Punk"], 370_000))
            .Respond(InnertubeSearchFilter.Videos, Shelf("CCHdMIEGaaM", "Alpha", ["convar HUN"], 248_000));

        var provider = Build(search: search, isrcs: NoIsrcs, options: new YouTubeOptions { Enabled = true, AllowVideos = true });

        var request = Request();
        request = request with { IsPoolGoodEnough = _ => true };

        var result = await provider.SearchAsync(request, CancellationToken.None);

        search.Requests.Should().ContainSingle().Which.Filter.Should().Be(InnertubeSearchFilter.Songs);
        result.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task A_disabled_source_answers_no_candidates_with_the_reason()
    {
        var provider = Build(options: new YouTubeOptions { Enabled = false });

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        result.Candidates.Should().BeEmpty();
        result.Message.Should().Be("YouTube is not enabled");
    }

    [Fact]
    public async Task The_grab_round_trips_through_the_handle()
    {
        var downloads = new StubYtDlpRunner();
        var provider = Build(downloads: downloads, options: new YouTubeOptions { DownloadsDir = Path.GetTempPath() });

        var candidate = YouTubeCandidateMapper.Map(
            Card("4D7u5KF7SP8", "Get Lucky", ["Daft Punk"], 370_000),
            "aphex twin alpha");

        var handle = await provider.GrabAsync(candidate, "queue-1", CancellationToken.None);

        handle.SourceType.Should().Be(SourceTypes.YouTube);
        downloads.Downloads.Should().ContainSingle().Which.DestinationDir
            .Should().EndWith("queue-1");

        var status = await provider.GetStatusAsync(handle, CancellationToken.None);

        status.State.Should().Be(DownloadState.Completed);
        status.CompletedPath.Should().NotBeNull();

        // Nothing to cancel: the download finished inside GrabAsync.
        await provider.CancelAsync(handle, CancellationToken.None);
    }

    private static SongSearchRequest Request() => new(
        1,
        "Alpha",
        "Aphex Twin",
        ["Aphex Twin"],
        "Selected Ambient Works",
        200_000,
        Wondarr.Core.Metadata.VersionFlags.None);

    private static InnertubeResult Card(
        string videoId,
        string title,
        string[] artists,
        int? durationMs = null) => new(
        videoId,
        title,
        artists,
        Album: null,
        durationMs,
        MusicVideoType: InnertubeVideoTypes.ArtTrack,
        IsExplicit: false);

    private static InnertubeSearchResult Shelf(
        string videoId,
        string title,
        string[] artists,
        int? durationMs = null) => new(
        "aphex twin alpha",
        InnertubeSearchFilter.Songs,
        TopResult: null,
        [new InnertubeResult(
            videoId,
            title,
            artists,
            Album: null,
            durationMs,
            MusicVideoType: InnertubeVideoTypes.ArtTrack,
            IsExplicit: false)]);

    private static YouTubeSourceProvider Build(
        StubInnertubeClient? search = null,
        StubYtDlpRunner? downloads = null,
        IReadOnlyList<string>? isrcs = null,
        YouTubeOptions? options = null) => new(
        search ?? new StubInnertubeClient(),
        downloads ?? new StubYtDlpRunner(),
        new StubIsrcSource(isrcs ?? []),
        new TestOptionsMonitor<YouTubeOptions>(options ?? new YouTubeOptions { Enabled = true }),
        NullLogger<YouTubeSourceProvider>.Instance);
}

/// <summary>Serves one scripted response per filter, in order; records every request.</summary>
internal sealed class StubInnertubeClient : IInnertubeClient
{
    private readonly Queue<(InnertubeSearchFilter Filter, InnertubeSearchResult Result)> _responses = new();

    public List<InnertubeSearchRequest> Requests { get; } = [];

    public StubInnertubeClient Respond(InnertubeSearchFilter filter, InnertubeSearchResult result)
    {
        _responses.Enqueue((filter, result));
        return this;
    }

    public Task<InnertubeSearchResult> SearchAsync(InnertubeSearchRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException($"Nothing was scripted for '{request.Query}'.");
        }

        return Task.FromResult(_responses.Dequeue().Result);
    }
}

/// <summary>Records every download; answers with the file it was asked to place.</summary>
internal sealed class StubYtDlpRunner : IYtDlpRunner
{
    public List<(string VideoId, string DestinationDir)> Downloads { get; } = [];

    public Task<YtDlpDownload> DownloadAsync(string videoId, string destinationDir, CancellationToken cancellationToken)
    {
        Downloads.Add((videoId, destinationDir));

        var path = Path.Combine(destinationDir, $"{videoId}.opus");
        var directory = Directory.CreateDirectory(destinationDir);
        File.WriteAllText(path, "opus");

        return Task.FromResult(new YtDlpDownload(videoId, path, "opus"));
    }

    public Task<YtDlpFormats> ProbeFormatsAsync(string videoId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The provider never probes formats.");
}

/// <summary>Serves the ISRCs it was built with.</summary>
internal sealed class StubIsrcSource(IReadOnlyList<string> isrcs) : ISongIsrcSource
{
    public Task<IReadOnlyList<string>> GetIsrcsAsync(long songId, CancellationToken cancellationToken) =>
        Task.FromResult(isrcs);
}
