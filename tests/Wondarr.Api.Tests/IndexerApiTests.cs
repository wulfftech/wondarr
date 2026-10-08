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
/// The indexer endpoints: the CRUD round trip, the type schema the editor is rendered from, the
/// connection test, and the one thing that must never come back out of the API — a stored secret.
/// <para>
/// The types are fakes registered into the test host; nothing here touches the network.
/// </para>
/// </summary>
public sealed class IndexerApiTests
{
    private const string Endpoint = "/api/v1/indexer";
    private const string ClientEndpoint = "/api/v1/downloadclient";

    [Fact]
    public async Task An_indexer_is_created_read_updated_and_deleted()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        using var created = await client.PostAsync(
            new Uri(Endpoint, UriKind.Relative),
            Body(new
            {
                name = "Jackett",
                type = "torznab",
                enabled = true,
                priority = 10,
                settings = new { baseUrl = "http://indexer.local/torznab", apiKey = "wonderwall" },
            }));

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var row = await ReadJsonAsync(created);
        var id = row.GetProperty("id").GetInt64();

        row.GetProperty("name").GetString().Should().Be("Jackett");
        row.GetProperty("type").GetString().Should().Be("torznab");
        row.GetProperty("protocol").GetString().Should().Be("Torrent");
        row.GetProperty("priority").GetInt32().Should().Be(10);
        row.GetProperty("settings").GetProperty("baseUrl").GetString()
            .Should().Be("http://indexer.local/torznab");

        using var fetched = await client.GetAsync(new Uri($"{Endpoint}/{id}", UriKind.Relative));

