using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.Indexers;
using Wondarr.Core.Notifications;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The download client endpoints: the CRUD round trip, the type schema, the connection test, the
/// secret that never comes back out, and the <c>409</c> that protects an indexer's client.
/// <para>
/// The types are fakes registered into the test host; nothing here touches the network.
/// </para>
/// </summary>
public sealed class DownloadClientApiTests
{
    private const string Endpoint = "/api/v1/downloadclient";
    private const string IndexerEndpoint = "/api/v1/indexer";

    [Fact]
    public async Task A_client_is_created_read_updated_and_deleted()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        using var created = await client.PostAsync(
            new Uri(Endpoint, UriKind.Relative),
            Body(new
            {
                name = "qBittorrent",
                type = "qbittorrent",
                enabled = true,
                priority = 1,
                settings = new { url = "http://downloads.local:8080", password = "wonderwall" },
            }));

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var row = await ReadJsonAsync(created);
        var id = row.GetProperty("id").GetInt64();

        row.GetProperty("name").GetString().Should().Be("qBittorrent");
        row.GetProperty("type").GetString().Should().Be("qbittorrent");
        row.GetProperty("protocol").GetString().Should().Be("Torrent");
        row.GetProperty("priority").GetInt32().Should().Be(1);

        using var fetched = await client.GetAsync(new Uri($"{Endpoint}/{id}", UriKind.Relative));

