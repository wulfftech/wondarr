using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wondarr.Core.Metadata.LastFm;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.ImportLists.LastFm;

/// <summary>One track of a Last.fm loved or top page.</summary>
/// <param name="Name">The track title.</param>
/// <param name="Mbid">The recording MBID, often an empty string.</param>
/// <param name="Duration">The length in whole seconds, as a string; <c>"0"</c> means unknown.</param>
/// <param name="Artist">The artist.</param>
internal sealed record LastFmTrack(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("mbid")] string? Mbid,
    [property: JsonPropertyName("duration")] string? Duration,
    [property: JsonPropertyName("artist")] LastFmArtist? Artist);

/// <summary>The artist of a <see cref="LastFmTrack"/>.</summary>
/// <param name="Name">The artist's name.</param>
/// <param name="Mbid">The artist MBID, often an empty string.</param>
internal sealed record LastFmArtist(string? Name, string? Mbid);

/// <summary>The paging attributes Last.fm puts in <c>@attr</c>.</summary>
/// <param name="Page">The page this answer is, from 1.</param>
/// <param name="TotalPages">How many pages the whole read has.</param>
/// <param name="PerPage">How many rows one page asks for.</param>
/// <param name="Total">How many rows the whole read has.</param>
internal sealed record LastFmAttributes(
    [property: JsonPropertyName("page")] string? Page,
    [property: JsonPropertyName("totalPages")] string? TotalPages,
    [property: JsonPropertyName("perPage")] string? PerPage,
    [property: JsonPropertyName("total")] string? Total);

/// <summary>The <c>track</c> array and <c>@attr</c> of one page, shared by both methods.</summary>
internal sealed record LastFmTrackPage(
    [property: JsonPropertyName("track")] List<LastFmTrack>? Track,
    [property: JsonPropertyName("@attr")] LastFmAttributes? Attributes);

/// <summary>The body of a <c>user.getlovedtracks</c> answer.</summary>
internal sealed record LastFmLovedResponse(
    [property: JsonPropertyName("lovedtracks")] LastFmTrackPage? LovedTracks);

/// <summary>The body of a <c>user.gettoptracks</c> answer.</summary>
internal sealed record LastFmTopResponse(
    [property: JsonPropertyName("toptracks")] LastFmTrackPage? TopTracks);