        fetched.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(fetched)).GetProperty("name").GetString().Should().Be("Jackett");

        using var updated = await client.PutAsync(
            new Uri($"{Endpoint}/{id}", UriKind.Relative),
            Body(new
            {
                name = "Jackett",
                type = "torznab",
                enabled = false,
                priority = 5,
                settings = new { baseUrl = "http://indexer.local/torznab", apiKey = "********" },
            }));

        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        var changed = await ReadJsonAsync(updated);
        changed.GetProperty("enabled").GetBoolean().Should().BeFalse();
        changed.GetProperty("priority").GetInt32().Should().Be(5);

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

        var id = await CreateAsync(client, apiKey: "wonderwall");

        using var fetched = await client.GetAsync(new Uri($"{Endpoint}/{id}", UriKind.Relative));
        var settings = (await ReadJsonAsync(fetched)).GetProperty("settings");

        settings.GetProperty("apiKey").GetString().Should().Be("********");
        settings.GetProperty("apiKey").GetString().Should().NotBe("wonderwall");

        using var page = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));
        var listed = await ReadJsonAsync(page);

        listed[0].GetProperty("settings").GetProperty("apiKey").GetString().Should().Be("********");
    }

    [Fact]
    public async Task An_update_with_the_mask_keeps_the_stored_key()
    {
        var torznab = new FakeTorznabType();
        using var factory = Factory(torznab);
        using var client = Authenticated(factory);

        var id = await CreateAsync(client, apiKey: "wonderwall");

        using var updated = await client.PutAsync(
            new Uri($"{Endpoint}/{id}", UriKind.Relative),
            Body(new
            {
                name = "Jackett",
                type = "torznab",
                settings = new { baseUrl = "http://indexer.local/torznab", apiKey = "********" },
            }));

        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        // The proof: a test against the row, still carrying the mask, reaches the type with the key.
        var tested = await TestAsync(client, id, apiKey: "********");

        tested.Success.Should().BeTrue();
        torznab.Tested.Should().HaveCount(1);
        torznab.Tested[0].GetProperty("apiKey").GetString().Should().Be("wonderwall");
    }

    [Fact]
    public async Task A_test_with_an_id_and_the_mask_tests_with_the_stored_key()
    {
        var torznab = new FakeTorznabType();
        using var factory = Factory(torznab);
        using var client = Authenticated(factory);

        var id = await CreateAsync(client, apiKey: "wonderwall");

        var tested = await TestAsync(client, id, apiKey: "********");

        tested.Success.Should().BeTrue();
        torznab.Tested.Should().HaveCount(1);
        torznab.Tested[0].GetProperty("apiKey").GetString().Should().Be("wonderwall");
    }

    [Fact]
    public async Task A_type_that_throws_answers_200_with_the_failure()
    {
        var torznab = new FakeTorznabType
        {
            Throw = new InvalidOperationException("the indexer answered HTTP 500."),
        };
        using var factory = Factory(torznab);
        using var client = Authenticated(factory);

        using var response = await client.PostAsync(
            new Uri($"{Endpoint}/test", UriKind.Relative),
            Body(new
            {
                name = "Jackett",
                type = "torznab",
                settings = new { baseUrl = "http://indexer.local/torznab" },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadJsonAsync(response);

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Be("the indexer answered HTTP 500.");
    }

    [Fact]
    public async Task An_unknown_type_is_a_validation_problem()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        using var response = await client.PostAsync(
            new Uri(Endpoint, UriKind.Relative),
            Body(new { name = "Jackett", type = "carrier pigeon", settings = new { } }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ReadJsonAsync(response);
        var message = problem.GetProperty("errors").GetProperty("type")[0].GetString();

        message.Should().Contain("Unknown indexer type 'carrier pigeon'");
    }

    [Fact]
    public async Task An_indexer_naming_a_client_of_the_other_protocol_is_a_validation_problem()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        var clientId = await CreateClientAsync(client, "sabnzbd");

        using var response = await client.PostAsync(
            new Uri(Endpoint, UriKind.Relative),
            Body(new
            {
                name = "Jackett",
                type = "torznab",
                settings = new { baseUrl = "http://indexer.local/torznab" },
                downloadClientId = clientId,
            }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ReadJsonAsync(response);
        var message = problem.GetProperty("errors").GetProperty("downloadClientId")[0].GetString();

        message.Should().Contain("Usenet");
    }

    [Fact]
    public async Task A_choosable_type_without_a_protocol_is_a_validation_problem()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        using var response = await client.PostAsync(
            new Uri(Endpoint, UriKind.Relative),
            Body(new
            {
                name = "Prowlarr",
                type = "prowlarr",
                settings = new { baseUrl = "http://prowlarr.local" },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ReadJsonAsync(response);
        var message = problem.GetProperty("errors").GetProperty("protocol")[0].GetString();

        message.Should().Contain("Torrent");
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
            .Should().BeEquivalentTo(["torznab", "prowlarr"]);

        var torznab = types.Single(entry => entry.GetProperty("type").GetString() == "torznab");

        torznab.GetProperty("protocol").GetString().Should().Be("Torrent");
        torznab.GetProperty("protocolChoosable").GetBoolean().Should().BeFalse();
        torznab.GetProperty("fields").EnumerateArray()
            .Select(field => field.GetProperty("name").GetString())
            .Should().Equal("baseUrl", "apiKey");

        var prowlarr = types.Single(entry => entry.GetProperty("type").GetString() == "prowlarr");

        prowlarr.GetProperty("protocol").GetRawText().Should().Be("null");
        prowlarr.GetProperty("protocolChoosable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_indexer_endpoints_require_a_key()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var list = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));
        using var row = await client.GetAsync(new Uri($"{Endpoint}/1", UriKind.Relative));
        using var schema = await client.GetAsync(new Uri($"{Endpoint}/schema", UriKind.Relative));
        using var created = await client.PostAsync(
            new Uri(Endpoint, UriKind.Relative),
            Body(new { name = "Jackett", type = "torznab" }));
        using var updated = await client.PutAsync(
            new Uri($"{Endpoint}/1", UriKind.Relative),
            Body(new { name = "Jackett", type = "torznab" }));
        using var deleted = await client.DeleteAsync(new Uri($"{Endpoint}/1", UriKind.Relative));
        using var test = await client.PostAsync(
            new Uri($"{Endpoint}/test", UriKind.Relative),
            Body(new { name = "Jackett", type = "torznab" }));

        list.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        row.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        schema.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        created.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        updated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        deleted.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        test.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>A host with the fake indexer and client types registered, as the source projects will.</summary>
    private static WondarrAppFactory Factory(FakeTorznabType? torznab = null) =>
        new(configureServices: services =>
        {
            services.AddSingleton<IIndexerType>(torznab ?? new FakeTorznabType());
            services.AddSingleton<IIndexerType>(new FakeProwlarrType());
            services.AddSingleton<IDownloadClientType>(new FakeQbittorrentType());
            services.AddSingleton<IDownloadClientType>(new FakeSabnzbdType());
        });

    private static HttpClient Authenticated(WondarrAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        return client;
    }

    private static async Task<long> CreateAsync(HttpClient client, string apiKey = "")
    {
        using var response = await client.PostAsync(
            new Uri(Endpoint, UriKind.Relative),
            Body(new
            {
                name = "Jackett",
                type = "torznab",
                settings = new { baseUrl = "http://indexer.local/torznab", apiKey },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await ReadJsonAsync(response)).GetProperty("id").GetInt64();
    }

    private static async Task<long> CreateClientAsync(HttpClient client, string type)
    {
        using var response = await client.PostAsync(
            new Uri(ClientEndpoint, UriKind.Relative),
            Body(new { name = "Grabber", type, settings = new { url = "http://downloads.local" } }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await ReadJsonAsync(response)).GetProperty("id").GetInt64();
    }

    private static async Task<(bool Success, string? Error)> TestAsync(HttpClient client, long id, string apiKey)
    {
        using var response = await client.PostAsync(
            new Uri($"{Endpoint}/test", UriKind.Relative),
            Body(new
            {
                name = "Jackett",
                type = "torznab",
                id,
                settings = new { baseUrl = "http://indexer.local/torznab", apiKey },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadJsonAsync(response);

        return (result.GetProperty("success").GetBoolean(), result.GetProperty("error").GetString());
    }

    private static StringContent Body(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    /// <summary>An indexer type with a fixed Torrent protocol, one plain field and one secret.</summary>
    private sealed class FakeTorznabType : IIndexerType
    {
        /// <summary>The settings this type was tested with, in order.</summary>
        public List<JsonElement> Tested { get; } = [];

        public string Type => "torznab";

        public DownloadProtocol? Protocol => DownloadProtocol.Torrent;

        public IReadOnlyList<NotificationField> Fields { get; } =
        [
            new("baseUrl", "Base URL", "url", Required: true),
            new("apiKey", "API Key", "password", Required: false, Secret: true),
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

    /// <summary>An indexer type that serves both protocols, so the row chooses.</summary>
    private sealed class FakeProwlarrType : IIndexerType
    {
        public string Type => "prowlarr";

        public DownloadProtocol? Protocol => null;

        public IReadOnlyList<NotificationField> Fields { get; } =
        [
            new("baseUrl", "Prowlarr URL", "url", Required: true),
        ];

        public IReadOnlyList<string> Validate(JsonElement settings) => [];

        public Task<ProviderTestResult> TestAsync(JsonElement settings, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTestResult(true, null));
    }

    /// <summary>A torrent download client type.</summary>
    private sealed class FakeQbittorrentType : IDownloadClientType
    {
        public string Type => "qbittorrent";

        public DownloadProtocol Protocol => DownloadProtocol.Torrent;

        public IReadOnlyList<NotificationField> Fields { get; } =
        [
            new("url", "URL", "url", Required: true),
        ];

        public IReadOnlyList<string> Validate(JsonElement settings) => [];

        public Task<ProviderTestResult> TestAsync(JsonElement settings, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTestResult(true, null));
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
}
