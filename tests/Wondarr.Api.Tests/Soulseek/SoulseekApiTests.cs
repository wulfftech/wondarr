using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wondarr.Sources.Slskd;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Wondarr.Api.Tests.Soulseek;

/// <summary>
/// The Soulseek settings and status endpoints, against a real host with a real <c>config.yml</c>:
/// a change has to reach both the file and the bound options the supervisor reads (Phase 2 gate).
/// </summary>
public sealed class SoulseekApiTests : IDisposable
{
    private const string SettingsEndpoint = "/api/v1/soulseek/settings";
    private const string StatusEndpoint = "/api/v1/soulseek/status";

    private readonly WondarrAppFactory _factory = new();

    [Fact]
    public async Task The_settings_endpoints_require_an_api_key()
    {
        using var client = _factory.CreateClient();

        using var settings = await client.GetAsync(new Uri(SettingsEndpoint, UriKind.Relative));
        using var status = await client.GetAsync(new Uri(StatusEndpoint, UriKind.Relative));

        settings.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        status.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Settings_never_carry_the_password()
    {
        using var client = Client();
        using var document = await GetAsync(client, SettingsEndpoint);

        var root = document.RootElement;

        root.TryGetProperty("password", out _).Should().BeFalse();
        root.GetProperty("passwordSet").GetBoolean().Should().BeFalse();
        root.GetProperty("shareLibrary").GetBoolean().Should().BeTrue();
        root.GetProperty("uploadSlots").GetInt32().Should().Be(10);

        // No APP__SOULSEEK__ variable is set for a test run, so nothing is read-only.
        root.GetProperty("readOnlyFields").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Changing_the_shares_round_trips_through_the_config_file_and_the_options()
    {
        using var client = Client();

        using var response = await client.PutAsJsonAsync(
            new Uri(SettingsEndpoint, UriKind.Relative),
            new { shareLibrary = false, sharedFolders = Array.Empty<string>() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("restartsSlskd").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("settings").GetProperty("shareLibrary").GetBoolean().Should().BeFalse();

        using var reloaded = await GetAsync(client, SettingsEndpoint);
        reloaded.RootElement.GetProperty("shareLibrary").GetBoolean().Should().BeFalse();

        // The supervisor acts on the options monitor, so that is what has to have changed.
        var options = _factory.Services.GetRequiredService<IOptionsMonitor<SoulseekOptions>>().CurrentValue;

        options.ShareLibrary.Should().BeFalse();

        // The empty list is what the user asked for: the default folder must not be appended to it,
        // and the file must say the list was set on purpose so a reload keeps it empty.
        options.SharedFolders.Should().BeEmpty();
        reloaded.RootElement.GetProperty("sharedFolders").GetArrayLength().Should().Be(0);
        ConfigFile().Should().Contain("shared_folders_set");
    }

    [Fact]
    public async Task A_written_shared_folder_list_replaces_the_default()
    {
        using var client = Client();

        using var response = await client.PutAsJsonAsync(
            new Uri(SettingsEndpoint, UriKind.Relative),
            new { sharedFolders = new List<string> { "/x" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.Services.GetRequiredService<IOptionsMonitor<SoulseekOptions>>()
            .CurrentValue.SharedFolders.Should().Equal(["/x"], "a bound list replaces, never merges with, the default");

        using var reloaded = await GetAsync(client, SettingsEndpoint);
        reloaded.RootElement.GetProperty("sharedFolders").EnumerateArray()
            .Select(folder => folder.GetString()).Should().Equal("/x");
    }

    [Fact]
    public async Task A_rejected_change_never_echoes_the_password()
    {
        using var client = Client();

        // A password without a username is refused by the validator; the refusal must not quote
        // the password back, in the body or in the file.
        using var response = await client.PutAsJsonAsync(
            new Uri(SettingsEndpoint, UriKind.Relative),
            new { username = string.Empty, password = "s3cret" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("s3cret");
        body.Should().Contain("soulseek.username:");
        ConfigFile().Should().NotContain("s3cret");
    }

    [Fact]
    public async Task An_invalid_change_is_a_problem_details_with_the_reasons()
    {
        using var client = Client();

        using var response = await client.PutAsJsonAsync(
            new Uri(SettingsEndpoint, UriKind.Relative),
            new { listenPort = 80 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        body.RootElement.GetProperty("errors").GetProperty("settings")[0].GetString()
            .Should().StartWith("soulseek.listen_port:");

        // Nothing was written: the port is what it was.
        using var settings = await GetAsync(client, SettingsEndpoint);
        settings.RootElement.GetProperty("listenPort").GetInt32().Should().Be(50300);
    }

    [Fact]
    public async Task An_empty_change_succeeds_without_restarting_slskd()
    {
        using var client = Client();

        using var response = await client.PutAsJsonAsync(
            new Uri(SettingsEndpoint, UriKind.Relative),
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("restartsSlskd").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Status_reports_the_mode_what_is_shared_and_the_search_budget()
    {
        using var client = Client();
        using var document = await GetAsync(client, StatusEndpoint);

        var root = document.RootElement;

        root.GetProperty("mode").GetString().Should().Be("bundled");
        root.GetProperty("state").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("loggedIn").GetBoolean().Should().BeFalse();
        root.GetProperty("pendingRestart").GetBoolean().Should().BeFalse();

        var sharing = root.GetProperty("sharing");
        sharing.GetProperty("enabled").GetBoolean().Should().BeTrue();
        sharing.GetProperty("folders").EnumerateArray().Select(folder => folder.GetString())
            .Should().Equal("/data/music");

        var budget = root.GetProperty("searchBudget");
        budget.GetProperty("maxSearches").GetInt32().Should().Be(30);
        budget.GetProperty("maxOutstanding").GetInt32().Should().Be(2);
        budget.GetProperty("submittedInWindow").GetInt32().Should().Be(0);
        budget.GetProperty("outstanding").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// The supervisor runs the real launcher against a Linux path that does not exist on a test
    /// machine, so <c>slskd.yml</c> is asserted through <see cref="SlskdConfigRenderer"/> with the
    /// options the PUT left behind, which is exactly what the supervisor renders from.
    /// </summary>
    [Fact]
    public async Task Turning_shares_off_renders_an_empty_shares_list()
    {
        using var client = Client();

        new SlskdConfigRenderer()
            .Render(Options(), new SlskdRuntimeSecrets("key", "wondarr", "password"))
            .Should().Contain("/data/music", "the default settings share the library root");

        using var response = await client.PutAsJsonAsync(
            new Uri(SettingsEndpoint, UriKind.Relative),
            new { shareLibrary = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var rendered = new SlskdConfigRenderer()
            .Render(Options(), new SlskdRuntimeSecrets("key", "wondarr", "password"));

        var stream = new YamlStream();
        using (var reader = new StringReader(rendered))
        {
            stream.Load(reader);
        }

        var shares = (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("shares")];

        shares.Children[new YamlScalarNode("directories")]
            .Should().BeOfType<YamlSequenceNode>()
            .Which.Children.Should().BeEmpty();
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>What the settings really wrote: the file is the source of truth, not the response.</summary>
    private string ConfigFile() => File.ReadAllText(Path.Combine(_factory.ConfigDir, "config.yml"));

    private SoulseekOptions Options() =>
        _factory.Services.GetRequiredService<IOptionsMonitor<SoulseekOptions>>().CurrentValue;

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);

        return client;
    }

    private static async Task<JsonDocument> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}