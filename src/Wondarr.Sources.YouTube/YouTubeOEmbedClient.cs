using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Wondarr.Sources.YouTube;

/// <summary>What YouTube's oEmbed answers for one video: the upload's own title and channel.</summary>
/// <param name="Title">The video's real title.</param>
/// <param name="AuthorName">The uploading channel's name.</param>
/// <param name="AuthorUrl">The uploading channel's URL, or <see langword="null"/>.</param>
public sealed record YouTubeOEmbed(string Title, string AuthorName, string? AuthorUrl);

/// <summary>
/// Reads a video's real title and channel from YouTube's keyless oEmbed endpoint. YouTube Music shows a song's
/// catalogue title and artist for an official video, not the upload's own, so a candidate is checked against this
/// before it may be confident (ADR-0014, amended 2026-11-21).
/// </summary>
public interface IYouTubeOEmbedClient
{
    /// <summary>Looks one video up.</summary>
    /// <param name="videoId">The 11-character video id.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The answer, or <see langword="null"/> when YouTube gave none (the video is gone or private, the request failed or timed out).</returns>
    Task<YouTubeOEmbed?> GetAsync(string videoId, CancellationToken cancellationToken);
}

/// <summary>
/// The oEmbed answers by video id, kept in memory for 24 hours. A singleton, because the typed client is transient.
/// </summary>
public sealed class YouTubeOEmbedCache
{
    /// <summary>How long an answer is kept.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private const int PruneAbove = 2_000;

    private readonly ConcurrentDictionary<string, (YouTubeOEmbed Answer, DateTimeOffset Expires)> _answers = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>Initialises a new instance of the <see cref="YouTubeOEmbedCache"/> class.</summary>
    /// <param name="time">The clock the lifetime runs on.</param>
    public YouTubeOEmbedCache(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        _time = time;
    }

    /// <summary>Gets a kept answer that has not expired.</summary>
    /// <param name="videoId">The video id.</param>
    /// <param name="answer">The answer, when there is one.</param>
    public bool TryGet(string videoId, out YouTubeOEmbed? answer)
    {
        if (_answers.TryGetValue(videoId, out var entry) && entry.Expires > _time.GetUtcNow())
        {
            answer = entry.Answer;

            return true;
        }

        answer = null;

        return false;
    }

    /// <summary>Keeps an answer for <see cref="Lifetime"/>.</summary>
    /// <param name="videoId">The video id.</param>
    /// <param name="answer">The answer.</param>
    public void Set(string videoId, YouTubeOEmbed answer)
    {
        var now = _time.GetUtcNow();

        if (_answers.Count >= PruneAbove)
        {
            foreach (var pair in _answers.Where(pair => pair.Value.Expires <= now))
            {
                _answers.TryRemove(pair.Key, out _);
            }
        }

        _answers[videoId] = (answer, now + Lifetime);
    }
}

/// <inheritdoc />
public sealed partial class YouTubeOEmbedClient : IYouTubeOEmbedClient
{
    /// <summary>The name of the typed <see cref="HttpClient"/>.</summary>
    public const string HttpClientName = "youtube-oembed";

    /// <summary>How long one lookup may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly YouTubeOEmbedCache _cache;
    private readonly ILogger<YouTubeOEmbedClient> _logger;

    /// <summary>Initialises a new instance of the <see cref="YouTubeOEmbedClient"/> class.</summary>
    /// <param name="http">The typed client.</param>
    /// <param name="cache">The answers kept so far.</param>
    /// <param name="logger">The logger.</param>
    public YouTubeOEmbedClient(HttpClient http, YouTubeOEmbedCache cache, ILogger<YouTubeOEmbedClient> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<YouTubeOEmbed?> GetAsync(string videoId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);

        if (_cache.TryGet(videoId, out var kept))
        {
            return kept;
        }

        var watch = Uri.EscapeDataString(YouTubeVideoLinks.WatchUrl(videoId));
        var uri = new Uri($"https://www.youtube.com/oembed?format=json&url={watch}");

        try
        {
            using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogStatus(_logger, videoId, (int)response.StatusCode);

                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("author_name", out var author) || author.ValueKind != JsonValueKind.String)
            {
                LogUnreadable(_logger, videoId);

                return null;
            }

            var authorUrl = root.TryGetProperty("author_url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null;
            var answer = new YouTubeOEmbed(title.GetString()!, author.GetString()!, authorUrl);

            _cache.Set(videoId, answer);

            return answer;
        }
        catch (Exception exception) when (exception is (HttpRequestException or JsonException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            LogFailed(_logger, videoId, exception);

            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The oEmbed lookup of {VideoId} was refused with HTTP {Status}")]
    private static partial void LogStatus(ILogger logger, string videoId, int status);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The oEmbed answer for {VideoId} has no title or author")]
    private static partial void LogUnreadable(ILogger logger, string videoId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The oEmbed lookup of {VideoId} failed")]
    private static partial void LogFailed(ILogger logger, string videoId, Exception exception);
}
