using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Metadata.AcoustId;

/// <summary>How an AcoustID lookup ended.</summary>
public enum AcoustIdStatus
{
    /// <summary>The service answered; <see cref="AcoustIdLookupResult.Results"/> is what it knows.</summary>
    Ok,

    /// <summary>No client key is configured, so no request was made.</summary>
    NotConfigured,

    /// <summary>The service is throttling this caller; retrying later is worth it.</summary>
    RateLimited,

    /// <summary>The client key was rejected: a configuration or health problem, not the file's fault.</summary>
    InvalidKey,

    /// <summary>The fingerprint itself was rejected.</summary>
    InvalidFingerprint,

    /// <summary>The service could not be reached, or answered with a server error.</summary>
    Unavailable,

    /// <summary>Any other failure; <see cref="AcoustIdLookupResult.Error"/> says what.</summary>
    Error,
}

/// <summary>One recording AcoustID links to a fingerprint.</summary>
/// <param name="Id">The MusicBrainz recording id.</param>
/// <param name="Title">The recording title, or <c>null</c>.</param>
/// <param name="DurationSeconds">The recording's length in seconds; AcoustID sends a float, for example 637.333.</param>
/// <param name="ArtistNames">The credited artist names, in the order the service sent them.</param>
public sealed record AcoustIdRecording(
    string Id,
    string? Title,
    double? DurationSeconds,
    IReadOnlyList<string> ArtistNames);

/// <summary>One AcoustID: an identifier for a fingerprint, with the recordings linked to it.</summary>
/// <param name="Id">The AcoustID (a UUID).</param>
/// <param name="Score">How sure the service is, between 0 and 1.</param>
/// <param name="Recordings">The recordings linked to it; empty when none are.</param>
public sealed record AcoustIdResult(string Id, double Score, IReadOnlyList<AcoustIdRecording> Recordings);

/// <summary>The answer to one fingerprint lookup.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Results">The AcoustIDs, in the order the service sent them; empty unless <see cref="AcoustIdStatus.Ok"/>.</param>
/// <param name="Error">A message safe to show and to log — never the client key.</param>
/// <param name="ErrorCode">The code of an error object AcoustID sent, or <see langword="null"/>.</param>
public sealed record AcoustIdLookupResult(
    AcoustIdStatus Status,
    IReadOnlyList<AcoustIdResult> Results,
    string? Error,
    int? ErrorCode = null);

/// <summary>Asks AcoustID what recording a fingerprint belongs to (MATCHING_ENGINE.md §6.5 step 3).</summary>
public interface IAcoustIdClient
{
    /// <summary>Looks one fingerprint up.</summary>
    /// <param name="fingerprint">The compressed base64 fingerprint fpcalc produced.</param>
    /// <param name="durationSeconds">The whole track's length in seconds, which the service checks the fingerprint against.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The AcoustIDs, or why there are none.</returns>
    Task<AcoustIdLookupResult> LookupAsync(
        string fingerprint,
        int durationSeconds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Makes one lookup with <paramref name="clientKey"/> and a fingerprint that is not one, to see
    /// whether AcoustID accepts the key: a key it knows answers "invalid fingerprint", one it does not
    /// answers "invalid key". Nothing is stored and no fingerprint is sent.
    /// </summary>
    /// <param name="clientKey">The key to try.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>
    /// <see cref="AcoustIdStatus.Ok"/> when the key is accepted — AcoustID answered anything but its
    /// invalid-key error (code 4); <see cref="AcoustIdLookupResult.Error"/> then holds the text of the
    /// refusal of the probe fingerprint. Otherwise why not (a message safe to show — never the key).
    /// </returns>
    Task<AcoustIdLookupResult> CheckKeyAsync(string clientKey, CancellationToken cancellationToken);
}

/// <summary>
/// The AcoustID v2 lookup client. The fingerprint is posted gzip-compressed, as the service asks for
/// large ones, and no response is cached: a fingerprint belongs to one file and is asked about once.
/// </summary>
/// <remarks>
/// <para>
/// The service allows three requests per second per client key, retries included. Spacing is enforced
/// by the <c>RequestSpacingHandler</c> the client is registered with, not here, so several callers
/// share one budget.
/// </para>
/// <para>
/// The client key is a secret: it is sent in the form body and nowhere else, and no failure path
/// copies it into <see cref="AcoustIdLookupResult.Error"/>, an exception or a log line.
/// </para>
/// </remarks>
public sealed partial class AcoustIdClient : IAcoustIdClient
{
    /// <summary>How many times one fingerprint is submitted before a throttle is given up on.</summary>
    private const int MaxAttempts = 3;

    /// <summary>AcoustID's code for "you are over the rate limit".</summary>
    private const int RateLimitCode = 14;

    /// <summary>AcoustID's code for a rejected client key.</summary>
    private const int InvalidKeyCode = 4;

    /// <summary>AcoustID's code for a fingerprint it cannot read.</summary>
    private const int InvalidFingerprintCode = 3;

