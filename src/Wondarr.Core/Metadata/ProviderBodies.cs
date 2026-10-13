using System.Net;
using System.Text;
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
            if (logger.IsEnabled(LogLevel.Debug))
            {
                var start = Snippet(body);
                LogUnreadableBody(logger, provider, start);
            }

            // A 502, so callers treat it as a provider that is down, not as one refusing the request.
            throw new MetadataProviderException(
                provider,
                HttpStatusCode.BadGateway,
                $"{provider} answered with a body that could not be read.");
        }
    }

    /// <summary>
    /// Whether a cached body still parses as <typeparamref name="T"/>. A row written before bodies were
    /// checked can be an error page; the caller then goes to the network, and the fresh answer overwrites it.
    /// </summary>
    public static bool IsReadable<T>(string provider, string body, JsonSerializerOptions options, ILogger logger)
    {
        try
        {
            JsonSerializer.Deserialize<T>(body, options);

            return true;
        }
        catch (JsonException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                var start = Snippet(body);
                LogUnreadableCacheEntry(logger, provider, start);
            }

            return false;
        }
    }

    /// <summary>The first characters of a body, safe to log: no control characters, no cut surrogate pair.</summary>
    internal static string Snippet(string body)
    {
        var length = Math.Min(body.Length, LoggedBodyLength);

        if (length < body.Length && length > 0 && char.IsHighSurrogate(body[length - 1]))
        {
            length--;
        }

        var builder = new StringBuilder(length);

        foreach (var character in body.AsSpan(0, length))
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString();
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{Provider} answered 2xx with a body that is not the expected JSON; not cached. Starts: {Start}")]
    private static partial void LogUnreadableBody(ILogger logger, string provider, string start);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "A cached {Provider} body is not the expected JSON; fetching it again. Starts: {Start}")]
    private static partial void LogUnreadableCacheEntry(ILogger logger, string provider, string start);
}
