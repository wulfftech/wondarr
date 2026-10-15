using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wondarr.Core.Organizer;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// Not parallelised: the environment-lock test sets a real <c>APP__IMPORT__CLEAN_LEFTOVER_FOLDERS</c>
/// variable for the duration of its run, and a concurrently starting host would read it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ImportSettingsApiTestsSeries
{
    /// <summary>The collection's name, shared by the test class.</summary>
    public const string Name = "Import settings API";
}

/// <summary><c>GET/PUT /api/v1/config/import</c>: the switch on Settings &gt; Download clients.</summary>
[Collection(ImportSettingsApiTestsSeries.Name)]
public sealed class ImportSettingsApiTests
{
    private const string Endpoint = "/api/v1/config/import";

    [Fact]
    public async Task The_endpoint_needs_the_api_key()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();

        using var read = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));
        read.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var write = await client.PutAsync(
            new Uri(Endpoint, UriKind.Relative),
            new StringContent("""{"cleanLeftoverFolders":false}""", Encoding.UTF8, "application/json"));
        write.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_option_is_on_by_default_and_not_locked()
    {
        using var factory = new WondarrAppFactory();
        using var api = new Session(factory);

        var (status, body) = await api.SendAsync(HttpMethod.Get, Endpoint);

        status.Should().Be(HttpStatusCode.OK);
        ((JsonObject)body!)["cleanLeftoverFolders"]!.GetValue<bool>().Should().BeTrue();
        ((JsonObject)body)["cleanLeftoverFoldersLocked"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task The_option_round_trips_is_written_to_config_yml_and_applies_without_a_restart()
    {
        using var factory = new WondarrAppFactory();
        using var api = new Session(factory);

        var (status, put) = await api.SendAsync(HttpMethod.Put, Endpoint, """{"cleanLeftoverFolders":false}""");

        status.Should().Be(HttpStatusCode.OK);
        ((JsonObject)put!)["cleanLeftoverFolders"]!.GetValue<bool>().Should().BeFalse();

        var yaml = await File.ReadAllTextAsync(Path.Combine(factory.ConfigDir, "config.yml"));

        yaml.Should().Contain("import:").And.Contain("clean_leftover_folders: false");

        var (_, again) = await api.SendAsync(HttpMethod.Get, Endpoint);

        ((JsonObject)again!)["cleanLeftoverFolders"]!.GetValue<bool>().Should().BeFalse();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOptionsMonitor<ImportOptions>>().CurrentValue.CleanLeftoverFolders
            .Should().BeFalse("the options reload, so the cleanup stops at once");

        var (_, back) = await api.SendAsync(HttpMethod.Put, Endpoint, """{"cleanLeftoverFolders":true}""");

        ((JsonObject)back!)["cleanLeftoverFolders"]!.GetValue<bool>().Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(factory.ConfigDir, "config.yml"))).Should().Contain("clean_leftover_folders: true");
    }

    [Fact]
    public async Task A_change_that_names_no_field_leaves_the_file_alone()
    {
        using var factory = new WondarrAppFactory();
        using var api = new Session(factory);

        var (status, body) = await api.SendAsync(HttpMethod.Put, Endpoint, "{}");

        status.Should().Be(HttpStatusCode.OK);
        ((JsonObject)body!)["cleanLeftoverFolders"]!.GetValue<bool>().Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(factory.ConfigDir, "config.yml"))).Should().NotContain("clean_leftover_folders");
    }

    [Fact]
    public async Task A_setting_the_environment_owns_is_read_only_and_a_change_is_refused_with_the_variable_named()
    {
        Environment.SetEnvironmentVariable("APP__IMPORT__CLEAN_LEFTOVER_FOLDERS", "false");

        try
        {
            using var factory = new WondarrAppFactory();
            using var api = new Session(factory);

            var (_, read) = await api.SendAsync(HttpMethod.Get, Endpoint);

            ((JsonObject)read!)["cleanLeftoverFolders"]!.GetValue<bool>().Should().BeFalse();
            ((JsonObject)read)["cleanLeftoverFoldersLocked"]!.GetValue<bool>().Should().BeTrue();

            var (status, body) = await api.SendAsync(HttpMethod.Put, Endpoint, """{"cleanLeftoverFolders":true}""");

            status.Should().Be(HttpStatusCode.BadRequest);
            ((JsonObject)body!)["errors"]!["settings"]!.AsArray()
                .Should().Contain(message => message!.GetValue<string>().Contains("APP__IMPORT__CLEAN_LEFTOVER_FOLDERS"));

            var yaml = await File.ReadAllTextAsync(Path.Combine(factory.ConfigDir, "config.yml"));

            yaml.Should().NotContain("clean_leftover_folders", "a refused change writes nothing");
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP__IMPORT__CLEAN_LEFTOVER_FOLDERS", null);
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly HttpClient _client;

        public Session(WondarrAppFactory factory)
        {
            _client = factory.CreateClient();
            _client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);
        }

        public void Dispose() => _client.Dispose();

        public async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, string? json = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var response = await _client.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();

            return (response.StatusCode, raw.Length == 0 ? null : JsonNode.Parse(raw));
        }
    }
}
