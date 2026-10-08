using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.Indexers;
using Wondarr.Core.Logging;
using Wondarr.Core.Notifications;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.Indexers;

/// <summary>
/// The rules an indexer is stored under: the type exists, the protocol is the type's own (or the
/// row's choice when the type serves both), the named download client serves the same protocol, and
/// a stored secret never leaves the service unmasked.
/// </summary>
public sealed class IndexerServiceTests : IDisposable
{
    private static readonly CancellationToken Token = CancellationToken.None;

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 6, 0, 0, TimeSpan.Zero));
    private readonly FakeIndexerType _torznab = new();
    private readonly FakeChoosableIndexerType _prowlarr = new();
    private readonly RecordingSecretRegistry _secrets = new();
    private readonly WondarrDbContext _context;
    private readonly IndexerService _service;

    public IndexerServiceTests()
    {
        _database.MigrateAsync(_time).GetAwaiter().GetResult();

        // One context for the whole test: the service is scoped to a request, and a test is one.
        _context = _database.CreateContext(_time);

        _service = new IndexerService(
            _context,
            [_torznab, _prowlarr],
            _secrets,
            NullLogger<IndexerService>.Instance);
    }

    [Fact]
    public async Task An_unknown_type_is_rejected_on_the_type()
    {
        var failure = await Assert.ThrowsAsync<IndexerValidationException>(
            () => _service.CreateAsync(Draft(type: "carrier pigeon"), Token));

        failure.Field.Should().Be("type");
        failure.Detail.Should().Contain("Unknown indexer type 'carrier pigeon'");
    }

    [Fact]
    public async Task A_blank_name_is_rejected_on_the_name()
    {
        var failure = await Assert.ThrowsAsync<IndexerValidationException>(
            () => _service.CreateAsync(Draft(name: "   "), Token));

        failure.Field.Should().Be("name");
    }

    [Fact]
    public async Task A_fixed_protocol_type_decides_the_row_protocol()
    {
        var indexer = await _service.CreateAsync(Draft(protocol: DownloadProtocol.Usenet), Token);

        indexer.Protocol.Should().Be(DownloadProtocol.Torrent);
    }

    [Fact]
    public async Task A_choosable_type_without_a_protocol_is_rejected_on_the_protocol()
    {
        var failure = await Assert.ThrowsAsync<IndexerValidationException>(
            () => _service.CreateAsync(Draft(type: "prowlarr", protocol: null), Token));

        failure.Field.Should().Be("protocol");
        failure.Detail.Should().Contain("Torrent");
    }

    [Fact]
    public async Task A_choosable_type_stores_the_protocol_the_row_chose()
    {
        var indexer = await _service.CreateAsync(
            Draft(type: "prowlarr", protocol: DownloadProtocol.Usenet), Token);

        indexer.Protocol.Should().Be(DownloadProtocol.Usenet);
    }

    [Fact]
    public async Task An_indexer_naming_a_missing_client_is_rejected()
    {
        var failure = await Assert.ThrowsAsync<IndexerValidationException>(
            () => _service.CreateAsync(Draft(downloadClientId: 404), Token));

        failure.Field.Should().Be("downloadClientId");
        failure.Detail.Should().Contain("404");
    }

    [Fact]
    public async Task An_indexer_naming_a_client_of_the_other_protocol_is_rejected()
    {
        var client = await AddClientAsync(DownloadProtocol.Usenet);

        var failure = await Assert.ThrowsAsync<IndexerValidationException>(
            () => _service.CreateAsync(Draft(downloadClientId: client.Id), Token));

        failure.Field.Should().Be("downloadClientId");
        failure.Detail.Should().Contain(client.Name);
    }

    [Fact]
    public async Task An_indexer_naming_a_client_of_the_same_protocol_is_stored()
    {
        var client = await AddClientAsync(DownloadProtocol.Torrent);

        var indexer = await _service.CreateAsync(Draft(downloadClientId: client.Id), Token);

        indexer.DownloadClientId.Should().Be(client.Id);
    }

    [Fact]
    public async Task A_secret_created_once_is_kept_when_the_update_sends_the_mask_back()
    {
        var created = await _service.CreateAsync(Draft(apiKey: "wonderwall"), Token);

        NotificationSecrets.Read(created.Settings).GetProperty("apiKey").GetString()
            .Should().Be("wonderwall");

        await _service.UpdateAsync(created.Id, Draft(apiKey: NotificationSecrets.Mask), Token);

        await using var database = _database.CreateContext(_time);
        var row = await database.Indexers.SingleAsync(Token);

        NotificationSecrets.Read(row.Settings).GetProperty("apiKey").GetString().Should().Be("wonderwall");
    }

    [Fact]
    public async Task A_secret_the_update_replaces_is_the_new_one()
    {
        var created = await _service.CreateAsync(Draft(apiKey: "wonderwall"), Token);

        await _service.UpdateAsync(created.Id, Draft(apiKey: "champagne-supernova"), Token);

        await using var database = _database.CreateContext(_time);
        var row = await database.Indexers.SingleAsync(Token);

        NotificationSecrets.Read(row.Settings).GetProperty("apiKey").GetString()
            .Should().Be("champagne-supernova");
    }

    [Fact]
    public async Task A_secret_is_registered_with_the_secret_registry_on_write_and_on_read()
    {
        var created = await _service.CreateAsync(Draft(apiKey: "wonderwall"), Token);

        _secrets.Registered.Should().Contain("wonderwall");

        _secrets.Registered.Clear();

        await _service.GetAsync(created.Id, Token);

        _secrets.Registered.Should().Contain("wonderwall");
    }

    [Fact]
    public async Task A_test_with_an_id_merges_the_stored_secret_before_connecting()
    {
        var created = await _service.CreateAsync(Draft(apiKey: "wonderwall"), Token);

        var result = await _service.TestAsync(
            Draft(apiKey: NotificationSecrets.Mask, id: created.Id), Token);

        result.Success.Should().BeTrue();
        _torznab.Tested.Should().ContainSingle();
        _torznab.Tested[0].GetProperty("apiKey").GetString().Should().Be("wonderwall");
    }

    [Fact]
    public async Task A_test_without_an_id_uses_the_settings_as_they_are()
    {
        await _service.TestAsync(Draft(apiKey: "wonderwall"), Token);

        _torznab.Tested.Should().ContainSingle();
        _torznab.Tested[0].GetProperty("apiKey").GetString().Should().Be("wonderwall");
    }

    [Fact]
    public async Task A_type_that_throws_is_a_failed_test_never_an_error()
    {
        _torznab.Throw = new InvalidOperationException("the indexer answered HTTP 500.");

        var result = await _service.TestAsync(Draft(), Token);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("the indexer answered HTTP 500.");
    }

    [Fact]
    public async Task A_settings_value_the_type_refuses_is_rejected_on_the_settings()
    {
        var failure = await Assert.ThrowsAsync<IndexerValidationException>(
            () => _service.CreateAsync(Draft(baseUrl: "ftp://indexer.local"), Token));

        failure.Field.Should().Be("settings");
        failure.Detail.Should().Contain("http");
    }

    /// <summary>Deleting an indexer that is not there is not an error, it is a miss.</summary>
    [Fact]
    public async Task Deleting_a_missing_indexer_reports_a_miss()
    {
        (await _service.DeleteAsync(404, Token)).Should().BeFalse();

        var created = await _service.CreateAsync(Draft(), Token);

        (await _service.DeleteAsync(created.Id, Token)).Should().BeTrue();
        (await _service.ListAsync(Token)).Should().BeEmpty();
    }

    /// <inheritdoc />
    public void Dispose() => _database.Dispose();

    private async Task<DownloadClient> AddClientAsync(DownloadProtocol protocol, string name = "Grabber")
    {
        var client = new DownloadClient
        {
            Name = name,
            Type = "fake",
            Protocol = protocol,
            Settings = "{}",
            Enabled = true,
            Priority = 1,
        };

        _context.DownloadClients.Add(client);
        await _context.SaveChangesAsync(Token);

        return client;
    }

    private static IndexerDraft Draft(
        string name = "Jackett",
        string type = "torznab",
        DownloadProtocol? protocol = DownloadProtocol.Usenet,
        string baseUrl = "http://indexer.local/torznab",
        string apiKey = "",
        long? downloadClientId = null,
        long? id = null) =>
        new(
            name,
            type,
            protocol,
            Enabled: true,
            Priority: 25,
            downloadClientId,
            JsonSerializer.Deserialize<JsonElement>(
                $$"""{"baseUrl":"{{baseUrl}}","apiKey":"{{apiKey}}"}"""),
            id);

    /// <summary>An indexer type with a fixed protocol, one plain field and one secret.</summary>
    private sealed class FakeIndexerType : IIndexerType
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

        public IReadOnlyList<string> Validate(JsonElement settings)
        {
            var url = settings.TryGetProperty("baseUrl", out var value) ? value.GetString() : null;

            return url is not null && url.StartsWith("http", StringComparison.Ordinal)
                ? []
                : ["The base URL must be an absolute http or https address."];
        }

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
    private sealed class FakeChoosableIndexerType : IIndexerType
    {
        public string Type => "prowlarr";

        public DownloadProtocol? Protocol => null;

        public IReadOnlyList<NotificationField> Fields { get; } =
        [
            new("baseUrl", "Prowlarr URL", "url", Required: true),
            new("apiKey", "API Key", "password", Required: false, Secret: true),
        ];

        public IReadOnlyList<string> Validate(JsonElement settings) => [];

        public Task<ProviderTestResult> TestAsync(JsonElement settings, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTestResult(true, null));
    }

    /// <summary>Remembers what it was asked to redact, so a test can assert on the registrations.</summary>
    private sealed class RecordingSecretRegistry : ISecretRegistry
    {
        /// <summary>Every value registered, in order.</summary>
        public List<string> Registered { get; } = [];

        public void Register(string? secret)
        {
            if (secret is not null)
            {
                Registered.Add(secret);
            }
        }

        public string Redact(string text) => text;
    }
}