        fetched.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(fetched)).GetProperty("name").GetString().Should().Be("qBittorrent");

        using var updated = await client.PutAsync(
            new Uri($"{Endpoint}/{id}", UriKind.Relative),
            Body(new
            {
                name = "qBittorrent",
                type = "qbittorrent",
                enabled = false,
                settings = new { url = "http://downloads.local:8080", password = "********" },
            }));

        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        var changed = await ReadJsonAsync(updated);
        changed.GetProperty("enabled").GetBoolean().Should().BeFalse();

        using var deleted = await client.DeleteAsync(new Uri($"{Endpoint}/{id}", UriKind.Relative));

        deleted.StatusCode.Should().Be(HttpStatusCode.OK);

        using var gone = await client.GetAsync(new Uri($"{Endpoint}/{id}", UriKind.Relative));

        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_stored_secret_never_comes_back_out()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        var id = await CreateAsync(client, password: "wonderwall");

        using var fetched = await client.GetAsync(new Uri($"{Endpoint}/{id}", UriKind.Relative));
        var settings = (await ReadJsonAsync(fetched)).GetProperty("settings");

        settings.GetProperty("password").GetString().Should().Be("********");
        settings.GetProperty("password").GetString().Should().NotBe("wonderwall");

        using var page = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));
        var listed = await ReadJsonAsync(page);

        listed[0].GetProperty("settings").GetProperty("password").GetString().Should().Be("********");
    }

    [Fact]
    public async Task A_test_with_an_id_and_the_mask_tests_with_the_stored_password()
    {
        var qbittorrent = new FakeQbittorrentType();
        using var factory = Factory(qbittorrent);
        using var client = Authenticated(factory);

        var id = await CreateAsync(client, password: "wonderwall");

        using var response = await client.PostAsync(
            new Uri($"{Endpoint}/test", UriKind.Relative),
            Body(new
            {
                name = "qBittorrent",
                type = "qbittorrent",
                id,
                settings = new { url = "http://downloads.local:8080", password = "********" },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadJsonAsync(response);

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        qbittorrent.Tested.Should().HaveCount(1);
        qbittorrent.Tested[0].GetProperty("password").GetString().Should().Be("wonderwall");
    }

    [Fact]
    public async Task A_type_that_throws_answers_200_with_the_failure()
    {
        var qbittorrent = new FakeQbittorrentType
        {
            Throw = new InvalidOperationException("the client answered HTTP 500."),
        };
        using var factory = Factory(qbittorrent);
        using var client = Authenticated(factory);

        using var response = await client.PostAsync(
            new Uri($"{Endpoint}/test", UriKind.Relative),
            Body(new
            {
                name = "qBittorrent",
                type = "qbittorrent",
                settings = new { url = "http://downloads.local:8080" },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadJsonAsync(response);

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Be("the client answered HTTP 500.");
    }

    [Fact]
    public async Task An_unknown_type_is_a_validation_problem()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        using var response = await client.PostAsync(
            new Uri(Endpoint, UriKind.Relative),
            Body(new { name = "Grabber", type = "carrier pigeon", settings = new { } }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ReadJsonAsync(response);
        var message = problem.GetProperty("errors").GetProperty("type")[0].GetString();

        message.Should().Contain("Unknown download client type 'carrier pigeon'");
    }

    [Fact]
    public async Task Deleting_a_client_an_indexer_names_is_a_conflict_naming_the_indexer()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        var clientId = await CreateAsync(client);
        var indexerId = await CreateIndexerAsync(client, clientId);

        using var refused = await client.DeleteAsync(new Uri($"{Endpoint}/{clientId}", UriKind.Relative));

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await refused.Content.ReadAsStringAsync();
        problem.Should().Contain("Jackett").And.Contain("\"title\":\"Download client in use\"");

        using var indexerDeleted = await client.DeleteAsync(
            new Uri($"{IndexerEndpoint}/{indexerId}", UriKind.Relative));

        indexerDeleted.StatusCode.Should().Be(HttpStatusCode.OK);

        using var clientDeleted = await client.DeleteAsync(new Uri($"{Endpoint}/{clientId}", UriKind.Relative));

        clientDeleted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_schema_lists_the_registered_types_with_their_protocols()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(new Uri($"{Endpoint}/schema", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var schema = await ReadJsonAsync(response);
        var types = schema.EnumerateArray().ToList();

        types.Select(entry => entry.GetProperty("type").GetString())
            .Should().BeEquivalentTo(["qbittorrent", "sabnzbd"]);

        var qbittorrent = types.Single(entry => entry.GetProperty("type").GetString() == "qbittorrent");

        qbittorrent.GetProperty("protocol").GetString().Should().Be("Torrent");
        qbittorrent.GetProperty("fields").EnumerateArray()
            .Select(field => field.GetProperty("name").GetString())
            .Should().Equal("url", "password");

        types.Single(entry => entry.GetProperty("type").GetString() == "sabnzbd")
            .GetProperty("protocol").GetString().Should().Be("Usenet");
    }

    [Fact]
    public async Task The_download_client_endpoints_require_a_key()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var list = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));
        using var row = await client.GetAsync(new Uri($"{Endpoint}/1", UriKind.Relative));
        using var schema = await client.GetAsync(new Uri($"{Endpoint}/schema", UriKind.Relative));
        using var created = await client.PostAsync(
            new Uri(Endpoint, UriKind.Relative),
            Body(new { name = "Grabber", type = "qbittorrent" }));
        using var updated = await client.PutAsync(
            new Uri($"{Endpoint}/1", UriKind.Relative),
            Body(new { name = "Grabber", type = "qbittorrent" }));
        using var deleted = await client.DeleteAsync(new Uri($"{Endpoint}/1", UriKind.Relative));
        using var test = await client.PostAsync(
            new Uri($"{Endpoint}/test", UriKind.Relative),
            Body(new { name = "Grabber", type = "qbittorrent" }));

        list.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        row.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        schema.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        created.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        updated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        deleted.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        test.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>A host with the fake client and indexer types registered, as the source projects will.</summary>
    private static WondarrAppFactory Factory(FakeQbittorrentType? qbittorrent = null) =>
        new(configureServices: services =>
        {
            services.AddSingleton<IDownloadClientType>(qbittorrent ?? new FakeQbittorrentType());
            services.AddSingleton<IDownloadClientType>(new FakeSabnzbdType());
            services.AddSingleton<IIndexerType>(new FakeTorznabType());
        });

    private static HttpClient Authenticated(WondarrAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        return client;
    }

    private static async Task<long> CreateAsync(HttpClient client, string password = "")
    {
        using var response = await client.PostAsync(
            new Uri(Endpoint, UriKind.Relative),
            Body(new
            {
                name = "qBittorrent",
                type = "qbittorrent",
                settings = new { url = "http://downloads.local:8080", password },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await ReadJsonAsync(response)).GetProperty("id").GetInt64();
    }

    private static async Task<long> CreateIndexerAsync(HttpClient client, long clientId)
    {
        using var response = await client.PostAsync(
            new Uri(IndexerEndpoint, UriKind.Relative),
            Body(new
            {
                name = "Jackett",
                type = "torznab",
                settings = new { baseUrl = "http://indexer.local/torznab" },
                downloadClientId = clientId,
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await ReadJsonAsync(response)).GetProperty("id").GetInt64();
    }

    private static StringContent Body(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    /// <summary>A torrent download client type with one plain field and one secret.</summary>
    private sealed class FakeQbittorrentType : IDownloadClientType
    {
        /// <summary>The settings this type was tested with, in order.</summary>
        public List<JsonElement> Tested { get; } = [];

        public string Type => "qbittorrent";

        public DownloadProtocol Protocol => DownloadProtocol.Torrent;

        public IReadOnlyList<NotificationField> Fields { get; } =
        [
            new("url", "URL", "url", Required: true),
            new("password", "Password", "password", Required: false, Secret: true),
        ];

        /// <summary>Thrown by every test when set.</summary>
        public Exception? Throw { get; set; }

        public IReadOnlyList<string> Validate(JsonElement settings) => [];

        public Task<ProviderTestResult> TestAsync(JsonElement settings, CancellationToken cancellationToken)
        {
            Tested.Add(settings);

            if (Throw is { } failure)
            {
                throw failure;
            }

            return Task.FromResult(new ProviderTestResult(true, null));
        }
    }

    /// <summary>A usenet download client type.</summary>
    private sealed class FakeSabnzbdType : IDownloadClientType
    {
        public string Type => "sabnzbd";

        public DownloadProtocol Protocol => DownloadProtocol.Usenet;

        public IReadOnlyList<NotificationField> Fields { get; } =
        [
            new("url", "URL", "url", Required: true),
        ];

        public IReadOnlyList<string> Validate(JsonElement settings) => [];

        public Task<ProviderTestResult> TestAsync(JsonElement settings, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTestResult(true, null));
    }

    /// <summary>An indexer type with a fixed Torrent protocol, so an indexer can name the client.</summary>
    private sealed class FakeTorznabType : IIndexerType
    {
        public string Type => "torznab";

        public DownloadProtocol? Protocol => DownloadProtocol.Torrent;

        public IReadOnlyList<NotificationField> Fields { get; } =
        [
            new("baseUrl", "Base URL", "url", Required: true),
        ];

        public IReadOnlyList<string> Validate(JsonElement settings) => [];

        public Task<ProviderTestResult> TestAsync(JsonElement settings, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTestResult(true, null));
    }
}
