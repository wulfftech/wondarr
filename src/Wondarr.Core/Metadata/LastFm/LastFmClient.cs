using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Logging;

namespace Wondarr.Core.Metadata.LastFm;

/// <summary>How a Last.fm call ended.</summary>
public enum LastFmStatus
{
    /// <summary>Last.fm answered and <see cref="LastFmResult{T}.Value"/> is what it knows.</summary>
    Ok,

    /// <summary>Last.fm answered that it does not know the track or artist (error 6).</summary>
    NotFound,

    /// <summary>No API key is configured, so no request was made.</summary>
    NotConfigured,

    /// <summary>The API key was rejected (errors 10 and 26).</summary>
    InvalidKey,

    /// <summary>Last.fm is throttling this caller (error 29, or a 429); calls are held back until the wait is over.</summary>
    RateLimited,

    /// <summary>Last.fm could not be reached, was too slow, or answered with a server error.</summary>
    Unavailable,

    /// <summary>Any other failure; <see cref="LastFmResult{T}.Message"/> says what.</summary>
    Error,
}

/// <summary>The answer to one Last.fm call.</summary>
/// <typeparam name="T">What a successful call carries.</typeparam>
/// <param name="Status">What happened.</param>
/// <param name="Value">The data; <see langword="null"/> unless <see cref="LastFmStatus.Ok"/>.</param>
/// <param name="Message">A message safe to show and to log — never the API key.</param>
public sealed record LastFmResult<T>(LastFmStatus Status, T? Value, string? Message)
    where T : class
{
    /// <summary>Gets a value indicating whether Last.fm answered with data.</summary>
    public bool Ok => Status == LastFmStatus.Ok;
}

/// <summary>What a key check found.</summary>
/// <param name="Status">What happened; <see cref="LastFmStatus.Ok"/> and <see cref="LastFmStatus.NotFound"/> both mean the key was accepted.</param>
/// <param name="Message">A sentence the Settings page can show.</param>
public sealed record LastFmKeyCheck(LastFmStatus Status, string Message)
{
    /// <summary>Gets a value indicating whether Last.fm accepted the key.</summary>
    public bool Accepted => Status is LastFmStatus.Ok or LastFmStatus.NotFound;
}

/// <summary>What Last.fm knows about a track (<c>track.getInfo</c>).</summary>
/// <param name="Url">The track's Last.fm page.</param>
/// <param name="Listeners">How many people have listened to it.</param>
/// <param name="Playcount">How often it has been played.</param>
/// <param name="Tags">The top tag names, most used first (at most five).</param>
/// <param name="WikiSummary">The wiki summary as plain text, without Last.fm's trailing "Read more" link.</param>
/// <param name="ArtistName">The artist as Last.fm spells it.</param>
public sealed record LastFmTrackInfo(
    string? Url,
    long? Listeners,
    long? Playcount,
    IReadOnlyList<string> Tags,
    string? WikiSummary,
    string? ArtistName);

/// <summary>What Last.fm knows about an artist (<c>artist.getInfo</c>).</summary>
/// <param name="Name">The artist's name.</param>
/// <param name="Url">The artist's Last.fm page.</param>
/// <param name="BioSummary">The biography summary as plain text.</param>
/// <param name="Listeners">How many people listen to the artist.</param>
public sealed record LastFmArtistInfo(string Name, string? Url, string? BioSummary, long? Listeners);

/// <summary>One track Last.fm lists as similar (<c>track.getSimilar</c>).</summary>
/// <param name="Artist">The artist.</param>
/// <param name="Title">The title.</param>
/// <param name="Url">The track's Last.fm page.</param>
/// <param name="Match">How similar, from 0 to 1.</param>
/// <param name="Mbid">The recording MBID when Last.fm knows one.</param>
public sealed record LastFmSimilarTrack(string Artist, string Title, string? Url, double? Match, string? Mbid);

/// <summary>Asks Last.fm what it knows about a song.</summary>
public interface ILastFmClient
{
    /// <summary>Gets a value indicating whether an API key is configured. With none, no call is ever made.</summary>
    bool IsConfigured { get; }

