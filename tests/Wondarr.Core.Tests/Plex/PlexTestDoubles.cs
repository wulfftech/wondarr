using System.Net;
using System.Text;
using Wondarr.Core.Logging;
using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;
using Wondarr.Core.Tests.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Wondarr.Core.Tests.Plex;

/// <summary>The responses recorded from a real plex.tv and Plex Media Server (see tests/fixtures/plex).</summary>
internal static class PlexFixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "plex", name));

    public static HttpResponseMessage Json(string fixture, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(Read(fixture), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Body(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Empty(HttpStatusCode status = HttpStatusCode.OK) => new(status);
}

/// <summary>Serves one scripted answer per request and records what it was asked.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responder = responder;

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);

        return Task.FromResult(_responder(request));
    }
}

/// <summary>Answers only once the client's timeout has fired.</summary>
internal sealed class SlowHttpMessageHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);

        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

/// <summary>Hands out clients built on one stub handler, standing in for <c>IHttpClientFactory</c>.</summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    private readonly TimeSpan _timeout;

    public StubHttpClientFactory(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        _handler = handler;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public HttpClient CreateClient(string name) =>
        new(_handler, disposeHandler: false) { Timeout = _timeout };
}

/// <summary>A settings-backed Plex connection, a fake clock and substituted Plex clients.</summary>
internal sealed class PlexConnectionHarness : IDisposable
{
    private readonly List<WondarrDbContext> _contexts = [];

    public PlexConnectionHarness()
    {
        Database = new SqliteTestDatabase();
        Database.MigrateAsync(Time).GetAwaiter().GetResult();
    }

    public SqliteTestDatabase Database { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));

    public IPlexTvClient Tv { get; } = Substitute.For<IPlexTvClient>();

    public IPlexServerClient Server { get; } = Substitute.For<IPlexServerClient>();

    public SecretRegistry Secrets { get; } = new();

    /// <summary>The identifier service over a fresh context, the way one scope of the app gets it.</summary>
    public IPlexClientIdentifier CreateIdentifier() => new PlexClientIdentifier(NewRepository(), IdentifierState);

    /// <summary>The app-wide identifier state, shared by every scope this fixture hands out.</summary>
    public PlexClientIdentifierState IdentifierState { get; } = new();

    public PlexConnectionService CreateService(
        IPlexTvClient? tv = null,
        IPlexServerClient? server = null,
        IPlexClientIdentifier? identifier = null) =>
        new(
            NewRepository(),
            identifier ?? CreateIdentifier(),
            tv ?? Tv,
            server ?? Server,
            Secrets,
            NullLogger<PlexConnectionService>.Instance);

    public Task<PlexConnectionSettings?> ReadSettingsAsync() =>
        NewRepository().GetAsync<PlexConnectionSettings>(PlexConnectionService.SettingKey, CancellationToken.None);

    public void Dispose()
    {
        foreach (var context in _contexts)
        {
            context.Dispose();
        }

        Database.Dispose();
    }

    private SettingsRepository NewRepository()
    {
        var context = Database.CreateContext(Time);
        _contexts.Add(context);

        return new SettingsRepository(context);
    }
}
