using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Wondarr.Core.ImportLists.ListenBrainz;

/// <summary>One row of a feedback answer: the recording it is about, and how it is named.</summary>
/// <param name="RecordingMbid">The MusicBrainz recording id, when ListenBrainz knows it.</param>
/// <param name="RecordingMsid">The MessyBrainz recording id, when there is no MBID.</param>
/// <param name="TrackMetadata">How the track was named when it was submitted.</param>
internal sealed record ListenBrainzFeedback(
    [property: JsonPropertyName("recording_mbid")] string? RecordingMbid,
    [property: JsonPropertyName("recording_msid")] string? RecordingMsid,
    [property: JsonPropertyName("track_metadata")] ListenBrainzTrackMetadata? TrackMetadata);

/// <summary>How a submitted track was named.</summary>
/// <param name="TrackName">The track title.</param>
/// <param name="ArtistName">The artist credit.</param>
internal sealed record ListenBrainzTrackMetadata(
    [property: JsonPropertyName("track_name")] string? TrackName,
    [property: JsonPropertyName("artist_name")] string? ArtistName);

/// <summary>One page of a feedback answer.</summary>
/// <param name="Count">How many rows this page carries.</param>
/// <param name="TotalCount">How many rows the whole feedback has.</param>
/// <param name="Offset">Where this page starts.</param>
/// <param name="Feedback">The rows, in the order they were loved.</param>
internal sealed record ListenBrainzFeedbackPage(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("total_count")] int TotalCount,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("feedback")] List<ListenBrainzFeedback>? Feedback);

/// <summary>The JSPF envelope of a playlist answer.</summary>
internal sealed record ListenBrainzJspf(
    [property: JsonPropertyName("playlist")] ListenBrainzJspfPlaylist? Playlist);

/// <summary>The playlist itself: its title and its tracks, in playlist order.</summary>
/// <param name="Title">The playlist's title.</param>
/// <param name="Track">The tracks, in playlist order.</param>
internal sealed record ListenBrainzJspfPlaylist(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("track")] List<ListenBrainzJspfTrack>? Track);

/// <summary>One track of a JSPF playlist.</summary>
/// <param name="Title">The track title.</param>
/// <param name="Creator">The artist credit.</param>
/// <param name="Album">The album title.</param>
/// <param name="Duration">The length in milliseconds.</param>
/// <param name="Identifier">The MusicBrainz URLs of the recording, album and artists.</param>
internal sealed record ListenBrainzJspfTrack(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("creator")] string? Creator,
    [property: JsonPropertyName("album")] string? Album,
    [property: JsonPropertyName("duration")] long? Duration,
    [property: JsonPropertyName("identifier")] List<string>? Identifier);

/// <summary>What one ListenBrainz call produced.</summary>
/// <param name="Status">The status ListenBrainz answered.</param>
/// <param name="Body">The body, as text.</param>
/// <param name="Wait">How long to wait before the next page, from the rate-limit headers.</param>
/// <param name="Error">Why the call could not be read, or <see langword="null"/> when it was.</param>
internal sealed record ListenBrainzAnswer(HttpStatusCode Status, string Body, TimeSpan Wait, string? Error)
{
    /// <summary>Gets a value indicating whether the call was read.</summary>
    public bool Ok => Error is null;

    /// <summary>A call that could not be read.</summary>
    /// <param name="error">Why.</param>
    public static ListenBrainzAnswer Failed(string error) => new(default, string.Empty, TimeSpan.Zero, error);
}