    /// <summary>Reads <c>track.getInfo</c>: by MBID when there is one (falling back to the names when Last.fm does not know it), else by artist and title.</summary>
    /// <param name="mbid">The recording MBID, or <see langword="null"/>.</param>
    /// <param name="artist">The artist.</param>
    /// <param name="title">The track title.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<LastFmResult<LastFmTrackInfo>> GetTrackAsync(
        string? mbid,
        string artist,
        string title,
        CancellationToken cancellationToken);

    /// <summary>Reads <c>artist.getInfo</c>.</summary>
    /// <param name="artist">The artist's name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<LastFmResult<LastFmArtistInfo>> GetArtistAsync(string artist, CancellationToken cancellationToken);

    /// <summary>Reads <c>track.getSimilar</c> with a limit of ten.</summary>
    /// <param name="mbid">The recording MBID, or <see langword="null"/>.</param>
    /// <param name="artist">The artist.</param>
    /// <param name="title">The track title.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<LastFmResult<IReadOnlyList<LastFmSimilarTrack>>> GetSimilarAsync(
        string? mbid,
        string artist,
        string title,
        CancellationToken cancellationToken);

    /// <summary>Makes one <c>track.getInfo</c> call with <paramref name="apiKey"/> to see whether Last.fm accepts it. Never cached.</summary>
    /// <param name="apiKey">The key to try.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<LastFmKeyCheck> CheckKeyAsync(string apiKey, CancellationToken cancellationToken);
}

/// <summary>
/// The Last.fm 2.0 client the song page reads through, on the named <c>lastfm</c> <see cref="HttpClient"/>
/// the import lists use: its request-spacing gate keeps the whole process under five requests a
/// second. Every successful answer is kept in memory for 24 hours. A rate limit (error 29, or a 429
/// with its <c>Retry-After</c>) holds every call back until the wait is over instead of asking again.
/// A call takes five seconds at most. The API key travels in the query, as Last.fm's own clients send
/// it, so no URL, header or exception text is ever logged or returned.
/// </summary>
public sealed partial class LastFmClient : ILastFmClient
{
    /// <summary>How many similar tracks are asked for.</summary>
    public const int SimilarLimit = 10;

    /// <summary>How many tags are kept.</summary>
    public const int TagLimit = 5;

    /// <summary>A track Last.fm certainly knows, for the key check.</summary>
    private const string CheckArtist = "Cher";

    private const string CheckTrack = "Believe";

    private const int NotFoundCode = 6;
    private const int RateLimitCode = 29;
    private const int MaxCacheEntries = 2000;

    private readonly IHttpClientFactory _factory;
    private readonly IOptionsMonitor<LastFmOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ISecretRegistry _secrets;
    private readonly ILogger<LastFmClient> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly object _backOffSync = new();

    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    /// <summary>Initialises a new instance of the <see cref="LastFmClient"/> class.</summary>
    /// <param name="factory">Builds the named <c>lastfm</c> client, which carries the spacing gate.</param>
    /// <param name="options">The API key.</param>
    /// <param name="timeProvider">The clock the cache and the back-off run on; tests drive it with <c>FakeTimeProvider</c>.</param>
    /// <param name="secrets">Told about a key that is only being tried, so no sink can print it.</param>
    /// <param name="logger">Logs failures at Debug by status; never a key or a URL.</param>
    public LastFmClient(
        IHttpClientFactory factory,
        IOptionsMonitor<LastFmOptions> options,
        TimeProvider timeProvider,
        ISecretRegistry secrets,
        ILogger<LastFmClient> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);

        _factory = factory;
        _options = options;
        _timeProvider = timeProvider;
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>Gets how long one call may take.</summary>
    public static TimeSpan CallTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets how long an answer is kept in memory.</summary>
    public static TimeSpan CacheTtl { get; } = TimeSpan.FromHours(24);

    /// <summary>Gets how long calls are held back after a rate limit that named no wait.</summary>
    public static TimeSpan DefaultBackOff { get; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets the longest back-off a <c>Retry-After</c> can ask for.</summary>
    public static TimeSpan MaxBackOff { get; } = TimeSpan.FromHours(1);

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.CurrentValue.ApiKey);

