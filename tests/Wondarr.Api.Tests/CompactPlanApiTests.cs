using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The Compact library dry run: the plan for a library is returned as it is, and asking for it changes
/// nothing. The moves themselves are P3-09b.
/// </summary>
public sealed class CompactPlanApiTests
{
    private const string CompactEndpoint = "/api/v1/library/1/compact";

    [Fact]
    public async Task Planning_an_empty_library_is_a_plan_with_no_moves()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(new Uri(CompactEndpoint, UriKind.Relative));
        var plan = (JsonObject)(await ReadJsonAsync(response))!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        plan["libraryId"]!.GetValue<long>().Should().Be(1);
        plan["albumsBefore"]!.GetValue<int>().Should().Be(0);
        plan["albumsAfter"]!.GetValue<int>().Should().Be(0);
        plan["songsConsidered"]!.GetValue<int>().Should().Be(0);
        plan["moves"].Should().BeOfType<JsonArray>().Which.Should().BeEmpty();

        // The proposed album contexts are the planner's business: a plan is not a write.
        plan.Should().NotContainKey("proposed");
    }

    [Fact]
    public async Task Planning_an_unknown_library_is_a_not_found()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(new Uri("/api/v1/library/987654/compact", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Planning_without_an_api_key_is_unauthorized()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(CompactEndpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static HttpClient Authenticated(WondarrAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        return client;
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
}
