using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// Not parallelised: the test sets a real <c>APP__SERVER__REQUIRE_LOCAL_HOST</c> variable for the
/// duration of its run, and a concurrently starting host would read it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SecuritySettingsEnvironmentLockSeries
{
    /// <summary>The collection's name, shared by the test class.</summary>
    public const string Name = "Security settings environment lock";
}

/// <summary>The environment owning <c>server.require_local_host</c>.</summary>
[Collection(SecuritySettingsEnvironmentLockSeries.Name)]
public sealed class SecuritySettingsEnvironmentLockTests
{
    private const string Endpoint = "/api/v1/config/security";

    [Fact]
    public async Task A_setting_the_environment_owns_is_read_only_and_a_change_is_refused_with_the_file_untouched()
    {
        Environment.SetEnvironmentVariable("APP__SERVER__REQUIRE_LOCAL_HOST", "false");

        try
        {
            using var factory = new WondarrAppFactory();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

            var configPath = Path.Combine(factory.ConfigDir, "config.yml");
            var before = await File.ReadAllBytesAsync(configPath);

            using var read = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));
            var state = JsonNode.Parse(await read.Content.ReadAsStringAsync())!;

            read.StatusCode.Should().Be(HttpStatusCode.OK);
            state["requireLocalHost"]!.GetValue<bool>().Should().BeFalse();
            state["requireLocalHostLocked"]!.GetValue<bool>().Should().BeTrue();

            using var write = await client.PutAsync(
                new Uri(Endpoint, UriKind.Relative),
                new StringContent("""{"requireLocalHost":true}""", Encoding.UTF8, "application/json"));
            var problem = JsonNode.Parse(await write.Content.ReadAsStringAsync())!;

            write.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            problem["errors"]!["settings"]!.AsArray()
                .Should().Contain(message => message!.GetValue<string>().Contains("APP__SERVER__REQUIRE_LOCAL_HOST"));

            (await File.ReadAllBytesAsync(configPath)).Should().Equal(before, "a refused change writes nothing");
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP__SERVER__REQUIRE_LOCAL_HOST", null);
        }
    }
}