    /// <inheritdoc />
    public async Task<LastFmResult<LastFmTrackInfo>> GetTrackAsync(
        string? mbid,
        string artist,
        string title,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(mbid))
        {
            var byMbid = await CallAsync("track.getInfo", Query("mbid", mbid.Trim()), ReadTrack, null, true, cancellationToken)
                .ConfigureAwait(false);

            if (byMbid.Status != LastFmStatus.NotFound)
            {
                return byMbid;
            }
        }

        return await CallAsync("track.getInfo", NamesQuery(artist, title), ReadTrack, null, true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<LastFmResult<LastFmArtistInfo>> GetArtistAsync(string artist, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artist);

        return CallAsync(
            "artist.getInfo",
            Query("artist", artist.Trim()) + "&autocorrect=1",
            ReadArtist,
            null,
            true,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<LastFmResult<IReadOnlyList<LastFmSimilarTrack>>> GetSimilarAsync(
        string? mbid,
        string artist,
        string title,
        CancellationToken cancellationToken)
    {
        var limit = "&limit=" + SimilarLimit.ToString(CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(mbid))
        {
            var byMbid = await CallAsync(
                "track.getSimilar",
                Query("mbid", mbid.Trim()) + limit,
                ReadSimilar,
                null,
                true,
                cancellationToken).ConfigureAwait(false);

            if (byMbid.Status != LastFmStatus.NotFound)
            {
                return byMbid;
            }
        }

        return await CallAsync(
            "track.getSimilar",
            NamesQuery(artist, title) + limit,
            ReadSimilar,
            null,
            true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LastFmKeyCheck> CheckKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var key = apiKey.Trim();

        // The key may not be stored yet, so the redactor has not been told about it.
        _secrets.Register(key);

        var result = await CallAsync("track.getInfo", NamesQuery(CheckArtist, CheckTrack), ReadTrack, key, false, cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            LastFmStatus.Ok or LastFmStatus.NotFound => new LastFmKeyCheck(result.Status, "Last.fm accepted the key."),
            LastFmStatus.InvalidKey => new LastFmKeyCheck(result.Status, "Last.fm rejected the key."),
            LastFmStatus.RateLimited => new LastFmKeyCheck(result.Status, "Last.fm is rate limiting this server; try again in a minute."),
            _ => new LastFmKeyCheck(result.Status, result.Message ?? "Last.fm answered something Wondarr could not use."),
        };
    }

    /// <summary>
    /// Last.fm's HTML as plain text: the trailing "Read more on Last.fm" anchor is dropped (the page
    /// keeps the track's URL for attribution), every other tag is removed and entities are decoded.
    /// </summary>
    internal static string? PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var withoutMore = ReadMoreLink().Replace(html, string.Empty);
        var withoutTags = AnyTag().Replace(withoutMore, string.Empty);
        var decoded = WebUtility.HtmlDecode(withoutTags);
        var collapsed = Whitespace().Replace(decoded, " ").Trim();

        return collapsed.Length == 0 ? null : collapsed;
    }

    private static string NamesQuery(string artist, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artist);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        return Query("artist", artist.Trim()) + "&" + Query("track", title.Trim()) + "&autocorrect=1";
    }

    private static string Query(string name, string value) => name + "=" + Uri.EscapeDataString(value);

    /// <summary>The message with the key removed; <see langword="null"/> when there is none.</summary>
    private static string? Clean(string? message, string apiKey) =>
        string.IsNullOrWhiteSpace(message)
            ? null
            : message.Replace(apiKey, "(removed)", StringComparison.Ordinal).Trim();

    private static LastFmTrackInfo? ReadTrack(JsonElement root)
    {
        if (!root.TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tags = new List<string>();

        if (track.TryGetProperty("toptags", out var top) && top.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in Items(top, "tag"))
            {
                if (Text(tag, "name") is { } name && tags.Count < TagLimit)
                {
                    tags.Add(name);
                }
            }
        }

        string? wiki = null;

        if (track.TryGetProperty("wiki", out var wikiElement) && wikiElement.ValueKind == JsonValueKind.Object)
        {
            wiki = PlainText(Text(wikiElement, "summary"));
        }

        var artist = track.TryGetProperty("artist", out var artistElement) && artistElement.ValueKind == JsonValueKind.Object
            ? Text(artistElement, "name")
            : null;

        return new LastFmTrackInfo(
            Text(track, "url"),
            Count(track, "listeners"),
            Count(track, "playcount"),
            tags,
            wiki,
            artist);
    }

    private static LastFmArtistInfo? ReadArtist(JsonElement root)
    {
        if (!root.TryGetProperty("artist", out var artist) || artist.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = Text(artist, "name");

        if (name is null)
        {
            return null;
        }

        string? bio = null;
        long? listeners = null;

        if (artist.TryGetProperty("bio", out var bioElement) && bioElement.ValueKind == JsonValueKind.Object)
        {
            bio = PlainText(Text(bioElement, "summary"));
        }

        if (artist.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
        {
            listeners = Count(stats, "listeners");
        }

        return new LastFmArtistInfo(name, Text(artist, "url"), bio, listeners);
    }

    private static IReadOnlyList<LastFmSimilarTrack>? ReadSimilar(JsonElement root)
    {
        if (!root.TryGetProperty("similartracks", out var similar) || similar.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tracks = new List<LastFmSimilarTrack>();

        foreach (var track in Items(similar, "track"))
        {
            var title = Text(track, "name");
            var artist = track.TryGetProperty("artist", out var artistElement) && artistElement.ValueKind == JsonValueKind.Object
                ? Text(artistElement, "name")
                : null;

            if (title is null || artist is null || tracks.Count >= SimilarLimit)
            {
                continue;
            }

            tracks.Add(new LastFmSimilarTrack(artist, title, Text(track, "url"), Number(track, "match"), Text(track, "mbid")));
        }

        return tracks;
    }

    /// <summary>The elements of an array property; Last.fm sends a lone object where there is exactly one.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            yield break;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return item;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            yield return value;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    /// <summary>A whole number Last.fm sends as a string, or as a number.</summary>
    private static long? Count(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    [GeneratedRegex(@"<a\b[^>]*>\s*Read more on Last\.fm\s*</a>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadMoreLink();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Last.fm is rate limiting; calls are held back for {Wait}.")]
    private static partial void LogBackOff(ILogger logger, TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Last.fm answered {Status} with a body Wondarr could not read.")]
    private static partial void LogUnreadable(ILogger logger, int status);

    private async Task<LastFmResult<T>> CallAsync<T>(
        string method,
        string query,
        Func<JsonElement, T?> read,
        string? apiKey,
        bool cacheable,
        CancellationToken cancellationToken)
        where T : class
    {
        var key = apiKey ?? _options.CurrentValue.ApiKey;

        if (string.IsNullOrWhiteSpace(key))
        {
            return new LastFmResult<T>(LastFmStatus.NotConfigured, null, null);
        }

        // The key is no part of the cache key: the answer does not depend on who asked.
        var cacheKey = (method + "?" + query).ToLowerInvariant();
        var now = _timeProvider.GetUtcNow();

        if (cacheable && _cache.TryGetValue(cacheKey, out var cached) && cached.Expires > now)
        {
            return (LastFmResult<T>)cached.Result;
        }

        if (BlockedFor(now) is { } wait)
        {
            var seconds = Math.Ceiling(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture);

            return new LastFmResult<T>(
                LastFmStatus.RateLimited,
                null,
                $"Last.fm asked to be left alone for another {seconds} seconds.");
        }

        var result = await SendAsync(method, query, read, key, cancellationToken).ConfigureAwait(false);

        if (cacheable && result.Status is LastFmStatus.Ok or LastFmStatus.NotFound)
        {
            Remember(cacheKey, result);
        }

        return result;
    }

    private async Task<LastFmResult<T>> SendAsync<T>(
        string method,
        string query,
        Func<JsonElement, T?> read,
        string apiKey,
        CancellationToken cancellationToken)
        where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);

        var url = "?method=" + method + "&" + query + "&api_key=" + Uri.EscapeDataString(apiKey) + "&format=json";

        try
        {
            using var http = _factory.CreateClient(ServiceCollectionExtensions.LastFmClientName);
            using var response = await http.GetAsync(new Uri(url, UriKind.Relative), timeout.Token).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            return Interpret(response, body, read, apiKey);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LastFmResult<T>(LastFmStatus.Unavailable, null, "Last.fm did not answer in time.");
        }
        catch (HttpRequestException)
        {
            // The exception text is dropped on purpose: it can carry the request URI, and the URI carries the key.
            return new LastFmResult<T>(LastFmStatus.Unavailable, null, "Last.fm could not be reached.");
        }
        catch (IOException)
        {
            return new LastFmResult<T>(LastFmStatus.Unavailable, null, "Last.fm could not be reached.");
        }
    }

    private LastFmResult<T> Interpret<T>(HttpResponseMessage response, string body, Func<JsonElement, T?> read, string apiKey)
        where T : class
    {
        JsonDocument? document = null;

        try
        {
            try
            {
                document = string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                // A proxy's HTML page, most likely; the status below decides what it means.
            }

            var root = document is { RootElement.ValueKind: JsonValueKind.Object } ? document.RootElement : (JsonElement?)null;
            int? code = null;

            if (root is { } asObject
                && asObject.TryGetProperty("error", out var errorElement)
                && errorElement.ValueKind == JsonValueKind.Number
                && errorElement.TryGetInt32(out var number))
            {
                code = number;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || code == RateLimitCode)
            {
                BackOff(response);

                return new LastFmResult<T>(LastFmStatus.RateLimited, null, "Last.fm is rate limiting this server.");
            }

            if (code is { } failure)
            {
                var message = root is { } failed ? Clean(Text(failed, "message"), apiKey) : null;

                return failure switch
                {
                    NotFoundCode => new LastFmResult<T>(LastFmStatus.NotFound, null, "Last.fm does not know it."),
                    10 or 26 => new LastFmResult<T>(LastFmStatus.InvalidKey, null, "Last.fm rejected the API key."),
                    8 or 11 or 16 => new LastFmResult<T>(LastFmStatus.Unavailable, null, "Last.fm is temporarily unavailable."),
                    _ => new LastFmResult<T>(
                        LastFmStatus.Error,
                        null,
                        message is null
                            ? $"Last.fm answered error {failure.ToString(CultureInfo.InvariantCulture)}."
                            : $"Last.fm: {message}"),
                };
            }

            if ((int)response.StatusCode >= 500)
            {
                return new LastFmResult<T>(LastFmStatus.Unavailable, null, "Last.fm is temporarily unavailable.");
            }

            if (root is { } parsed && response.IsSuccessStatusCode && read(parsed) is { } value)
            {
                return new LastFmResult<T>(LastFmStatus.Ok, value, null);
            }

            LogUnreadable(_logger, (int)response.StatusCode);

            return new LastFmResult<T>(LastFmStatus.Error, null, "Last.fm answered something Wondarr could not read.");
        }
        finally
        {
            document?.Dispose();
        }
    }

    /// <summary>Holds every call back until the wait Last.fm asked for is over.</summary>
    private void BackOff(HttpResponseMessage response)
    {
        var now = _timeProvider.GetUtcNow();
        var wait = DefaultBackOff;
        var retryAfter = response.Headers.RetryAfter;

        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            wait = delta;
        }
        else if (retryAfter?.Date is { } date && date - now > TimeSpan.Zero)
        {
            wait = date - now;
        }

        wait = wait > MaxBackOff ? MaxBackOff : wait;

        lock (_backOffSync)
        {
            var until = now + wait;

            if (until > _blockedUntil)
            {
                _blockedUntil = until;
            }
        }

        LogBackOff(_logger, wait);
    }

    private TimeSpan? BlockedFor(DateTimeOffset now)
    {
        lock (_backOffSync)
        {
            return _blockedUntil > now ? _blockedUntil - now : null;
        }
    }

    private void Remember(string cacheKey, object result)
    {
        var now = _timeProvider.GetUtcNow();

        if (_cache.Count >= MaxCacheEntries)
        {
            foreach (var (key, entry) in _cache)
            {
                if (entry.Expires <= now)
                {
                    _cache.TryRemove(key, out _);
                }
            }

            if (_cache.Count >= MaxCacheEntries)
            {
                _cache.Clear();
            }
        }

        _cache[cacheKey] = new CacheEntry(now + CacheTtl, result);
    }

    private sealed record CacheEntry(DateTimeOffset Expires, object Result);
}
