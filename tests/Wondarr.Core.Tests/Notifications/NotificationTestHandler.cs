using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Wondarr.Core.Tests.Notifications;

/// <summary>One request as it went out, copied before the client disposes it.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Url">The address the request went to.</param>
/// <param name="ContentType">The content type, or <see langword="null"/>.</param>
/// <param name="Authorization">The authorization header, or <see langword="null"/>.</param>
/// <param name="Headers">Every request header, values joined.</param>
/// <param name="Body">The body, or <see langword="null"/> when there was none.</param>
internal sealed record RecordedNotificationRequest(
    HttpMethod Method,
    string Url,
    string? ContentType,
    AuthenticationHeaderValue? Authorization,
    IReadOnlyDictionary<string, string> Headers,
    string? Body)
{
    /// <summary>Gets the value of one header, or <see langword="null"/>.</summary>
    /// <param name="name">The header name.</param>
    public string? TryGetHeader(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// Answers with whatever a test asks for and keeps every request, body included: an
/// <see cref="HttpRequestMessage"/> cannot be read after it has been sent.
/// </summary>
internal sealed class NotificationTestHandler : HttpMessageHandler
{
    private readonly List<RecordedNotificationRequest> _requests = [];

    /// <summary>Gets or sets the answer to the nth attempt, 1-based.</summary>
    public Func<int, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

    /// <summary>Gets every request that went out, in order.</summary>
    public IReadOnlyList<RecordedNotificationRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Gets the parsed bodies that went out, in order.</summary>
    public IReadOnlyList<JsonElement> Payloads =>
        [.. Requests.Select(request => JsonDocument.Parse(request.Body ?? "{}").RootElement.Clone())];

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        int attempt;

        lock (_requests)
        {
            _requests.Add(new RecordedNotificationRequest(
                request.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.Authorization,
                headers,
                body));

            attempt = _requests.Count;
        }

        return Respond(attempt);
    }
}
