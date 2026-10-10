using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using global::FakeSlskd; // qualified: this test namespace also ends in "FakeSlskd"
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wondarr.Sources.Tests.FakeSlskd;

/// <summary>
/// Hosts the fake slskd and its AcoustID stub in process, on free loopback ports, with a temporary
/// data directory. The apps are the real ones — <see cref="FakeSlskdApp.Build"/> and
/// <see cref="AcoustIdStubApp.Build"/> — so the tests exercise the same wiring the container runs.
/// </summary>
internal sealed class FakeSlskdHarness : IAsyncDisposable
{
    /// <summary>The API key the harness writes into the fake's configuration.</summary>
    public const string ApiKey = "test-api-key";

    /// <summary>The header the webhook receiver expects, from the fake's configuration.</summary>
    public const string WebhookHeaderName = "X-Wondarr-Gate";

    /// <summary>The webhook header's value.</summary>
    public const string WebhookHeaderValue = "phase2";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly List<WebApplication> _apps = [];
    private readonly FakeSlskdState _state;

    private FakeSlskdHarness(
        string root,
        FakeSlskdState state,
        HttpClient slskd,
        HttpClient acoustId,
        HttpClient? innertube,
        WebhookReceiver webhooks)
    {
        Root = root;
        _state = state;
        Slskd = slskd;
        AcoustId = acoustId;
        Innertube = innertube;
        Webhooks = webhooks;
    }

    /// <summary>The temporary directory the fake reads and writes.</summary>
    public string Root { get; }

    /// <summary>A client for the fake's API, with the API key already set.</summary>
    public HttpClient Slskd { get; }

    /// <summary>A client for the AcoustID stub; it needs no key.</summary>
    public HttpClient AcoustId { get; }

    /// <summary>A client for the InnerTube stub, when the harness started one; it needs no key.</summary>
    public HttpClient? Innertube { get; }

    /// <summary>The webhook receiver the fake posts <c>DownloadFileComplete</c> to.</summary>
    public WebhookReceiver Webhooks { get; }

    /// <summary>Where the fake moves finished downloads.</summary>
    public string DownloadsDirectory => Path.Combine(Root, "downloads");

    /// <summary>Where the fake generates audio before moving it.</summary>
    public string IncompleteDirectory => Path.Combine(Root, "incomplete");

    /// <summary>The directory the fake claims to share.</summary>
    public string ShareDirectory => Path.Combine(Root, "music");

    /// <summary>Whether the app is configured to share a directory.</summary>
    public bool HasShare { get; private init; }

