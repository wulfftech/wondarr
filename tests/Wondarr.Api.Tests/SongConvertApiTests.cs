using System.Net;
using FluentAssertions;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>Conversion on demand over HTTP: the dry run, the 202, and the refusals.</summary>
public sealed class SongConvertApiTests
{
    [Fact]
    public async Task A_preview_of_an_empty_library_counts_nothing()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.PostAsync(
            new Uri("/api/v1/song/convert/preview", UriKind.Relative),
            SongApiTests.Json("""{"libraryId":1,"rule":{"codec":"mp3","bitrateKbps":320}}"""));
        var plan = await SongApiTests.ReadJsonAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        plan.GetProperty("convert").GetInt32().Should().Be(0);
        plan.GetProperty("songs").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_conversion_is_accepted_with_the_command_that_will_run_it()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.PostAsync(
            new Uri("/api/v1/song/convert", UriKind.Relative),
            SongApiTests.Json("""{"libraryId":1}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await SongApiTests.ReadJsonAsync(response)).GetProperty("commandId").GetInt64().Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("""{"songIds":[1],"libraryId":1}""", "songIds")]
    [InlineData("""{}""", "songIds")]
    [InlineData("""{"libraryId":999}""", "libraryId")]
    [InlineData("""{"libraryId":1,"rule":{"codec":"wav"}}""", "rule")]
    public async Task A_request_that_cannot_run_is_a_bad_request_naming_the_field(string body, string field)
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.PostAsync(
            new Uri("/api/v1/song/convert", UriKind.Relative),
            SongApiTests.Json(body));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SongApiTests.ReadJsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
    }
}
