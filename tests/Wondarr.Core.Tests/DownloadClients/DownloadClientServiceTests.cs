using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.Notifications;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.DownloadClients;

/// <summary>
/// The rules a download client is stored under, and the one rule that protects the rows pointing at
/// it: a client an indexer names is not deleted while the indexer exists.
/// </summary>
public sealed class DownloadClientServiceTests : IDisposable
{
    private static readonly CancellationToken Token = CancellationToken.None;

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 6, 0, 0, TimeSpan.Zero));
    private readonly FakeDownloadClientType _qbittorrent = new();
    private readonly WondarrDbContext _context;
    private readonly DownloadClientService _service;

    public DownloadClientServiceTests()
    {
        _database.MigrateAsync(_time).GetAwaiter().GetResult();

        // One context for the whole test: the service is scoped to a request, and a test is one.
        _context = _database.CreateContext(_time);

        _service = new DownloadClientService(
            _context,
            [_qbittorrent],
            new NoopSecretRegistry(),
            NullLogger<DownloadClientService>.Instance);
    }

    [Fact]
    public async Task An_unknown_type_is_rejected_on_the_type()
    {
        var failure = await Assert.ThrowsAsync<DownloadClientValidationException>(
            () => _service.CreateAsync(Draft(type: "carrier pigeon"), Token));

        failure.Field.Should().Be("type");
        failure.Detail.Should().Contain("Unknown download client type 'carrier pigeon'");
    }

    [Fact]
    public async Task A_blank_name_is_rejected_on_the_name()
    {
        var failure = await Assert.ThrowsAsync<DownloadClientValidationException>(
            () => _service.CreateAsync(Draft(name: "   "), Token));

        failure.Field.Should().Be("name");
    }

    [Fact]
    public async Task The_type_decides_the_protocol_and_the_default_priority_is_one()
    {
        var client = await _service.CreateAsync(Draft(), Token);

        client.Protocol.Should().Be(DownloadProtocol.Torrent);
        client.Priority.Should().Be(1);
    }

    [Fact]
    public async Task A_secret_created_once_is_kept_when_the_update_sends_the_mask_back()
    {
        var created = await _service.CreateAsync(Draft(password: "wonderwall"), Token);

        NotificationSecrets.Read(created.Settings).GetProperty("password").GetString()
            .Should().Be("wonderwall");

        await _service.UpdateAsync(created.Id, Draft(password: NotificationSecrets.Mask), Token);

        await using var database = _database.CreateContext(_time);
        var row = await database.DownloadClients.SingleAsync(Token);

        NotificationSecrets.Read(row.Settings).GetProperty("password").GetString().Should().Be("wonderwall");
    }

    [Fact]
    public async Task A_test_with_an_id_merges_the_stored_secret_before_connecting()
    {
        var created = await _service.CreateAsync(Draft(password: "wonderwall"), Token);

        var result = await _service.TestAsync(
            Draft(password: NotificationSecrets.Mask, id: created.Id), Token);

        result.Success.Should().BeTrue();
        _qbittorrent.Tested.Should().ContainSingle();
        _qbittorrent.Tested[0].GetProperty("password").GetString().Should().Be("wonderwall");
    }

    [Fact]
    public async Task A_type_that_throws_is_a_failed_test_never_an_error()
    {
        _qbittorrent.Throw = new InvalidOperationException("the client answered HTTP 500.");

        var result = await _service.TestAsync(Draft(), Token);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("the client answered HTTP 500.");
    }

    [Fact]
    public async Task Deleting_a_client_an_indexer_names_is_refused_with_the_indexers_names()
    {
        var client = await _service.CreateAsync(Draft(), Token);
        await AddIndexerAsync("Jackett", client.Id);

        var failure = await Assert.ThrowsAsync<DownloadClientInUseException>(
            () => _service.DeleteAsync(client.Id, Token));

        failure.IndexerNames.Should().Equal("Jackett");
        failure.Message.Should().Contain("Jackett");

        (await _service.ListAsync(Token)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_client_deletes_once_no_indexer_names_it()
    {
        var client = await _service.CreateAsync(Draft(), Token);
        var indexer = await AddIndexerAsync("Jackett", client.Id);

        await Assert.ThrowsAsync<DownloadClientInUseException>(
            () => _service.DeleteAsync(client.Id, Token));

        _context.Indexers.Remove(indexer);
        await _context.SaveChangesAsync(Token);

        (await _service.DeleteAsync(client.Id, Token)).Should().BeTrue();
        (await _service.ListAsync(Token)).Should().BeEmpty();
    }

    /// <summary>Deleting a client that is not there is not an error, it is a miss.</summary>
    [Fact]
    public async Task Deleting_a_missing_client_reports_a_miss()
    {
        (await _service.DeleteAsync(404, Token)).Should().BeFalse();
    }

    /// <inheritdoc />
    public void Dispose() => _database.Dispose();

    private async Task<Indexer> AddIndexerAsync(string name, long clientId)
    {
        var indexer = new Indexer
        {
            Name = name,
            Type = "torznab",
            Protocol = DownloadProtocol.Torrent,
            Settings = "{}",
            Enabled = true,
            Priority = 25,
            DownloadClientId = clientId,
        };

        _context.Indexers.Add(indexer);
        await _context.SaveChangesAsync(Token);

        return indexer;
    }

    private static DownloadClientDraft Draft(
        string name = "qBittorrent",
        string type = "qbittorrent",
        string url = "http://downloads.local:8080",
        string password = "",
        long? id = null) =>
        new(
            name,
            type,
            Enabled: true,
            Priority: 1,
            JsonSerializer.Deserialize<JsonElement>(
                $$"""{"url":"{{url}}","password":"{{password}}"}"""),
            id);

    /// <summary>A client type with one plain field and one secret, which is all the rules need.</summary>
    private sealed class FakeDownloadClientType : IDownloadClientType
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

        public IReadOnlyList<string> Validate(JsonElement settings)
        {
            var url = settings.TryGetProperty("url", out var value) ? value.GetString() : null;

            return url is not null && url.StartsWith("http", StringComparison.Ordinal)
                ? []
                : ["The URL must be an absolute http or https address."];
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

    /// <summary>The registry the service needs; these tests assert on the settings, not the redaction.</summary>
    private sealed class NoopSecretRegistry : Wondarr.Core.Logging.ISecretRegistry
    {
        public void Register(string? secret)
        {
        }

        public string Redact(string text) => text;
    }
}