    /// <summary>Starts the fake and its stub.</summary>
    /// <param name="scenarioJson">The scenario document.</param>
    /// <param name="username">The Soulseek username the fake claims; <see langword="null"/> reports as logged out.</param>
    /// <param name="generator">The audio generator; a byte-writing fake unless a test says otherwise.</param>
    /// <param name="shareDirectory">Whether a shared directory is configured.</param>
    /// <param name="innertubeFixtures">The directory of recorded InnerTube responses; when set, the InnerTube stub starts too.</param>
    public static async Task<FakeSlskdHarness> StartAsync(
        string scenarioJson,
        string? username = "wondarr-test",
        IAudioGenerator? generator = null,
        bool shareDirectory = true,
        string? innertubeFixtures = null)
    {
        // FreePort hands out a port that is free now, not when Kestrel binds it: a test running in
        // parallel can take it in between (seen in CI). Start again on fresh ports when that happens.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await StartOnceAsync(scenarioJson, username, generator, shareDirectory, innertubeFixtures)
                    .ConfigureAwait(false);
            }
            catch (IOException exception) when (attempt < MaxStartAttempts && IsAddressInUse(exception))
            {
                // The failed attempt has already stopped what it started.
            }
        }
    }

    /// <summary>How many times <see cref="StartAsync"/> tries fresh ports before giving up.</summary>
    private const int MaxStartAttempts = 5;

    private static bool IsAddressInUse(Exception exception) =>
        exception is Microsoft.AspNetCore.Connections.AddressInUseException
        || exception.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException
        || exception.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase);

    private static async Task<FakeSlskdHarness> StartOnceAsync(
        string scenarioJson,
        string? username,
        IAudioGenerator? generator,
        bool shareDirectory,
        string? innertubeFixtures)
    {
        var root = Path.Combine(Path.GetTempPath(), "fakeslskd-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(root, "downloads"));
        Directory.CreateDirectory(Path.Combine(root, "incomplete"));
        Directory.CreateDirectory(Path.Combine(root, "music"));

        var port = FreePort();
        var acoustIdPort = FreePort();
        var webhookPort = FreePort();
        var innertubePort = FreePort();

        var started = new List<WebApplication>();

        try
        {
            return await StartAppsAsync().ConfigureAwait(false);
        }
        catch
        {
            // A port taken from under us (or any other start failure): leave nothing listening and no
            // temporary folder behind, so the caller can try again on fresh ports.
            foreach (var app in started)
            {
                await app.StopAsync().ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }

            throw;
        }

        async Task<FakeSlskdHarness> StartAppsAsync()
        {
            var webhooks = new WebhookReceiver();
            var webhookApp = BuildWebhookReceiver(webhookPort, webhooks);
            await webhookApp.StartAsync().ConfigureAwait(false);
            started.Add(webhookApp);

            var configuration = new SlskdConfiguration
            {
                WebPort = port,
                WebIpAddress = "127.0.0.1",
                ApiKeys = [ApiKey],
                SoulseekUsername = username,
                DownloadsDirectory = Path.Combine(root, "downloads"),
                IncompleteDirectory = Path.Combine(root, "incomplete"),
                ShareDirectories = shareDirectory ? [Path.Combine(root, "music")] : [],
                Webhooks =
                [
                    new WebhookTarget(
                        "gate",
                        $"http://127.0.0.1:{webhookPort}/hook",
                        [DownloadFileCompleteEvent.EventName],
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [WebhookHeaderName] = WebhookHeaderValue,
                        }),
                ],
            };

            var options = new FakeSlskdOptions
            {
                Configuration = configuration,
                Scenario = Scenario.Parse(scenarioJson),
                AcoustIdPort = acoustIdPort,
                InnertubePort = innertubePort,
                InnertubeFixtureDir = innertubeFixtures,
                AudioGenerator = generator ?? new TestAudioGenerator(),
            };

            var state = new FakeSlskdState(options);
            var slskdApp = FakeSlskdApp.Build(options, state);
            var acoustIdApp = AcoustIdStubApp.Build(options, state);

            await slskdApp.StartAsync().ConfigureAwait(false);
            started.Add(slskdApp);
            await acoustIdApp.StartAsync().ConfigureAwait(false);
            started.Add(acoustIdApp);

            var apps = new List<WebApplication> { webhookApp, slskdApp, acoustIdApp };
            HttpClient? innertube = null;

            if (innertubeFixtures is not null)
            {
                var innertubeApp = InnertubeStubApp.Build(options, state);
                await innertubeApp.StartAsync().ConfigureAwait(false);
                started.Add(innertubeApp);
                apps.Add(innertubeApp);
                innertube = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{innertubePort}/") };
            }

            var slskd = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            slskd.DefaultRequestHeaders.Add(FakeSlskdApp.ApiKeyHeader, ApiKey);

            var acoustId = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{acoustIdPort}/") };

            var harness = new FakeSlskdHarness(root, state, slskd, acoustId, innertube, webhooks) { HasShare = shareDirectory };

            foreach (var app in apps)
            {
                harness._apps.Add(app);
            }

            return harness;
        }
    }

    /// <summary>An API-key-less client, for the 401 cases.</summary>
    public HttpClient CreateAnonymousClient() => new() { BaseAddress = Slskd.BaseAddress };

    /// <summary>Reads a document from the fake.</summary>
    /// <param name="path">The request path.</param>
    public async Task<JsonNode> GetJsonAsync(string path)
    {
        using var response = await Slskd.GetAsync(path).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {path}");

        return JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false))!;
    }

    /// <summary>Reads a document from the fake into one of the app's own response types.</summary>
    /// <param name="path">The request path.</param>
    public async Task<T> GetAsync<T>(string path)
    {
        using var response = await Slskd.GetAsync(path).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {path}");

        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions).ConfigureAwait(false))!;
    }

    /// <summary>Posts a JSON body and returns the raw response.</summary>
    /// <param name="path">The request path.</param>
    /// <param name="body">The body.</param>
    public Task<HttpResponseMessage> PostRawAsync(string path, object body) =>
        Slskd.PostAsJsonAsync(path, body, JsonOptions);

    /// <summary>Posts a JSON body and deserialises the response.</summary>
    /// <param name="path">The request path.</param>
    /// <param name="body">The body.</param>
    public async Task<T> PostAsync<T>(string path, object body)
    {
        using var response = await PostRawAsync(path, body).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"POST {path}");

        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions).ConfigureAwait(false))!;
    }

    /// <summary>Every transfer of <paramref name="username"/>, flattened out of the grouped listing.</summary>
    /// <param name="username">The peer.</param>
    public async Task<IReadOnlyList<SlskdTransferResource>> ListTransfersAsync(string username)
    {
        var users = await GetAsync<List<DownloadUserResource>>("/api/v0/transfers/downloads").ConfigureAwait(false);

        return users
            .Where(user => user.Username == username)
            .SelectMany(user => user.Directories)
            .SelectMany(directory => directory.Files)
            .ToArray();
    }

    /// <summary>Polls until <paramref name="predicate"/> holds for one of the peer's transfers.</summary>
    /// <param name="username">The peer.</param>
    /// <param name="predicate">What to wait for.</param>
    /// <param name="description">What is being waited for, for the failure message.</param>
    public async Task<SlskdTransferResource> WaitForTransferAsync(
        string username,
        Func<SlskdTransferResource, bool> predicate,
        string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            var transfer = (await ListTransfersAsync(username).ConfigureAwait(false)).FirstOrDefault(predicate);

            if (transfer is not null)
            {
                return transfer;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException($"no transfer of {username} became {description}");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.StopAsync().ConfigureAwait(false);
        }

        _state.Dispose();
        Slskd.Dispose();
        AcoustId.Dispose();

        foreach (var app in _apps)
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must not fail a test.
        }
    }

    /// <summary>A port nothing is listening on yet.</summary>
    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }

    private static WebApplication BuildWebhookReceiver(int port, WebhookReceiver receiver)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "FakeSlskdTests",
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(receiver);

        var app = builder.Build();

        app.MapPost("/hook", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);

            receiver.Record(
                context.Request.Headers.ToDictionary(
                    header => header.Key,
                    header => header.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase),
                body);

            return Results.Ok();
        });

        return app;
    }
}

