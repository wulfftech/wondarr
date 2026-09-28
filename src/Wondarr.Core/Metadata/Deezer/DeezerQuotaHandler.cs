using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Wondarr.Core.Metadata.Deezer;

/// <summary>
/// Turns Deezer's "Quota limit exceeded" — which it reports as <em>HTTP 200</em> with an
/// <c>error</c> object carrying code 4 — into a <c>429</c> with a <c>Retry-After</c>, so the retry
/// pipeline above it treats a quota like any other throttle instead of a success. Registered
/// innermost, below the spacing handler, so a retried attempt is spaced and then inspected.
/// </summary>
public sealed class DeezerQuotaHandler : DelegatingHandler
{
    /// <summary>How long the handler asks callers to wait after a quota error, in seconds.</summary>
    public const int RetryAfterSeconds = 5;

    /// <summary>The error code Deezer uses for an exceeded quota.</summary>
    private const int QuotaExceededCode = DeezerClient.QuotaExceededCode;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.OK || response.Content is null)
        {
            return response;
        }

        // The body has to be buffered to be inspected; put it back so the caller can read it. Deezer
        // always answers JSON, so the content type is safe to restate.
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.Content = new StringContent(body, Encoding.UTF8, "application/json");

        if (!TryReadErrorCode(body, out var code) || code != QuotaExceededCode)
        {
            return response;
        }

        response.StatusCode = HttpStatusCode.TooManyRequests;
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(RetryAfterSeconds));
        response.ReasonPhrase = "Quota limit exceeded";

        return response;
    }

    /// <summary>Reads the error code out of a body, when it carries an <c>error</c> object.</summary>
    private static bool TryReadErrorCode(string body, out int code)
    {
        code = 0;

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("code", out var value)
                || !value.TryGetInt32(out code))
            {
                return false;
            }

            if (code != QuotaExceededCode)
            {
                return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return true;
    }
}