    /// <summary>AcoustID's codes for "something is wrong on our side".</summary>
    private const int InternalErrorCode = 5;

    /// <summary>AcoustID's code for "the service is too busy".</summary>
    private const int ServiceBusyCode = 13;

    /// <summary>A string that is not a fingerprint, sent to see whether a client key is accepted.</summary>
    private const string ProbeFingerprint = "AQAAAAAA";

    /// <summary>How long to wait before a retry when the service sends no <c>Retry-After</c>.</summary>
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest this client waits inside one lookup. A longer <c>Retry-After</c> is not waited out:
    /// the verifier defers the file and the tracker asks again later, which costs no idle thread here.
    /// </summary>
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<AcoustIdOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AcoustIdClient> _logger;

    /// <summary>Initialises a new instance of the <see cref="AcoustIdClient"/> class.</summary>
    /// <param name="http">The typed client, already carrying the base URL, the User-Agent and the spacing handler.</param>
    /// <param name="options">The client key, base URL and score thresholds.</param>
    /// <param name="timeProvider">The clock the retry waits run on; tests drive it with <c>FakeTimeProvider</c>.</param>
    /// <param name="logger">Logs retries at Debug and throttling at Warning; never logs a key or a fingerprint.</param>
    public AcoustIdClient(
        HttpClient http,
        IOptionsMonitor<AcoustIdOptions> options,
        TimeProvider timeProvider,
        ILogger<AcoustIdClient> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AcoustIdLookupResult> LookupAsync(
        string fingerprint,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        var clientKey = _options.CurrentValue.ClientKey;

        if (string.IsNullOrWhiteSpace(clientKey))
        {
            // Verified by probe and duration alone; there is nothing to ask without a key.
            return new AcoustIdLookupResult(AcoustIdStatus.NotConfigured, [], null);
        }

        return await LookupWithKeyAsync(clientKey, fingerprint, durationSeconds, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AcoustIdLookupResult> CheckKeyAsync(string clientKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientKey);

        var result = await LookupWithKeyAsync(clientKey.Trim(), ProbeFingerprint, 1, cancellationToken)
            .ConfigureAwait(false);

        // The probe fingerprint is refused on purpose. AcoustID refusing it with any error object other
        // than the invalid-key one (code 4, already reported as InvalidKey) means the key got past.
        return result.Status is AcoustIdStatus.InvalidFingerprint or AcoustIdStatus.Error && result.ErrorCode is not null
            ? new AcoustIdLookupResult(AcoustIdStatus.Ok, [], result.Error, result.ErrorCode)
            : result;
    }

    private async Task<AcoustIdLookupResult> LookupWithKeyAsync(
        string clientKey,
        string fingerprint,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = BuildRequest(clientKey, fingerprint, durationSeconds);

            HttpResponseMessage response;
            try
            {
                response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new AcoustIdLookupResult(AcoustIdStatus.Unavailable, [], "AcoustID did not answer in time");
            }
            catch (HttpRequestException exception)
            {
                return new AcoustIdLookupResult(
                    AcoustIdStatus.Unavailable,
                    [],
                    $"AcoustID could not be reached: {exception.Message}");
            }

            using (response)
            {
                ResponseBody parsed;

                // Reading the body is part of the exchange: a connection dropped mid-response is an
                // outage like any other, not an exception the caller has to handle.
                try
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    parsed = ReadBody(body);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new AcoustIdLookupResult(AcoustIdStatus.Unavailable, [], "AcoustID did not answer in time");
                }
                catch (HttpRequestException exception)
                {
                    return new AcoustIdLookupResult(
                        AcoustIdStatus.Unavailable,
                        [],
                        $"AcoustID could not be reached: {exception.Message}");
                }
                catch (IOException exception)
                {
                    return new AcoustIdLookupResult(
                        AcoustIdStatus.Unavailable,
                        [],
                        $"AcoustID's answer could not be read: {exception.Message}");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests || parsed.Code == RateLimitCode)
                {
                    var delay = RetryDelay(response);

                    // Waiting out an hour-long Retry-After in here holds the import for no gain: the
                    // caller defers the file and the tracker comes back when the wait is over.
                    if (attempt >= MaxAttempts || delay > MaxRetryWait)
                    {
                        LogRateLimited(_logger, attempt, delay);

                        return new AcoustIdLookupResult(AcoustIdStatus.RateLimited, [], null);
                    }

                    LogRetrying(_logger, attempt, delay);

                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);

                    continue;
                }

                if (parsed.Code == InvalidKeyCode)
                {
                    // The message is the service's own; it describes the key, it never contains it.
                    return new AcoustIdLookupResult(
                        AcoustIdStatus.InvalidKey,
                        [],
                        parsed.Message ?? "AcoustID rejected the client key");
                }

                if (parsed.Code == InvalidFingerprintCode)
                {
                    return new AcoustIdLookupResult(
                        AcoustIdStatus.InvalidFingerprint,
                        [],
                        parsed.Message ?? "AcoustID rejected the fingerprint",
                        parsed.Code);
                }

                if (parsed.Code is InternalErrorCode or ServiceBusyCode || (int)response.StatusCode >= 500)
                {
                    return new AcoustIdLookupResult(
                        AcoustIdStatus.Unavailable,
                        [],
                        parsed.Message ?? $"AcoustID answered {(int)response.StatusCode}");
                }

                if (string.Equals(parsed.Status, "ok", StringComparison.Ordinal))
                {
                    return new AcoustIdLookupResult(AcoustIdStatus.Ok, parsed.Results, null);
                }

                if (string.Equals(parsed.Status, "error", StringComparison.Ordinal))
                {
                    return new AcoustIdLookupResult(
                        AcoustIdStatus.Error,
                        [],
                        parsed.Message ?? $"AcoustID answered error {parsed.Code}",
                        parsed.Code);
                }

                return new AcoustIdLookupResult(
                    AcoustIdStatus.Error,
                    [],
                    response.IsSuccessStatusCode
                        ? "AcoustID sent a response that could not be read"
                        : $"AcoustID answered {(int)response.StatusCode}");
            }
        }
    }

