using System.Net;

namespace Wondarr.Core.Plex;

/// <summary>One Plex answer: the status and the body, read before the response is disposed.</summary>
/// <param name="Status">The status line.</param>
/// <param name="Body">The response body, or an empty string when there is none.</param>
internal readonly record struct PlexResponse(HttpStatusCode Status, string Body)
{
    /// <summary>Whether the status is 2xx.</summary>
    public bool IsSuccess => (int)Status is >= 200 and < 300;
}

/// <summary>
/// Sends Plex requests and maps transport failures. Every message here names the method and the
/// path only: a Plex URL can carry a <c>path</c> query that belongs to the user's library layout,
/// and the token must never appear in a message at all.
/// </summary>
internal static class PlexHttp
{
    /// <summary>
    /// Sends one request. Only a transport failure or a 401/403 throws here; every other status is
    /// handed back, because the callers disagree about what 404 and 5xx mean.
    /// </summary>
    /// <param name="http">The client to send with.</param>
    /// <param name="request">The request to send.</param>
    /// <param name="subject">Who is being called, for example <c>plex.tv</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="PlexUnauthorizedException">The answer was 401 or 403.</exception>
    /// <exception cref="PlexException">The request timed out or could not be made at all.</exception>
    public static async Task<PlexResponse> SendAsync(
        HttpClient http,
        HttpRequestMessage request,
        string subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);

        HttpResponseMessage response;

        try
        {
            response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout, not the caller's cancellation: HttpClient reports both as a
            // cancellation of the request.
            throw new PlexException(
                $"{subject} did not answer within {http.Timeout.TotalSeconds:0} seconds.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new PlexException($"{subject} could not be reached: {exception.Message}", exception);
        }

        using (response)
        {
            var status = response.StatusCode;

            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new PlexUnauthorizedException(
                    $"{subject} refused {request.Method} {Path(request)} with {(int)status}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new PlexResponse(status, body);
        }
    }

    /// <summary>
    /// Returns the body of a successful answer, and turns every other status into the exception the
    /// caller should see.
    /// </summary>
    /// <param name="response">The answer to inspect.</param>
    /// <param name="request">The request it answers, for the message.</param>
    /// <param name="subject">Who answered, for example <c>The Plex server</c>.</param>
    /// <exception cref="PlexUnauthorizedException">The answer was 401 or 403.</exception>
    /// <exception cref="PlexException">The answer was any other failure.</exception>
    public static string Read(PlexResponse response, HttpRequestMessage request, string subject)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new PlexUnauthorizedException(
                $"{subject} refused {request.Method} {Path(request)} with {(int)response.Status}.");
        }

        if (!response.IsSuccess)
        {
            throw new PlexException(
                $"{subject} answered {(int)response.Status} for {request.Method} {Path(request)}.");
        }

        return response.Body;
    }

    /// <summary>The request's path, without its query: a query can carry a library path.</summary>
    private static string Path(HttpRequestMessage request) =>
        request.RequestUri?.AbsolutePath ?? "the request";
}
