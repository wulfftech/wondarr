using System.Net;
using Wondarr.Core.Persistence;
using Wondarr.Sources.Slskd;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>In-memory <see cref="ISettingsRepository"/>, so tests never touch the database file.</summary>
internal sealed class InMemorySettingsRepository : ISettingsRepository
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_values.TryGetValue(key, out var value) ? (T?)value : default);

    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_values.Remove(key));
}

/// <summary>Serves a canned response, recording the requests it was given.</summary>
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

    public static StubHttpMessageHandler Status(HttpStatusCode status) => new(status, string.Empty);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null
            ? string.Empty
            : request.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult());

        var response = new HttpResponseMessage(_status)
        {
            RequestMessage = request,
            Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
        };

        return Task.FromResult(response);
    }
}

/// <summary>Shared builders for the slskd tests.</summary>
internal static class SlskdTestData
{
    public static readonly SlskdRuntimeSecrets Secrets =
        new(new string('a', 64), "wondarr", new string('b', 64));

    /// <summary>A repository already holding <see cref="Secrets"/>, so a client test knows the key.</summary>
    public static InMemorySettingsRepository RepositoryWithRuntimeSecrets()
    {
        var repository = new InMemorySettingsRepository();
        repository.SetAsync(SlskdSecretsStore.SettingKey, Secrets, CancellationToken.None).GetAwaiter().GetResult();
        return repository;
    }

    public static IOptionsMonitor<SoulseekOptions> Monitor(SoulseekOptions options)
    {
        var monitor = Substitute.For<IOptionsMonitor<SoulseekOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    public static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "slskd", name);

    public static string ReadFixture(string name) => File.ReadAllText(FixturePath(name));

    /// <summary>
    /// Compares <paramref name="actual"/> with the checked-in golden file <paramref name="name"/>,
    /// byte for byte. Set <c>WONDARR_UPDATE_FIXTURES=1</c> to rewrite the golden files instead —
    /// that is how they were produced, and it is the only way they change.
    /// </summary>
    public static void AssertMatchesGolden(string name, string actual)
    {
        var path = Path.Combine(SourceFixtureDirectory(), name);

        if (Environment.GetEnvironmentVariable("WONDARR_UPDATE_FIXTURES") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        File.Exists(path).Should().BeTrue(
            $"{name} is missing; regenerate the golden files with WONDARR_UPDATE_FIXTURES=1");

        File.ReadAllText(path).Should().Be(actual, $"{name} is a golden file and must match byte for byte");
    }

    /// <summary>Walks up from the test output directory to the checked-in fixture directory.</summary>
    private static string SourceFixtureDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "fixtures", "slskd");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not find tests/fixtures/slskd above {AppContext.BaseDirectory}");
    }

    /// <summary>Parses a rendered document so a test can assert on structure, not on text.</summary>
    public static Dictionary<object, object> Parse(string yaml) =>
        new YamlDotNet.Serialization.DeserializerBuilder()
            .Build()
            .Deserialize<Dictionary<object, object>>(yaml);

    /// <summary>Returns one top-level section of a rendered document.</summary>
    public static Dictionary<object, object> Section(string yaml, string name) =>
        (Dictionary<object, object>)Parse(yaml)[name];
}
