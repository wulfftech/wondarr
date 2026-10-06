// The search strategy is ported from spotDL (https://github.com/spotDL/spotify-downloader), the
// ISRC-first query and its verified-result early return, MIT.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// The YouTube source behind <see cref="ISourceProvider"/>: the InnerTube search strategy (the ISRC
/// query first, then the songs shelf, then the videos shelf when the source rule allows them) and the
/// yt-dlp grab. A grab is finished when <see cref="GrabAsync"/> returns — yt-dlp runs to completion
/// inside it — so a YouTube handle is always a completed download.
/// </summary>
public sealed partial class YouTubeSourceProvider : ISourceProvider
{
    /// <summary>The grab handle is Wondarr's own JSON, in the web casing the queue's columns use.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IInnertubeClient _search;
    private readonly IYtDlpRunner _downloads;
    private readonly ISongIsrcSource _isrcs;
    private readonly IOptionsMonitor<YouTubeOptions> _options;
    private readonly ILogger<YouTubeSourceProvider> _logger;

    /// <summary>Initialises a new instance of the <see cref="YouTubeSourceProvider"/> class.</summary>
    /// <param name="search">The InnerTube client: the only way a search is sent.</param>
    /// <param name="downloads">The yt-dlp runner: the only way a video is fetched.</param>
    /// <param name="isrcs">Reads the song's ISRCs, the identity the first query asks for.</param>
    /// <param name="options">The <c>youtube</c> section of <c>config.yml</c>.</param>
    /// <param name="logger">One Warning per query that failed.</param>
    public YouTubeSourceProvider(
        IInnertubeClient search,
        IYtDlpRunner downloads,
        ISongIsrcSource isrcs,
        IOptionsMonitor<YouTubeOptions> options,
        ILogger<YouTubeSourceProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(isrcs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _search = search;
        _downloads = downloads;
        _isrcs = isrcs;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string SourceType => SourceTypes.YouTube;

    /// <inheritdoc />
    public Task<(bool Available, string? Reason)> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        return Task.FromResult<(bool, string?)>(options.Enabled
            ? (true, null)
            : (false, "YouTube is disabled (Settings → Sources)"));
    }

    /// <inheritdoc />
    public async Task<SourceSearchResult> SearchAsync(SongSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (available, reason) = await GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);

        if (!available)
        {
            return new SourceSearchResult([], [], reason);
        }

        var options = _options.CurrentValue;
        var queries = new List<string>();
        var candidates = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? firstError = null;
        var submitted = 0;

        // The ISRC query first: the catalogue's own identity, so the card it returns is the song
        // (spotDL's verified-result early return). One query per ISRC, stopping at the first card.
        foreach (var isrc in await _isrcs.GetIsrcsAsync(request.SongId, cancellationToken).ConfigureAwait(false))
        {
            if (submitted >= options.SearchLimit)
            {
                break;
            }

            var (result, error) = await RunAsync(
                new InnertubeSearchRequest(isrc, InnertubeSearchFilter.None),
                cancellationToken).ConfigureAwait(false);

            if (result is null)
            {
                firstError ??= error;
                continue;
            }

            submitted++;
            queries.Add(isrc);
            Add(result, candidates, seen);

            if (result.TopResult is not null)
            {
                // The card is the verified result: the song's own identity answered, so the shelf
                // queries would only add noise around it.
                return new SourceSearchResult(candidates, queries);
            }
        }

        // The songs shelf: the catalogue's Art Tracks, the only results a grab may use by default.
        var text = string.Join(" ", request.MainArtists.Count > 0 ? request.MainArtists : [request.ArtistCredit]);

        if (submitted < options.SearchLimit)
        {
            var (songs, error) = await RunAsync(
                new InnertubeSearchRequest($"{text} - {request.Title}", InnertubeSearchFilter.Songs),
                cancellationToken).ConfigureAwait(false);

            if (songs is null)
            {
                firstError ??= error;
            }
            else
            {
                submitted++;
                queries.Add(songs.Query);
                Add(songs, candidates, seen);

                if (request.IsPoolGoodEnough?.Invoke(candidates) == true)
                {
                    return new SourceSearchResult(candidates, queries);
                }
            }
        }

        // The videos shelf: official videos and user uploads, only when the source rule allows them.
        if (options.AllowVideos && submitted < options.SearchLimit)
        {
            var (videos, error) = await RunAsync(
                new InnertubeSearchRequest($"{text} - {request.Title}", InnertubeSearchFilter.Videos),
                cancellationToken).ConfigureAwait(false);

            if (videos is null)
            {
                firstError ??= error;
            }
            else
            {
                submitted++;
                queries.Add(videos.Query);
                Add(videos, candidates, seen);
            }
        }

        if (queries.Count == 0 && firstError is not null)
        {
            return new SourceSearchResult([], [], firstError);
        }

        return new SourceSearchResult(candidates, queries);
    }

