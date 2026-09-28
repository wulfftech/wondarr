using System.Net;
using Compilarr.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Compilarr.Api.Tests;

public class ConfigurationBootstrapTests
{
    [Fact]
    public async Task The_app_starts_against_a_temp_config_dir_and_generates_a_key()
    {
        using var factory = new CompilarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/ping", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        File.Exists(Path.Combine(factory.ConfigDir, "config.yml")).Should().BeTrue();

        var options = factory.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        options.ApiKey.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public async Task The_database_is_created_and_migrated_on_startup()
    {
        using var factory = new CompilarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/ping", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        File.Exists(Path.Combine(factory.ConfigDir, "compilarr.db")).Should().BeTrue();
    }
}
