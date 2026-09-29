using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Tests.Metadata;

/// <summary>
/// The recorded AcoustID responses under <c>tests/fixtures/acoustid</c>. They are not copied next to
/// the test binaries, so the directory is found by walking up to the repository root.
/// </summary>
internal static class AcoustIdFixtures
{
    /// <summary>A key long enough for the secret registry to treat it as one.</summary>
    public const string ClientKey = "S3cretAcoustIdKey";

    /// <summary>The checked-in <c>tests/fixtures/acoustid</c> directory.</summary>
    public static string Directory { get; } = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "acoustid");

    /// <summary>A fingerprint that is never sent anywhere in these tests.</summary>
    public const string Fingerprint = "AQABz0qUkZK4oOfhL-CPc4e5C_wW2H2QH9uDL4cvoT8";

    /// <summary>Reads one recorded response.</summary>
    public static string Read(string name) => File.ReadAllText(Path.Combine(Directory, name));

    /// <summary>An AcoustID answer, with the status the fixture documents.</summary>
    public static HttpResponseMessage Response(string name, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(Read(name), Encoding.UTF8, "application/json"),
    };

    /// <summary>Decompresses a request body that was sent as gzip.</summary>
    public static string Decompress(byte[] body)
    {
        using var buffer = new MemoryStream(body);
        using var gzip = new GZipStream(buffer, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        return reader.ReadToEnd();
    }

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

/// <summary>An <see cref="IOptionsMonitor{T}"/> over one instance, for tests that build a client by hand.</summary>
internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
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

/// <summary>
/// Serves canned responses, and records what each request actually was: its method, URI and the
/// decompressed gzip body, which is the only place the form fields can be asserted on.
/// </summary>
internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responder = responder;

    /// <summary>The URIs asked for, in order.</summary>
    public List<Uri> Requests { get; } = [];

    /// <summary>The methods used, in order.</summary>
    public List<HttpMethod> Methods { get; } = [];

    /// <summary>The decompressed body of each request, in order.</summary>
    public List<string> Bodies { get; } = [];

    /// <summary>The Content-Encoding of each request, in order.</summary>
    public List<string> Encodings { get; } = [];

    /// <summary>The Content-Type of each request, in order.</summary>
    public List<string> ContentTypes { get; } = [];

    /// <summary>The User-Agent of each request, in order.</summary>
    public List<string> UserAgents { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        Methods.Add(request.Method);
        Encodings.Add(string.Join(",", request.Content?.Headers.ContentEncoding ?? []));
        ContentTypes.Add(request.Content?.Headers.ContentType?.MediaType ?? string.Empty);
        UserAgents.Add(string.Join(",", request.Headers.UserAgent.Select(value => value.ToString())));

        var body = request.Content is null
            ? []
            : request.Content.ReadAsByteArrayAsync(CancellationToken.None).GetAwaiter().GetResult();

        Bodies.Add(Encodings[^1].Contains("gzip", StringComparison.Ordinal)
            ? AcoustIdFixtures.Decompress(body)
            : Encoding.UTF8.GetString(body));

        return Task.FromResult(_responder(request));
    }

    /// <summary>Reads one form field out of a decompressed body.</summary>
    public string? Form(string name, int request = 0)
    {
        foreach (var pair in Bodies[request].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);

            if (separator > 0 && string.Equals(pair[..separator], name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return null;
    }

    /// <summary>The field names of one decompressed body, in order.</summary>
    public IReadOnlyList<string> Fields(int request = 0) =>
        [.. Bodies[request]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair[..pair.IndexOf('=', StringComparison.Ordinal)])];

    /// <summary>A handler that answers every request with one fixture.</summary>
    /// <param name="name">The fixture file to serve.</param>
    /// <param name="status">The status the fixture was recorded with.</param>
    public static RecordingHttpMessageHandler Serving(string name, HttpStatusCode status = HttpStatusCode.OK)
    {
        // A fresh response per request: one is consumed and disposed by the caller.
        return new RecordingHttpMessageHandler(_ => AcoustIdFixtures.Response(name, status));
    }

    /// <summary>The numbers in a form field, as the service expects whole seconds.</summary>
    public static int? Integer(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}