/// <summary>Records what the fake posts to the configured webhook.</summary>
internal sealed class WebhookReceiver
{
    private readonly List<ReceivedWebhook> _received = [];
    private readonly Lock _gate = new();

    /// <summary>Whether a webhook has arrived.</summary>
    public bool HasReceived
    {
        get
        {
            lock (_gate)
            {
                return _received.Count > 0;
            }
        }
    }

    /// <summary>Remembers a call.</summary>
    /// <param name="headers">The request's headers.</param>
    /// <param name="body">The request's body.</param>
    public void Record(IReadOnlyDictionary<string, string> headers, string body)
    {
        lock (_gate)
        {
            _received.Add(new ReceivedWebhook(headers, body));
        }
    }

    /// <summary>Waits for the first webhook to arrive.</summary>
    /// <param name="timeout">How long to wait.</param>
    public async Task<ReceivedWebhook> WaitAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_received.Count > 0)
                {
                    return _received[0];
                }
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException("the fake never posted its webhook");
    }

    /// <summary>One webhook call.</summary>
    /// <param name="Headers">The request's headers.</param>
    /// <param name="Body">The request's body.</param>
    internal sealed record ReceivedWebhook(IReadOnlyDictionary<string, string> Headers, string Body);
}

/// <summary>
/// The generator the tests use: it writes a fixed number of bytes and returns a fingerprint derived
/// from the file's path, so each scenario file gets its own stable, predictable fingerprint. The dev
/// machine has no <c>ffmpeg</c>, so the real generator is only exercised when one is present.
/// </summary>
internal sealed class TestAudioGenerator : IAudioGenerator
{
    /// <summary>How many bytes each generated file holds.</summary>
    public const int GeneratedSize = 4096;

    /// <summary>The fingerprint the generator answers for <paramref name="path"/>.</summary>
    /// <param name="path">The file's path.</param>
    public static string FingerprintFor(string path) => $"FP:{path}";

    /// <inheritdoc />
    public Task<string> GenerateAsync(ScenarioFile file, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, new byte[GeneratedSize]);

        return Task.FromResult(FingerprintFor(file.Path));
    }
}