/// <summary>Reads a list's settings the way both ListenBrainz providers do.</summary>
internal static class ListenBrainzSettings
{
    /// <summary>The MBID inside a <c>https://listenbrainz.org/playlist/{mbid}</c> link.</summary>
    private static readonly Regex PlaylistInLink = new(
        @"playlist/(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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
    /// Reads the playlist MBID out of what the user typed: a bare MBID, or a link of the site's own
    /// shape, with or without a scheme, a query or anything after the id.
    /// </summary>
    public static string? PlaylistMbid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (Guid.TryParseExact(text, "D", out var bare))
        {
            return bare.ToString("D", CultureInfo.InvariantCulture);
        }

        var match = PlaylistInLink.Match(text);

        return match.Success
        && Guid.TryParseExact(match.Groups["id"].Value, "D", out var id)
            ? id.ToString("D", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// The recording MBID of one value, when it is one: the same id a JSPF track's
    /// <c>identifier</c> URLs carry.
    /// </summary>
    public static string? RecordingMbid(string? value) =>
        value is null ? null : RecordingMbid([value]);

    /// <summary>
    /// The recording MBID among a track's <c>identifier</c> URLs — the first one, because a JSPF
    /// track may also carry its release's and its artists' MusicBrainz URLs.
    /// </summary>
    public static string? RecordingMbid(IEnumerable<string>? identifiers)
    {
        if (identifiers is null)
        {
            return null;
        }

        const string marker = "/recording/";

        foreach (var identifier in identifiers)
        {
            var value = identifier.Trim();
            var at = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

            if (at < 0)
            {
                continue;
            }

            var tail = value[(at + marker.Length)..];
            var end = tail.IndexOf('/', StringComparison.Ordinal);

            if (end >= 0)
            {
                tail = tail[..end];
            }

            if (Guid.TryParseExact(tail, "D", out var id))
            {
                return id.ToString("D", CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    /// <summary>The MessyBrainz id of a row, as it was submitted, when there is one.</summary>
    public static string? RecordingMsid(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One call to ListenBrainz's 1.0 API, and how its answers are read.</summary>
internal static class ListenBrainzFetch
{
    /// <summary>How far past the reset the wait may go: a stuck header must not stall a sync.</summary>
    internal const int MaxWaitSeconds = 60;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Asks for one page, and reads the rate-limit headers of the answer.</summary>
    public static async Task<ListenBrainzAnswer> GetAsync(HttpClient http, string relative, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(relative, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ListenBrainzAnswer.Failed("ListenBrainz did not answer in time.");
        }
        catch (HttpRequestException)
        {
            return ListenBrainzAnswer.Failed("ListenBrainz could not be reached.");
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
                return ListenBrainzAnswer.Failed("ListenBrainz did not answer in time.");
            }
            catch (HttpRequestException)
            {
                return ListenBrainzAnswer.Failed("ListenBrainz could not be reached.");
            }
            catch (IOException)
            {
                return ListenBrainzAnswer.Failed("ListenBrainz could not be reached.");
            }

            // ListenBrainz answers HTTP 200 with an anti-bot page when it is suspicious of the
            // caller: the read is not empty, it is early, and the sync will try again.
            if (IsBrowserCheck(response, body))
            {
                return ListenBrainzAnswer.Failed("ListenBrainz asked for a browser check; the list will be read again later.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return ListenBrainzAnswer.Failed("ListenBrainz is rate limiting Wondarr; the list will be read again later.");
            }

            return response.StatusCode != HttpStatusCode.OK
                ? ListenBrainzAnswer.Failed($"ListenBrainz answered {(int)response.StatusCode}.")
                : new ListenBrainzAnswer(response.StatusCode, body, Wait(response), null);
        }
    }

    /// <summary>Reads a feedback page, or <see langword="null"/> when the body is not one.</summary>
    public static ListenBrainzFeedbackPage? Feedback(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<ListenBrainzFeedbackPage>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a JSPF playlist, or <see langword="null"/> when the body is not one.</summary>
    public static ListenBrainzJspf? Playlist(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<ListenBrainzJspf>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    /// <summary>
    /// How long the next page has to wait: when the quota is spent, <c>X-RateLimit-Reset-In</c>
    /// says in how many seconds it fills again.
    /// </summary>
    private static TimeSpan Wait(HttpResponseMessage response)
    {
        var remaining = Header(response, "X-RateLimit-Remaining");

        if (remaining is not { Length: > 0 } left
            || !int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var free)
            || free != 0)
        {
            return TimeSpan.Zero;
        }

        var reset = Header(response, "X-RateLimit-Reset-In");

        return reset is { Length: > 0 }
        && int.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
        && seconds > 0
            ? TimeSpan.FromSeconds(Math.Min(seconds, MaxWaitSeconds))
            : TimeSpan.Zero;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.NonValidated.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
