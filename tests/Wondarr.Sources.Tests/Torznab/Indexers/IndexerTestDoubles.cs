using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Logging;
using Wondarr.Core.Sources;
using Wondarr.Sources.Torznab.Indexers;

namespace Wondarr.Sources.Tests.Torznab.Indexers;

/// <summary>
/// The checked-in Torznab/Newznab answers under <c>tests/fixtures/torznab</c>. They are not copied
/// next to the test binaries, so the directory is found by walking up to the repository root.
/// </summary>
internal static class TorznabFixtures
{
    /// <summary>The API key every fixture request carries; long enough for the secret registry.</summary>
    public const string ApiKey = "S3cretIndexerKey";

    /// <summary>The base URL every test indexer row uses.</summary>
    public const string BaseUrl = "https://indexer.example";

    /// <summary>The checked-in <c>tests/fixtures/torznab</c> directory.</summary>
    public static string Directory { get; } = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "torznab");

    /// <summary>Reads one checked-in answer.</summary>
    /// <param name="name">The fixture file's name.</param>
    public static string Read(string name) => File.ReadAllText(Path.Combine(Directory, name));

    /// <summary>A fixture answer, as an HTTP response.</summary>
    /// <param name="name">The fixture file's name.</param>
    /// <param name="status">The status to answer with.</param>
    public static HttpResponseMessage Response(string name, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(Read(name), Encoding.UTF8, "application/xml"),
    };

    /// <summary>An indexer row wired to the fixture endpoint.</summary>
    /// <param name="id">The row's id.</param>
    /// <param name="type">The row's type name.</param>
    /// <param name="protocol">The row's protocol.</param>
    /// <param name="categories">The row's categories setting.</param>
    public static Indexer Indexer(
        long id = 1,
        string type = "torznab",
        DownloadProtocol protocol = DownloadProtocol.Torrent,
        string categories = "3000") => new()
    {
        Id = id,
        Name = "Example Indexer",
        Type = type,
        Protocol = protocol,
        Settings = $$"""
            {
              "url": "{{BaseUrl}}",
              "apiPath": "/api",
              "apiKey": "{{ApiKey}}",
              "categories": "{{categories}}"
            }
            """,
    };

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (System.IO.Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"Could not find tests/fixtures above {AppContext.BaseDirectory}");
    }
}

/// <summary>
/// Serves canned answers and records what each request actually was: its URI and its User-Agent. No
/// request ever leaves the process.
/// </summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    /// <summary>The URIs asked for, in order.</summary>
    public List<Uri> Requests { get; } = [];

    /// <summary>The User-Agent of each request, in order.</summary>
    public List<string> UserAgents { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        UserAgents.Add(string.Join(",", request.Headers.UserAgent.Select(value => value.ToString())));

        return Task.FromResult(_responder(request));
    }

    /// <summary>A handler that answers every request with one fixture.</summary>
    /// <param name="name">The fixture file to serve.</param>
    /// <param name="status">The status to answer with.</param>
    public static RecordingHandler Serving(string name, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => TorznabFixtures.Response(name, status));

    /// <summary>A handler that answers by the request's <c>t</c> parameter, the way an indexer does.</summary>
    /// <param name="caps">The fixture served for <c>t=caps</c>.</param>
    /// <param name="results">The fixture served for a search.</param>
    public static RecordingHandler ServingCapsAndResults(string caps, string results) =>
        new(request =>
            request.RequestUri!.Query.Contains("t=caps", StringComparison.Ordinal)
                ? TorznabFixtures.Response(caps)
                : TorznabFixtures.Response(results));
}

/// <summary>An <see cref="IHttpClientFactory"/> over one handler, for tests that build a client by hand.</summary>
internal sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>
/// A logger that keeps every rendered line, so a test can assert that a secret never reached one.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    /// <summary>Every line the caller logged, rendered with its arguments.</summary>
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        Lines.Add(formatter(state, exception));
    }
}

/// <summary>The shared wiring of the classes under test.</summary>
internal static class TorznabTest
{
    /// <summary>A caps reader over the given handler, with a fresh cache and a real secret registry.</summary>
    /// <param name="handler">The handler its requests go to.</param>
    /// <param name="cache">The cache to remember caps answers in.</param>
    /// <param name="secrets">The secret registry its API key is registered with.</param>
    /// <param name="logger">The log sink.</param>
    public static NewznabCapabilitiesReader CapsReader(
        HttpMessageHandler handler,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? cache = null,
        ISecretRegistry? secrets = null,
        ILogger<NewznabCapabilitiesReader>? logger = null) => new(
        new StaticHttpClientFactory(handler),
        cache ?? new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
        secrets ?? new SecretRegistry(),
        logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<NewznabCapabilitiesReader>.Instance);

    /// <summary>A caps reader no request is ever sent to, for tests that only validate settings.</summary>
    public static NewznabCapabilitiesReader CapsHandler() => CapsReader(RecordingHandler.Serving("caps-prowlarr.xml"));

    /// <summary>A Torznab client over the given handler.</summary>
    /// <param name="handler">The handler its requests go to.</param>
    /// <param name="capabilities">The caps reader it asks.</param>
    /// <param name="secrets">The secret registry its API key is registered with.</param>
    /// <param name="logger">The log sink.</param>
    public static TorznabIndexerClient TorznabClient(
        HttpMessageHandler handler,
        NewznabCapabilitiesReader? capabilities = null,
        ISecretRegistry? secrets = null,
        ILogger<TorznabIndexerClient>? logger = null) => new(
        new StaticHttpClientFactory(handler),
        capabilities ?? CapsReader(handler, secrets: secrets),
        secrets ?? new SecretRegistry(),
        logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TorznabIndexerClient>.Instance);

    /// <summary>A Newznab client over the given handler.</summary>
    /// <param name="handler">The handler its requests go to.</param>
    /// <param name="capabilities">The caps reader it asks.</param>
    /// <param name="secrets">The secret registry its API key is registered with.</param>
    /// <param name="logger">The log sink.</param>
    public static NewznabIndexerClient NewznabClient(
        HttpMessageHandler handler,
        NewznabCapabilitiesReader? capabilities = null,
        ISecretRegistry? secrets = null,
        ILogger<NewznabIndexerClient>? logger = null) => new(
        new StaticHttpClientFactory(handler),
        capabilities ?? CapsReader(handler, secrets: secrets),
        secrets ?? new SecretRegistry(),
        logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<NewznabIndexerClient>.Instance);

    /// <summary>Parses a request URL's query into its parameters, unescaped.</summary>
    /// <param name="url">The URL the client built.</param>
    public static IReadOnlyDictionary<string, string> Query(Uri url)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);

            if (separator > 0)
            {
                parameters[pair[..separator]] = Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return parameters;
    }
}