/// <summary>The body of a Last.fm error answer: <c>{"error": 6, "message": "…"}</c>.</summary>
internal sealed record LastFmError(
    [property: JsonPropertyName("error")] int? Error,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>What one page of a Last.fm read produced.</summary>
/// <param name="Status">The status Last.fm answered.</param>
/// <param name="Body">The body, as text.</param>
/// <param name="Error">Why the page could not be read, or <see langword="null"/> when it was.</param>
internal sealed record LastFmAnswer(HttpStatusCode Status, string Body, string? Error)
{
    /// <summary>Gets a value indicating whether the page was read.</summary>
    public bool Ok => Error is null;

    /// <summary>A page that could not be read.</summary>
    /// <param name="error">Why.</param>
    public static LastFmAnswer Failed(string error) => new(default, string.Empty, error);
}

/// <summary>Reads a list's settings the way both Last.fm providers do.</summary>
internal static class LastFmSettings
{
    /// <summary>The periods <c>user.gettoptracks</c> accepts, in the order the form lists them.</summary>
    public static readonly string[] Periods = ["overall", "7day", "1month", "3month", "6month", "12month"];

    /// <summary>The period a top list reads until the user changes it.</summary>
    public const string DefaultPeriod = "12month";

    /// <summary>The trimmed value of one settings property, when it is a non-empty string.</summary>
    public static string? Setting(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object
        && settings.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && text.Trim().Length > 0
            ? text.Trim()
            : null;

    /// <summary>
    /// The key a list reads with: its own, else Wondarr's <c>lastfm.api_key</c>, else
    /// <see langword="null"/> (the list then reports that it needs one).
    /// </summary>
    public static string? ApiKey(JsonElement settings, IOptionsMonitor<LastFmOptions>? global) =>
        Setting(settings, "apiKey")
        ?? (string.IsNullOrWhiteSpace(global?.CurrentValue.ApiKey) ? null : global!.CurrentValue.ApiKey!.Trim());

    /// <summary>The value of a number settings property, as a JSON number or a string of digits.</summary>
    public static int? Number(JsonElement settings, string name)
    {
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null,
        };
    }
}

/// <summary>One paged call to Last.fm's 2.0 API, and how its answers are read.</summary>
internal static class LastFmFetch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Asks for one page of a user's loved or top tracks. The API key travels in the query, as
    /// Last.fm's own clients send it, so nothing here is logged and no error repeats the URL.
    /// </summary>
    /// <param name="period">The top-tracks period, or <see langword="null"/> for a loved read.</param>
    public static async Task<LastFmAnswer> PageAsync(
        HttpClient http,
        string method,
        string user,
        string apiKey,
        string? period,
        int limit,
        int page,
        ILastFmBackOff? backOff,
        CancellationToken cancellationToken)
    {
        // Last.fm limits the caller, not the list: while the song page's client or another list has
        // been told to wait, this read does not go out.
        if (backOff?.Remaining is not null)
        {
            return LastFmAnswer.Failed(RateLimitedText);
        }

        var periodPart = period is null ? string.Empty : string.Create(
            CultureInfo.InvariantCulture,
            $"&period={Uri.EscapeDataString(period)}");
        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"?method={method}&user={Uri.EscapeDataString(user)}&api_key={Uri.EscapeDataString(apiKey)}"
            + $"{periodPart}&format=json&limit={limit.ToString(CultureInfo.InvariantCulture)}"
            + $"&page={page.ToString(CultureInfo.InvariantCulture)}");

        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LastFmAnswer.Failed("Last.fm did not answer in time.");
        }
        catch (HttpRequestException)
        {
            return LastFmAnswer.Failed("Last.fm could not be reached.");
        }

        using (response)
        {
            string body;

            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return LastFmAnswer.Failed("Last.fm did not answer in time.");
            }
            catch (HttpRequestException)
            {
                return LastFmAnswer.Failed("Last.fm could not be reached.");
            }
            catch (IOException)
            {
                return LastFmAnswer.Failed("Last.fm could not be reached.");
            }

            // A 429 or error 29 is Last.fm asking to be left alone: recorded for every Last.fm caller.
            if (response.StatusCode == HttpStatusCode.TooManyRequests || ErrorCode(body) == 29)
            {
                backOff?.Trip(response);

                return LastFmAnswer.Failed(RateLimitedText);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return LastFmAnswer.Failed(Error(body) ?? $"Last.fm answered {(int)response.StatusCode}.");
            }

            // A web page where JSON was asked for is Last.fm asking for a browser check: the read is
            // not empty, it is early, and the sync will try again.
            if (IsBrowserCheck(response, body))
            {
                return LastFmAnswer.Failed("Last.fm answered a page that is not JSON; the list will be read again later.");
            }

            return Error(body) is { } failure ? LastFmAnswer.Failed(failure) : new LastFmAnswer(response.StatusCode, body, null);
        }
    }

    /// <summary>What a rate-limited read says, whether it was a 429 or error 29.</summary>
    internal const string RateLimitedText = "Last.fm: Rate limit exceeded; the list will be read again later.";

    /// <summary>Reads a loved page, or <see langword="null"/> when the body is not one.</summary>
    public static LastFmTrackPage? Loved(string body) => Read<LastFmLovedResponse>(body)?.LovedTracks;

    /// <summary>Reads a top page, or <see langword="null"/> when the body is not one.</summary>
    public static LastFmTrackPage? Top(string body) => Read<LastFmTopResponse>(body)?.TopTracks;

    /// <summary>How many pages the read has, at least one.</summary>
    public static int TotalPages(LastFmAttributes? attributes) =>
        attributes?.TotalPages is { } text
        && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages)
        && pages > 0
            ? pages
            : 1;

    /// <summary>Whether the answer is a web page rather than the JSON that was asked for.</summary>
    private static bool IsBrowserCheck(HttpResponseMessage response, string body)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;

        if (mediaType is not null && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var first = body.AsSpan().TrimStart();

        return first.Length == 0 || (first[0] != '{' && first[0] != '[');
    }

    /// <summary>The error code of a Last.fm error body, or <see langword="null"/> when it is not one.</summary>
    private static int? ErrorCode(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<LastFmError>(body, Json)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The failure a Last.fm error body maps to, or <see langword="null"/> when it is not one.</summary>
    private static string? Error(string body)
    {
        LastFmError? error;

        try
        {
            error = JsonSerializer.Deserialize<LastFmError>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (error?.Error is not { } code || code == 0)
        {
            return null;
        }

        // 6 is an unknown user, 10 and 26 a bad key, 29 the rate limit. The message Last.fm writes
        // is the human part, and it never carries the key.
        var message = string.IsNullOrWhiteSpace(error.Message) ? $"error {code}" : error.Message.Trim();

        return code == 29 ? RateLimitedText : $"Last.fm: {message}";
    }

    private static T? Read<T>(string body)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
