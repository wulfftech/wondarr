using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The API sends enums as camelCase strings, so the OpenAPI document has to type them as strings:
/// the frontend generates its types from it (M-32). A string schema that listed other names than the
/// API writes would be as wrong as the integer one was.
/// </summary>
public sealed class OpenApiEnumSchemaTests : IDisposable
{
    private readonly WondarrAppFactory _factory = new();

    public static TheoryData<Type> SerialisedEnums => new()
    {
        typeof(QueueItemState),
        typeof(SearchOutcome),
        typeof(HistoryEventType),
        typeof(CommandStatus),
    };

    [Theory]
    [MemberData(nameof(SerialisedEnums))]
    public async Task An_enum_is_a_string_schema_listing_the_names_the_api_writes(Type enumType)
    {
        var schema = await EnumSchemaAsync(enumType.Name);

        schema.GetProperty("type").GetString().Should().Be("string");

        var documented = DocumentedNames(schema);
        var options = _factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var written = Enum.GetValues(enumType)
            .Cast<object>()
            .Select(value => JsonSerializer.SerializeToElement(value, enumType, options).GetString())
            .ToList();

        documented.Should().BeEquivalentTo(written);
        documented.Should().OnlyContain(name => char.IsLower(name![0]));
    }

    [Fact]
    public async Task No_component_schema_is_a_bare_integer_enum()
    {
        using var document = await FetchDocumentAsync();
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var integerEnums = new List<string>();

        foreach (var schema in schemas.EnumerateObject())
        {
            var isInteger = schema.Value.TryGetProperty("type", out var type) && type.ToString().Contains("integer", StringComparison.Ordinal);

            // A real integer component schema would carry a format; the generator's enum fallback is a bare integer.
            if (isInteger && (schema.Value.TryGetProperty("enum", out _) || !schema.Value.TryGetProperty("format", out _)))
            {
                integerEnums.Add(schema.Name);
            }
        }

        integerEnums.Should().BeEmpty("every enum the API writes as a string is typed as one in the document");
    }

    [Fact]
    public async Task The_state_of_a_queue_item_the_api_returns_is_one_of_the_documented_names()
    {
        var documented = DocumentedNames(await EnumSchemaAsync(nameof(QueueItemState)));

        // One live queue item per song, so each state gets its own song.
        foreach (var state in Enum.GetValues<QueueItemState>())
        {
            var songId = await QueueApiTests.SeedSongAsync(_factory, $"Song {state}");
            var candidateId = await QueueApiTests.SeedCandidateAsync(_factory, songId, $"Song {state}.flac");
            await QueueApiTests.SeedQueueItemAsync(_factory, songId, candidateId, state);
        }

        using var client = QueueApiTests.Authenticated(_factory);
        using var response = await client.GetAsync(new Uri("/api/v1/queue?includeFinished=true&pageSize=50", UriKind.Relative));
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var states = body.RootElement.GetProperty("records").EnumerateArray()
            .Select(record => record.GetProperty("state").GetString())
            .ToList();

        states.Should().Contain("remotelyQueued");
        states.Should().OnlyContain(state => documented.Contains(state));
    }

    [Fact]
    public async Task The_event_type_of_a_history_row_the_api_returns_is_one_of_the_documented_names()
    {
        var documented = DocumentedNames(await EnumSchemaAsync(nameof(HistoryEventType)));

        var songId = await QueueApiTests.SeedSongAsync(_factory, "Get Lucky");

        using (var scope = _factory.Services.CreateScope())
        {
            await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

            foreach (var eventType in Enum.GetValues<HistoryEventType>())
            {
                context.History.Add(new HistoryItem { SongId = songId, EventType = eventType, Data = "{}" });
            }

            await context.SaveChangesAsync();
        }

        using var client = QueueApiTests.Authenticated(_factory);
        using var response = await client.GetAsync(new Uri("/api/v1/history?pageSize=50", UriKind.Relative));
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var types = body.RootElement.GetProperty("records").EnumerateArray()
            .Select(record => record.GetProperty("eventType").GetString())
            .ToList();

        types.Should().Contain("grabbed");
        types.Should().BeEquivalentTo(documented);
    }

    [Fact]
    public async Task The_status_of_a_command_the_api_returns_is_one_of_the_documented_names()
    {
        var documented = DocumentedNames(await EnumSchemaAsync(nameof(CommandStatus)));

        using var client = QueueApiTests.Authenticated(_factory);
        using var content = new StringContent("{\"name\":\"Heartbeat\"}", Encoding.UTF8, "application/json");
        using var created = await client.PostAsync(new Uri("/api/v1/command", UriKind.Relative), content);
        created.EnsureSuccessStatusCode();

        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdBody.RootElement.GetProperty("id").GetInt64();

        using var response = await client.GetAsync(new Uri($"/api/v1/command/{id}", UriKind.Relative));
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var status = body.RootElement.GetProperty("status").GetString();

        status.Should().NotBeNullOrEmpty();
        documented.Should().Contain(status);
    }

    [Fact]
    public async Task The_outcome_of_an_interactive_search_the_api_returns_is_one_of_the_documented_names()
    {
        var documented = DocumentedNames(await EnumSchemaAsync(nameof(SearchOutcome)));

        using var factory = ReleaseApiTests.Factory();
        var songId = await QueueApiTests.SeedSongAsync(factory, "Get Lucky");

        using var client = QueueApiTests.Authenticated(factory);
        using var response = await client.GetAsync(new Uri($"/api/v1/release?songId={songId}", UriKind.Relative));
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var outcome = body.RootElement.GetProperty("outcome").GetString();

        outcome.Should().NotBeNullOrEmpty();
        documented.Should().Contain(outcome);
    }

    public void Dispose() => _factory.Dispose();

    private static List<string?> DocumentedNames(JsonElement schema) =>
        schema.GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToList();

    private async Task<JsonElement> EnumSchemaAsync(string name)
    {
        using var document = await FetchDocumentAsync();

        return document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(name).Clone();
    }

    private async Task<JsonDocument> FetchDocumentAsync()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);

        using var response = await client.GetAsync(new Uri("/docs/v1/openapi.json", UriKind.Relative));
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
