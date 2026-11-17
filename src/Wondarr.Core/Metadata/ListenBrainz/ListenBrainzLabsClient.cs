using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Wondarr.Core.Metadata.ListenBrainz;

/// <summary>One recording ListenBrainz Labs lists as similar to the seed.</summary>
/// <param name="RecordingMbid">The recording MBID.</param>
/// <param name="RecordingName">The recording's title.</param>
/// <param name="ArtistCreditName">The artist credit as MusicBrainz spells it.</param>
/// <param name="ReleaseName">The release the recording is shown on, or <see langword="null"/>.</param>
/// <param name="ReleaseMbid">The release MBID, or <see langword="null"/>.</param>
/// <param name="CaaReleaseMbid">The release MBID the Cover Art Archive holds a cover under, or <see langword="null"/>.</param>
/// <param name="Score">The algorithm's own score; only the order is used.</param>
public sealed record ListenBrainzSimilarRecording(
    string RecordingMbid,
    string RecordingName,
    string ArtistCreditName,
    string? ReleaseName,
    string? ReleaseMbid,
    string? CaaReleaseMbid,
    double? Score);

/// <summary>How a ListenBrainz Labs call ended.</summary>
public enum ListenBrainzLabsStatus
{
    /// <summary>Labs answered; <see cref="ListenBrainzLabsResult.Recordings"/> is its list, possibly empty.</summary>
    Ok,

    /// <summary>Labs answered 400 because it does not know the algorithm name.</summary>
    AlgorithmRejected,

    /// <summary>Labs is throttling this caller (429); the gate holds later calls back for its <c>Retry-After</c>.</summary>
    RateLimited,

    /// <summary>Labs could not be reached, was too slow, or answered something unusable.</summary>
    Unavailable,
}

/// <summary>The answer to one ListenBrainz Labs call.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Recordings">The similar recordings, best first; empty unless <see cref="ListenBrainzLabsStatus.Ok"/>.</param>
/// <param name="Message">A message safe to log; for a rejected algorithm, what the server said.</param>
public sealed record ListenBrainzLabsResult(
    ListenBrainzLabsStatus Status,
    IReadOnlyList<ListenBrainzSimilarRecording> Recordings,
    string? Message)
{
    /// <summary>Gets a value indicating whether Labs answered with a list.</summary>
    public bool Ok => Status == ListenBrainzLabsStatus.Ok;
}

/// <summary>Reads similar recordings from ListenBrainz Labs.</summary>
public interface IListenBrainzLabsClient
{
    /// <summary>Reads <c>similar-recordings/json</c> for one recording.</summary>
    /// <param name="recordingMbid">The seed's recording MBID.</param>
    /// <param name="algorithm">The Labs algorithm name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<ListenBrainzLabsResult> GetSimilarRecordingsAsync(
        string recordingMbid,
        string algorithm,
        CancellationToken cancellationToken);
}

/// <summary>
/// The ListenBrainz Labs client. It sits behind its own one-request-a-second spacing gate, which also
/// records a <c>Retry-After</c> for every later caller. It never throws for a provider failure: a
/// throttled, slow or broken Labs is a status, because Discover carries on with its other sources.
/// </summary>
public sealed partial class ListenBrainzLabsClient : IListenBrainzLabsClient
{
    /// <summary>The provider key in logs and exceptions.</summary>
    public const string Provider = "listenbrainz-labs";

    /// <summary>What Labs says when the algorithm name is not one of its enumeration's members.</summary>
    public const string InvalidEnumerationMessage = "value is not a valid enumeration member";

    private const int BodyExcerptLength = 300;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <summary>Initialises a new instance of the <see cref="ListenBrainzLabsClient"/> class.</summary>
    /// <param name="http">The typed client, which carries the base address and the spacing gate.</param>
    /// <param name="logger">Where a failure is reported, at debug level.</param>
    public ListenBrainzLabsClient(HttpClient http, ILogger<ListenBrainzLabsClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        _http = http;
        _logger = logger ?? NullLogger<ListenBrainzLabsClient>.Instance;
    }

    /// <inheritdoc />
    public async Task<ListenBrainzLabsResult> GetSimilarRecordingsAsync(
        string recordingMbid,
        string algorithm,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingMbid);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);

        var uri = new Uri(
            "similar-recordings/json?recording_mbids=" + Uri.EscapeDataString(recordingMbid.Trim())
            + "&algorithm=" + Uri.EscapeDataString(algorithm.Trim()),
            UriKind.Relative);

        try
        {
            using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return Interpret(response.StatusCode, body);
        }
        catch (MetadataProviderException exception)
        {
            // The spacing gate refusing to queue behind a long Retry-After.
            LogFailure(_logger, exception.Message);

            return Failed(ListenBrainzLabsStatus.RateLimited, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(ListenBrainzLabsStatus.Unavailable, "ListenBrainz Labs did not answer in time.");
        }
        catch (HttpRequestException)
        {
            return Failed(ListenBrainzLabsStatus.Unavailable, "ListenBrainz Labs could not be reached.");
        }
    }

    private static ListenBrainzLabsResult Failed(ListenBrainzLabsStatus status, string message) =>
        new(status, [], message);

    private static ListenBrainzLabsResult Interpret(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.TooManyRequests)
        {
            return Failed(ListenBrainzLabsStatus.RateLimited, "ListenBrainz Labs is rate limiting this server.");
        }

        if (status == HttpStatusCode.BadRequest
            && body.Contains(InvalidEnumerationMessage, StringComparison.OrdinalIgnoreCase))
        {
            return Failed(ListenBrainzLabsStatus.AlgorithmRejected, Excerpt(body));
        }

        if (!IsSuccess(status))
        {
            return Failed(
                ListenBrainzLabsStatus.Unavailable,
                $"ListenBrainz Labs answered {(int)status}.");
        }

        try
        {
            var rows = JsonSerializer.Deserialize<List<Row>>(body, SerializerOptions) ?? [];

            return new ListenBrainzLabsResult(
                ListenBrainzLabsStatus.Ok,
                [
                    .. rows
                        .Where(row => !string.IsNullOrWhiteSpace(row.RecordingMbid)
                            && !string.IsNullOrWhiteSpace(row.RecordingName))
                        .Select(row => new ListenBrainzSimilarRecording(
                            row.RecordingMbid!.Trim().ToLowerInvariant(),
                            row.RecordingName!.Trim(),
                            row.ArtistCreditName?.Trim() ?? string.Empty,
                            NullIfEmpty(row.ReleaseName),
                            NullIfEmpty(row.ReleaseMbid)?.ToLowerInvariant(),
                            NullIfEmpty(row.CaaReleaseMbid)?.ToLowerInvariant(),
                            row.Score)),
                ],
                null);
        }
        catch (JsonException)
        {
            return Failed(ListenBrainzLabsStatus.Unavailable, "ListenBrainz Labs answered something Wondarr could not read.");
        }
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Excerpt(string body)
    {
        var flat = body.ReplaceLineEndings(" ").Trim();

        return flat.Length <= BodyExcerptLength
            ? flat
            : string.Create(CultureInfo.InvariantCulture, $"{flat[..BodyExcerptLength]}...");
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "ListenBrainz Labs call failed: {Message}")]
    private static partial void LogFailure(ILogger logger, string message);

    private sealed class Row
    {
        public string? RecordingMbid { get; set; }

        public string? RecordingName { get; set; }

        public string? ArtistCreditName { get; set; }

        public string? ReleaseName { get; set; }

        public string? ReleaseMbid { get; set; }

        public string? CaaReleaseMbid { get; set; }

        public double? Score { get; set; }
    }
}
