using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Metadata;

/// <summary>
/// Checks a provider's 2xx body before it is cached. A captive portal or an error page that answers 200
/// with HTML must not be stored: every later lookup would hit the same unreadable entry until it expired.
/// </summary>
internal static partial class ProviderBodies
{
    /// <summary>How much of an unreadable body reaches the debug log.</summary>
    private const int LoggedBodyLength = 120;

    /// <summary>
    /// Parses <paramref name="body"/> as <typeparamref name="T"/>, and throws when it is not that.
    /// </summary>
    /// <exception cref="MetadataProviderException">The body is not <typeparamref name="T"/>.</exception>
    public static T? Parse<T>(
        string provider,
        string body,
        JsonSerializerOptions options,
        ILogger logger)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, options);
        }
        catch (JsonException)
        {
            LogUnreadableBody(logger, provider, body.Length > LoggedBodyLength ? body[..LoggedBodyLength] : body);

            // A 502, so callers treat it as a provider that is down, not as one refusing the request.
            throw new MetadataProviderException(
                provider,
                HttpStatusCode.BadGateway,
                $"{provider} answered with a body that could not be read.");
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{Provider} answered 2xx with a body that is not the expected JSON; not cached. Starts: {Start}")]
    private static partial void LogUnreadableBody(ILogger logger, string provider, string start);
}
