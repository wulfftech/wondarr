using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Compilarr.Core.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Compilarr.Api.Tests;

public class ConfigurationBootstrapTests
{
    [Fact]
    public async Task The_app_starts_against_a_temp_config_dir_and_generates_a_key()
    {
        var directory = Path.Combine(Path.GetTempPath(), "compilarr-api-tests", Guid.NewGuid().ToString("N"));

        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.UseSetting("ConfigDir", directory));

            using var client = factory.CreateClient();
            using var response = await client.GetAsync(new Uri("/ping", UriKind.Relative));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            File.Exists(Path.Combine(directory, "config.yml")).Should().BeTrue();

            var options = factory.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
            options.ApiKey.Should().HaveLength(32);
            options.ApiKey.Should().MatchRegex("^[0-9a-f]{32}$");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
