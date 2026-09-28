using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Wondarr.Api.Tests;

public sealed class SystemStatusTests
{
    private const string StatusEndpoint = "/api/v1/system/status";

    [Fact]
    public async Task Status_without_a_key_is_unauthorized()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(StatusEndpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Status_reports_the_fields_dashboards_read_in_camel_case()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.GetAsync(new Uri(StatusEndpoint, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var status = document.RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        status.GetProperty("appName").GetString().Should().Be("Wondarr");
        status.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        status.GetProperty("urlBase").GetString().Should().BeEmpty();
        status.GetProperty("authentication").GetString().Should().Be("forms");
        status.GetProperty("databaseType").GetString().Should().Be("sqLite");
        status.GetProperty("databaseVersion").GetString().Should().NotBeNullOrWhiteSpace();
        status.GetProperty("migrationVersion").GetInt32().Should().BePositive();
        status.GetProperty("startTime").GetDateTime().Should().BeBefore(DateTime.UtcNow.AddMinutes(1));
        status.GetProperty("isDocker").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);

        // camelCase everywhere: no PascalCase leftovers.
        status.TryGetProperty("AppName", out _).Should().BeFalse();
        status.TryGetProperty("DatabaseType", out _).Should().BeFalse();
    }
}