    /// <summary>Builds the gzip-compressed form post. The body is rebuilt per attempt: a request cannot be resent.</summary>
    private static HttpRequestMessage BuildRequest(string clientKey, string fingerprint, int durationSeconds)
    {
        var form = "client=" + Uri.EscapeDataString(clientKey)
            + "&duration=" + durationSeconds.ToString(CultureInfo.InvariantCulture)
            + "&fingerprint=" + Uri.EscapeDataString(fingerprint)
            + "&meta=recordings&format=json";

        var compressed = Compress(form);
        compressed.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        compressed.Headers.ContentEncoding.Add("gzip");

        return new HttpRequestMessage(HttpMethod.Post, "lookup") { Content = compressed };
    }

    /// <summary>Gzip-compresses a body the service can decompress from the Content-Encoding header.</summary>
    private static ByteArrayContent Compress(string body)
    {
        using var buffer = new MemoryStream();

        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(body));
        }

        return new ByteArrayContent(buffer.ToArray());
    }

    /// <summary>How long the service asked this caller to wait, defaulting to one second.</summary>
    private TimeSpan RetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;

        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - _timeProvider.GetUtcNow();

            return wait > TimeSpan.Zero ? wait : DefaultRetryDelay;
        }

        return DefaultRetryDelay;
    }

    /// <summary>Reads what the service sent: the results, or the error object.</summary>
    private static ResponseBody ReadBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new ResponseBody(null, null, null, []);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ResponseBody(null, null, null, []);
            }

            int? code = null;
            string? message = null;

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var value)
                    ? value
                    : null;
                message = Text(error, "message");
            }

            return new ResponseBody(Text(root, "status"), code, message, ReadResults(root));
        }
        catch (JsonException)
        {
            // An HTML error page from a proxy, most likely; the caller reports it as unreadable.
            return new ResponseBody(null, null, null, []);
        }
    }

    private static List<AcoustIdResult> ReadResults(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = new List<AcoustIdResult>();

        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = Text(result, "id");
            if (id is null)
            {
                continue;
            }

            parsed.Add(new AcoustIdResult(id, Number(result, "score") ?? 0, ReadRecordings(result)));
        }

        return parsed;
    }

    private static List<AcoustIdRecording> ReadRecordings(JsonElement result)
    {
        // A known fingerprint with no linked recordings simply leaves the property out.
        if (!result.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = new List<AcoustIdRecording>();

        foreach (var recording in recordings.EnumerateArray())
        {
            if (recording.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = Text(recording, "id");
            if (id is null)
            {
                continue;
            }

            parsed.Add(new AcoustIdRecording(
                id,
                Text(recording, "title"),
                Number(recording, "duration"),
                ReadArtists(recording)));
        }

        return parsed;
    }

    private static List<string> ReadArtists(JsonElement recording)
    {
        if (!recording.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>();

        foreach (var artist in artists.EnumerateArray())
        {
            var name = artist.ValueKind == JsonValueKind.Object ? Text(artist, "name") : null;

            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetDouble()
            : null;

    [LoggerMessage(Level = LogLevel.Debug, Message = "AcoustID asked for a {Delay} wait before attempt {Attempt}.")]
    private static partial void LogRetrying(ILogger logger, int attempt, TimeSpan delay);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "AcoustID is rate limiting (attempt {Attempt}, asked for a {Delay} wait); deferring the file.")]
    private static partial void LogRateLimited(ILogger logger, int attempt, TimeSpan delay);

    /// <summary>What one response body said, with a result list that is never null.</summary>
    private sealed record ResponseBody(
        string? Status,
        int? Code,
        string? Message,
        IReadOnlyList<AcoustIdResult> Results);
}
