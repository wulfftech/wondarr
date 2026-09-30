using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Lyrics;

/// <summary>How a lyrics lookup ended.</summary>
public enum LyricsLookupStatus
{
    /// <summary>LRCLIB has the track, with plain lyrics, synced lyrics, or both.</summary>
    Found,

    /// <summary>LRCLIB has the track and says it has no lyrics at all.</summary>
    Instrumental,

    /// <summary>LRCLIB does not have the track.</summary>
    NotFound,

    /// <summary>LRCLIB could not be asked, or answered something that could not be read.</summary>
    Unavailable,
}

/// <summary>What LRCLIB knows about one track.</summary>
/// <param name="Status">What the lookup yielded.</param>
/// <param name="PlainLyrics">The unsynced text, or <see langword="null"/> when there is none.</param>
/// <param name="SyncedLyrics">The LRC text with its time tags, or <see langword="null"/> when there is none.</param>
/// <param name="LrclibId">LRCLIB's own id for the record, or <see langword="null"/>.</param>
public sealed record LyricsLookup(
    LyricsLookupStatus Status,
    string? PlainLyrics,
    string? SyncedLyrics,
    long? LrclibId);

/// <summary>Asks LRCLIB for a song's lyrics (LIBRARY_OUTPUT.md §7.4).</summary>
public interface ILrclibClient
{
    /// <summary>Looks one track up by its title, artist and length.</summary>
    /// <param name="trackName">The track title.</param>
    /// <param name="artistName">The performing artist's name.</param>
    /// <param name="durationSeconds">The track's length in whole seconds; LRCLIB matches it within ±2 s.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The lyrics, or why there are none.</returns>
    Task<LyricsLookup> FindAsync(
        string trackName,
        string artistName,
        int durationSeconds,
        CancellationToken cancellationToken);
}

/// <summary>
/// The LRCLIB client: one <c>api/get</c> per track, falling back to <c>api/search</c> when LRCLIB has no
/// exact record, and never more than one answer per call.
/// </summary>
/// <remarks>
/// <para>
/// Lyrics are a nicety, so nothing here throws: a throttle, a timeout, a dropped connection, a server
/// error and an unreadable body are all <see cref="LyricsLookupStatus.Unavailable"/>, which the caller
/// treats exactly like a miss. Only the caller's own cancellation propagates.
/// </para>
/// <para>
/// There are no retries and no <c>Retry-After</c> waits. The answer to a throttle is "no lyrics this
/// time"; the spacing handler the client is registered with keeps the next track polite.
/// </para>
/// </remarks>
public sealed partial class LrclibClient : ILrclibClient
{
    /// <summary>How far a candidate's length may differ from the file's and still count as the track.</summary>
    private const double DurationToleranceSeconds = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<LrclibClient> _logger;

