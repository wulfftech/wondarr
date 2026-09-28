using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Xunit;

namespace Compilarr.Api.Tests;

public sealed class ApiKeyAuthenticationTests : IDisposable
{
    private const string AuthUserEndpoint = "/api/v1/auth/user";

    private readonly CompilarrAppFactory _factory = new();

    [Fact]
    public async Task Api_requests_without_a_key_are_unauthorized()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri(AuthUserEndpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_requests_with_a_wrong_key_are_unauthorized()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", new string('0', 32));

        using var response = await client.GetAsync(new Uri(AuthUserEndpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_requests_accept_the_key_as_a_header_a_query_parameter_and_a_bearer_token()
    {
        using var headerClient = _factory.CreateClient();
        headerClient.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);

        using var queryClient = _factory.CreateClient();
        using var bearerClient = _factory.CreateClient();
        bearerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.ApiKey);

        using var fromHeader = await headerClient.GetAsync(new Uri(AuthUserEndpoint, UriKind.Relative));
        using var fromQuery = await queryClient.GetAsync(new Uri($"{AuthUserEndpoint}?apikey={_factory.ApiKey}", UriKind.Relative));
        using var fromBearer = await bearerClient.GetAsync(new Uri(AuthUserEndpoint, UriKind.Relative));

        fromHeader.StatusCode.Should().Be(HttpStatusCode.OK);
        fromQuery.StatusCode.Should().Be(HttpStatusCode.OK);
        fromBearer.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_user_endpoint_reports_no_credentials_until_they_are_set()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);

        using var response = await client.GetAsync(new Uri(AuthUserEndpoint, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("\"configured\":false");
    }

    public void Dispose() => _factory.Dispose();
}
