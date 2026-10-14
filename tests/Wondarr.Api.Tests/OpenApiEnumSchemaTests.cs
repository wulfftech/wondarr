using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
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

        var documented = schema.GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToList();
        var options = _factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var written = Enum.GetValues(enumType)
            .Cast<object>()
            .Select(value => JsonSerializer.SerializeToElement(value, enumType, options).GetString())
            .ToList();

        documented.Should().BeEquivalentTo(written);
        documented.Should().OnlyContain(name => char.IsLower(name![0]));
    }

    [Fact]
    public async Task The_state_of_a_queue_item_the_api_returns_is_one_of_the_documented_names()
    {
        var schema = await EnumSchemaAsync(nameof(QueueItemState));
        var documented = schema.GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToList();

        var songId = await QueueApiTests.SeedSongAsync(_factory, "Bohemian Rhapsody");
        var candidateId = await QueueApiTests.SeedCandidateAsync(_factory, songId, "Bohemian Rhapsody.flac");
        await QueueApiTests.SeedQueueItemAsync(_factory, songId, candidateId, QueueItemState.RemotelyQueued);

        using var client = QueueApiTests.Authenticated(_factory);
        using var response = await client.GetAsync(new Uri("/api/v1/queue", UriKind.Relative));
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var state = body.RootElement.GetProperty("records")[0].GetProperty("state").GetString();

        state.Should().Be("remotelyQueued");
        documented.Should().Contain(state);
    }

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> EnumSchemaAsync(string name)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);

        using var response = await client.GetAsync(new Uri("/docs/v1/openapi.json", UriKind.Relative));
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(name).Clone();
    }
}