    /// <inheritdoc />
    public async Task<GrabHandle> GrabAsync(Candidate candidate, string destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!string.Equals(candidate.SourceType, SourceTypes.YouTube, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Not a YouTube candidate (was {candidate.SourceType})", nameof(candidate));
        }

        if (string.IsNullOrWhiteSpace(candidate.RemotePath))
        {
            throw new ArgumentException("A YouTube candidate needs the video id it was found as", nameof(candidate));
        }

        var destinationDir = Path.Combine(_options.CurrentValue.DownloadsDir, destination);

        // yt-dlp creates the folders of its output template itself; this also makes the folder exist
        // for a status poll that races a download that never produced a file.
        Directory.CreateDirectory(destinationDir);

        var download = await _downloads
            .DownloadAsync(candidate.RemotePath, destinationDir, cancellationToken)
            .ConfigureAwait(false);

        return new GrabHandle(
            SourceTypes.YouTube,
            JsonSerializer.Serialize(new YouTubeGrab(download.VideoId, destinationDir), SerializerOptions));
    }

    /// <inheritdoc />
    public Task<DownloadStatus> GetStatusAsync(GrabHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var grab = Read(handle);

        // yt-dlp finished inside GrabAsync, so the handle is always a completed download: the file is
        // where the runner left it, and the size is what is on disk.
        var file = new FileInfo(Path.Combine(grab.DestinationDir, $"{grab.VideoId}.opus"));
        var size = file.Exists ? file.Length : 0;

        return Task.FromResult(new DownloadStatus(
            DownloadState.Completed,
            1,
            size,
            size,
            null,
            null,
            file.FullName));
    }

    /// <inheritdoc />
    public Task CancelAsync(GrabHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);

        // There is nothing to cancel: the download ran to completion inside GrabAsync, and the queue
        // tracker only cancels grabs that are still in flight.
        _ = Read(handle);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs one query. A null result means the query failed: a failed query never contributes
    /// candidates, and the strategy moves on to the next one with the failure's message.
    /// </summary>
    private async Task<(InnertubeSearchResult? Result, string? Error)> RunAsync(
        InnertubeSearchRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await _search.SearchAsync(request, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogQueryFailed(_logger, request.Query, exception);
            return (null, exception.Message);
        }
    }

    /// <summary>Adds one search's results, the card first, dropping the repeats across queries.</summary>
    private static void Add(InnertubeSearchResult result, List<Candidate> candidates, HashSet<string> seen)
    {
        IEnumerable<InnertubeResult> results = result.Results;

        if (result.TopResult is { } card)
        {
            results = [card, .. result.Results];
        }

        foreach (var candidate in YouTubeCandidateMapper.Map(results, result.Query))
        {
            if (seen.Add(candidate.BlocklistKey))
            {
                candidates.Add(candidate);
            }
        }
    }

    /// <summary>Reads the grab Wondarr's own handle carries.</summary>
    private static YouTubeGrab Read(GrabHandle handle)
    {
        if (!string.Equals(handle.SourceType, SourceTypes.YouTube, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Not a YouTube grab handle (was {handle.SourceType})", nameof(handle));
        }

        return JsonSerializer.Deserialize<YouTubeGrab>(handle.Value, SerializerOptions)
            ?? throw new ArgumentException("The grab handle does not carry a YouTube grab", nameof(handle));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "YouTube query {Query} failed; trying the next one")]
    private static partial void LogQueryFailed(ILogger logger, string query, Exception exception);

    /// <summary>What a YouTube grab handle carries: nothing live, only what the finished download is.</summary>
    /// <param name="VideoId">The video id that was fetched.</param>
    /// <param name="DestinationDir">The absolute per-grab folder the file landed in.</param>
    private sealed record YouTubeGrab(string VideoId, string DestinationDir);
}
