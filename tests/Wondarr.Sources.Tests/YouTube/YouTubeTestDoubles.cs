using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using NSubstitute;
using Wondarr.Sources.YouTube;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>Serves a canned response, recording the requests it was given (the slskd double, for InnerTube).</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public StubHttpMessageHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>
    /// Each request's body, read while the request is still alive: the client disposes the
    /// <see cref="HttpRequestMessage"/> (and its content) as soon as it has the response.
    /// </summary>
    public List<string> Bodies { get; } = [];

    public static StubHttpMessageHandler Ok(string body) => new(HttpStatusCode.OK, body);

    public static StubHttpMessageHandler Status(HttpStatusCode status, string body) => new(status, body);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null
            ? string.Empty
            : request.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult());

        var response = new HttpResponseMessage(_status)
        {
            RequestMessage = request,
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        };

        return Task.FromResult(response);
    }
}

/// <summary>Shared builders for the YouTube tests.</summary>
internal static class YouTubeTestData
{
    /// <summary>The date the fixtures were recorded, so the computed client version is deterministic.</summary>
    public static readonly DateTimeOffset Recorded = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    public static IOptionsMonitor<YouTubeOptions> Monitor(YouTubeOptions options)
    {
        var monitor = Substitute.For<IOptionsMonitor<YouTubeOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    public static InnertubeClient Client(
        StubHttpMessageHandler handler,
        YouTubeOptions? options = null,
        TimeProvider? timeProvider = null) =>
        new(
            new HttpClient(handler),
            Monitor(options ?? new YouTubeOptions()),
            timeProvider ?? TimeProvider.System);

    public static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "ytmusic", name));
}