    /// <summary>Initialises a new instance of the <see cref="LrclibClient"/> class.</summary>
    /// <param name="http">The typed client, carrying the base URL, the timeout and the spacing handler.</param>
    /// <param name="logger">Logs per-lookup detail at Debug; never logs lyrics text.</param>
    public LrclibClient(HttpClient http, ILogger<LrclibClient> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LyricsLookup> FindAsync(
        string trackName,
        string artistName,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackName);
        ArgumentException.ThrowIfNullOrWhiteSpace(artistName);

        var exact = await FetchAsync(
            string.Concat(
                "api/get?track_name=", Uri.EscapeDataString(trackName),
                "&artist_name=", Uri.EscapeDataString(artistName),
                "&duration=", durationSeconds.ToString(CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);

        if (exact.Failed)
        {
            return Unavailable(exact.Reason);
        }

        if (exact.StatusCode == HttpStatusCode.OK)
        {
            var record = ReadRecord(exact.Body);

            if (record is null)
            {
                return Unavailable("the answer could not be read");
            }

            // The exact endpoint matched on title, artist and length; a record with no text either way
            // is a miss for us, exactly as if LRCLIB had not had the track.
            return Map(record) ?? NotFound();
        }

        if (exact.StatusCode != HttpStatusCode.NotFound)
        {
            // Including 429 and 5xx: throttled or broken is "no lyrics this time".
            LogStatus(_logger, (int)exact.StatusCode);

            return Unavailable("the service answered a status that is not a hit");
        }

        var search = await FetchAsync(
            string.Concat(
                "api/search?track_name=", Uri.EscapeDataString(trackName),
                "&artist_name=", Uri.EscapeDataString(artistName)),
            cancellationToken).ConfigureAwait(false);

        if (search.Failed)
        {
            return Unavailable(search.Reason);
        }

        if (search.StatusCode != HttpStatusCode.OK)
        {
            LogStatus(_logger, (int)search.StatusCode);

            return Unavailable("the service answered a status that is not a hit");
        }

        var candidates = ReadRecords(search.Body);

        if (candidates is null)
        {
            return Unavailable("the answer could not be read");
        }

        foreach (var candidate in candidates)
        {
            // The length is the one value a search hit can be checked against: a different recording of
            // the same title (a live take, a remix) has a different one.
            if (candidate.Duration is not { } length
                || Math.Abs(length - durationSeconds) > DurationToleranceSeconds)
            {
                continue;
            }

            if (Map(candidate) is { } lyrics)
            {
                return lyrics;
            }
        }

        return NotFound();
    }

    /// <summary>The lookup for a track LRCLIB does not have.</summary>
    private static LyricsLookup NotFound() => new(LyricsLookupStatus.NotFound, null, null, null);

    /// <summary>The lookup for a service that could not be asked, or answered something unreadable.</summary>
    private LyricsLookup Unavailable(string reason)
    {
        LogUnavailable(_logger, reason);

        return new LyricsLookup(LyricsLookupStatus.Unavailable, null, null, null);
    }

    /// <summary>Reads one record into a lookup, or <see langword="null"/> when it carries no lyrics.</summary>
    private static LyricsLookup? Map(LrclibRecord record)
    {
        if (record.Instrumental)
        {
            return new LyricsLookup(LyricsLookupStatus.Instrumental, null, null, record.Id);
        }

        var synced = Text(record.SyncedLyrics);
        var plain = Text(record.PlainLyrics) ?? (synced is null ? null : Text(DerivePlainLyrics(synced)));

        return plain is null && synced is null
            ? null
            : new LyricsLookup(LyricsLookupStatus.Found, plain, synced, record.Id);
    }

    /// <summary>A value with nothing but whitespace in it is no value at all.</summary>
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Derives the unsynced text from synced lyrics: every leading <c>[mm:ss.xx]</c> time tag goes, a
    /// line that is nothing but a metadata tag (<c>[ar:…]</c>, <c>[ti:…]</c>, <c>[length:…]</c>) goes,
    /// and the remaining lines keep their order.
    /// </summary>
    /// <param name="synced">The LRC text.</param>
    /// <returns>The plain text, possibly empty.</returns>
    internal static string DerivePlainLyrics(string synced)
    {
        ArgumentNullException.ThrowIfNull(synced);

        var lines = new List<string>();

        foreach (var raw in synced.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n')
                     .Split('\n'))
        {
            if (IsMetadataLine(raw))
            {
                continue;
            }

            lines.Add(LeadingTimeTags().Replace(raw, string.Empty).TrimEnd());
        }

        // An LRC file often ends with a lone end-of-track timestamp, which strips down to a blank line.
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Whether a line is one whole <c>[key:value]</c> tag with a non-numeric key. A whole-line time tag
    /// (<c>[00:30.64]</c>) is not metadata: it is a timed line whose text happens to be empty.
    /// </summary>
    private static bool IsMetadataLine(string line)
    {
        var trimmed = line.Trim();

        if (trimmed.Length < 4 || trimmed[0] != '[' || trimmed[^1] != ']')
        {
            return false;
        }

        var separator = trimmed.IndexOf(':', StringComparison.Ordinal);

        if (separator < 2)
        {
            return false;
        }

        return !trimmed[1..separator].All(char.IsAsciiDigit);
    }

    /// <summary>Every <c>[mm:ss.xx]</c> tag at the very start of a line, and the space after it.</summary>
    [GeneratedRegex(@"^(?:\[[0-9]{1,3}:[0-9]{1,2}(?:[.:][0-9]{1,3})?\]\s*)+")]
    private static partial Regex LeadingTimeTags();

    /// <summary>One <c>api/get</c> or <c>api/search</c> exchange, or why there was none.</summary>
    private async Task<Fetch> FetchAsync(string relative, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await _http
                .GetAsync(relative, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout, not the caller giving up.
            return Fetch.TimedOut;
        }
        catch (HttpRequestException)
        {
            return Fetch.Unreachable;
        }

        using (response)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                return new Fetch(false, response.StatusCode, body, string.Empty);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Fetch.TimedOut;
            }
            catch (HttpRequestException)
            {
                return Fetch.Unreachable;
            }
            catch (IOException)
            {
                return Fetch.Unreachable;
            }
        }
    }

    /// <summary>Reads one record, or <see langword="null"/> when the body is not one.</summary>
    private static LrclibRecord? ReadRecord(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<LrclibRecord>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a search's array of records, or <see langword="null"/> when the body is not one.</summary>
    private static List<LrclibRecord>? ReadRecords(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<List<LrclibRecord>>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "LRCLIB answered {StatusCode} for a lyrics lookup.")]
    private static partial void LogStatus(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No lyrics this time: {Reason}.")]
    private static partial void LogUnavailable(ILogger logger, string reason);

    /// <summary>One response: its status and body, or the reason there was no usable one.</summary>
    /// <param name="Failed">Whether the exchange failed before a body could be read.</param>
    /// <param name="StatusCode">The status answered.</param>
    /// <param name="Body">The body, as text.</param>
    /// <param name="Reason">A short, log-safe description of a failed exchange.</param>
    private sealed record Fetch(bool Failed, HttpStatusCode StatusCode, string Body, string Reason)
    {
        /// <summary>LRCLIB did not answer inside the client's timeout.</summary>
        public static Fetch TimedOut { get; } = new(true, default, string.Empty, "the request timed out");

        /// <summary>LRCLIB could not be reached, or the answer could not be read.</summary>
        public static Fetch Unreachable { get; } = new(true, default, string.Empty, "the request failed");
    }

    /// <summary>
    /// The part of an LRCLIB record Wondarr reads. Everything else the service sends
    /// (<c>hasWordSync</c>, <c>lyricsfile</c>, <c>name</c>, the album title …) is ignored.
    /// </summary>
    /// <param name="Id">LRCLIB's id for the record.</param>
    /// <param name="Duration">The record's length in seconds; the service sends a float.</param>
    /// <param name="Instrumental">Whether the service says the track has no lyrics at all.</param>
    /// <param name="PlainLyrics">The unsynced text.</param>
    /// <param name="SyncedLyrics">The LRC text.</param>
    private sealed record LrclibRecord(
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("duration")] double? Duration,
        [property: JsonPropertyName("instrumental")] bool Instrumental,
        [property: JsonPropertyName("plainLyrics")] string? PlainLyrics,
        [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics);
}
