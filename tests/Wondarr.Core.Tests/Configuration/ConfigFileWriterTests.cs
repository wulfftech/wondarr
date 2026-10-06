using System.Collections;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Configuration;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Wondarr.Core.Tests.Configuration;

/// <summary>
/// <c>config.yml</c> is edited by hand as well as by the settings pages, so a write must change the
/// section it was asked to change and nothing else.
/// </summary>
public sealed class ConfigFileWriterTests
{
    private const string Realistic = """
        server:
          port: 1077
          api_key: 0f8a4c1d2e3b4a5c6d7e8f9012345678
          auth: forms
          url_base: ""

        auth:
          required: true
          method: forms

        metadata:
          musicbrainz:
            user_agent: Wondarr/0.1 (https://example.invalid)
          prefer_release_group: true

        soulseek:
          username: old-user
          password: hunter2
          listen_port: 50300
          share_library: true
          shared_folders:
            - /data/music

        """;

    [Fact]
    public async Task UpdateSection_keeps_every_other_section_as_it_was()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig(Realistic);

        var writer = Writer(directory, out _);

        await writer.UpdateSectionAsync(
            "soulseek",
            new Dictionary<string, object?> { ["share_library"] = false, ["listen_port"] = 50301 },
            CancellationToken.None);

        var before = Parse(Realistic);
        var after = Parse(directory.ReadConfig());

        foreach (var section in new[] { "server", "auth", "metadata" })
        {
            before.Children[new YamlScalarNode(section)].ToString()
                .Should().Be(after.Children[new YamlScalarNode(section)].ToString(), $"{section} must not change");
        }

        // The keys the writer was not asked about survive too.
        Section(after)["username"].ToString().Should().Be("old-user");
        Section(after)["password"].ToString().Should().Be("hunter2");
    }

    [Fact]
    public async Task UpdateSection_adds_replaces_and_removes_keys()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig(Realistic);

        var writer = Writer(directory, out _);

        await writer.UpdateSectionAsync(
            "soulseek",
            new Dictionary<string, object?>
            {
                ["listen_port"] = 50302,
                ["upload_slots"] = 7,
                ["password"] = null,
            },
            CancellationToken.None);

        var section = Section(Parse(directory.ReadConfig()));

        section.Children.Keys
            .OfType<YamlScalarNode>()
            .Select(key => key.Value)
            .Should().NotContain("password", "a null value removes the key");

        section["listen_port"].ToString().Should().Be("50302");
        section["upload_slots"].ToString().Should().Be("7");
    }

    [Fact]
    public async Task UpdateSection_creates_the_section_when_the_file_has_none()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("server:\n  port: 1077\n");

        var writer = Writer(directory, out _);

        await writer.UpdateSectionAsync(
            "soulseek",
            new Dictionary<string, object?> { ["share_library"] = false },
            CancellationToken.None);

        var root = Parse(directory.ReadConfig());

        Section(root)["share_library"].ToString().Should().Be("false");
        root.Children[new YamlScalarNode("server")].ToString().Should().Contain("1077");
    }

    [Fact]
    public async Task UpdateSection_round_trips_a_list_of_folders()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig(Realistic);

        var writer = Writer(directory, out _);

        await writer.UpdateSectionAsync(
            "soulseek",
            new Dictionary<string, object?> { ["shared_folders"] = new List<string> { "/data/music", "/data/other" } },
            CancellationToken.None);

        Section(Parse(directory.ReadConfig()))["shared_folders"]
            .Should().BeOfType<YamlSequenceNode>()
            .Which.Children.Select(child => child.ToString())
            .Should().Equal("/data/music", "/data/other");
    }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("")]
    [InlineData("a: b")]
    [InlineData("# not a comment")]
    [InlineData(" padded ")]
    [InlineData("\"already quoted\"")]
    [InlineData("first\nsecond")]
    [InlineData("- looks like a list")]
    [InlineData("* looks like an anchor")]
    public async Task UpdateSection_writes_text_that_reads_back_as_the_same_string(string value)
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("server:\n  port: 1077\n");

        var writer = Writer(directory, out var configuration);

        await writer.UpdateSectionAsync(
            "soulseek",
            new Dictionary<string, object?> { ["username"] = value },
            CancellationToken.None);

        // The file is still a YAML document, and the string the user typed is the string it holds.
        Section(Parse(directory.ReadConfig()))["username"].ToString().Should().Be(value);

        // The provider reads `~` as the YAML null marker, which is its own semantics for a value
        // that was never written through the writer; every other string comes back unchanged.
        configuration["Soulseek:Username"].Should().Be(value == "~" ? null : value);
    }

    [Fact]
    public async Task UpdateSection_marks_an_explicitly_empty_list()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig(Realistic);

        var writer = Writer(directory, out var configuration);

        await writer.UpdateSectionAsync(
            "soulseek",
            new Dictionary<string, object?> { ["shared_folders"] = new List<string>() },
            CancellationToken.None);

        var section = Section(Parse(directory.ReadConfig()));

        section["shared_folders"].Should().BeOfType<YamlSequenceNode>().Which.Children.Should().BeEmpty();

        // An empty sequence flattens to no key at all, so the file would otherwise be
        // indistinguishable from one that never mentioned the key.
        section["shared_folders_set"].ToString().Should().Be("true");
        configuration.GetSection("Soulseek:SharedFolders").Exists().Should().BeFalse();
        configuration["Soulseek:SharedFoldersSet"].Should().Be("true");
    }

    [Fact]
    public async Task UpdateSection_drops_the_empty_list_marker_once_the_list_has_items()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig(Realistic);

        var writer = Writer(directory, out var configuration);

        await writer.UpdateSectionAsync(
            "soulseek",
            new Dictionary<string, object?> { ["shared_folders"] = new List<string>() },
            CancellationToken.None);

        await writer.UpdateSectionAsync(
            "soulseek",
            new Dictionary<string, object?> { ["shared_folders"] = new List<string> { "/x" } },
            CancellationToken.None);

        var section = Section(Parse(directory.ReadConfig()));

        section.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value)
            .Should().NotContain("shared_folders_set");
        configuration["Soulseek:SharedFolders:0"].Should().Be("/x");
    }

    [Fact]
    public async Task UpdateSection_creates_the_file_owner_only_on_unix()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig(Realistic);

        var writer = Writer(directory, out _);

        await writer.UpdateSectionAsync(
            "soulseek",
            new Dictionary<string, object?> { ["share_library"] = false },
            CancellationToken.None);

        if (OperatingSystem.IsWindows())
        {
            // Unix modes do not exist here; the assertion below is about the container.
            return;
        }

        // The file holds the API key and the Soulseek password: never world-readable, not even
        // for the instant between writing and chmod-ing it.
        File.GetUnixFileMode(directory.Paths.ConfigFile)
            .Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task UpdateSection_writes_lf_line_endings()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig(Realistic);

        var writer = Writer(directory, out _);

        await writer.UpdateSectionAsync(
            "soulseek",
            new Dictionary<string, object?> { ["share_library"] = false },
            CancellationToken.None);

        directory.ReadConfig().Should().NotContain("\r\n");
    }

    [Fact]
    public async Task UpdateSection_reloads_the_configuration_so_bound_options_see_the_change()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig(Realistic);

        var builder = new ConfigurationBuilder();
        builder.AddWondarrConfiguration(directory.Paths, new Hashtable());
        var configuration = builder.Build();

        configuration.GetSection("Soulseek").Get<TestSoulseekOptions>()!.ShareLibrary.Should().BeTrue();

        var writer = new ConfigFileWriter(
            directory.Paths,
            configuration,
            NullLogger<ConfigFileWriter>.Instance);

        await writer.UpdateSectionAsync(
            "soulseek",
            new Dictionary<string, object?> { ["share_library"] = false },
            CancellationToken.None);

        var bound = configuration.GetSection("Soulseek").Get<TestSoulseekOptions>()!;

        bound.ShareLibrary.Should().BeFalse();
        bound.ListenPort.Should().Be(50300, "keys the writer did not touch keep their values");
    }

    [Fact]
    public async Task Concurrent_updates_of_different_keys_both_land()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig(Realistic);

        var writer = Writer(directory, out _);

        await Task.WhenAll(
            writer.UpdateSectionAsync(
                "soulseek",
                new Dictionary<string, object?> { ["listen_port"] = 50310 },
                CancellationToken.None),
            writer.UpdateSectionAsync(
                "soulseek",
                new Dictionary<string, object?> { ["upload_slots"] = 9 },
                CancellationToken.None));

        var section = Section(Parse(directory.ReadConfig()));

        section["listen_port"].ToString().Should().Be("50310");
        section["upload_slots"].ToString().Should().Be("9");
        section["username"].ToString().Should().Be("old-user");
    }

    [Fact]
    public async Task A_nested_key_creates_the_intermediate_mapping_and_binds_onto_it()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("""
            youtube:
              enabled: true

            """);

        var writer = Writer(directory, out var configuration);

        await writer.UpdateSectionAsync(
            "youtube",
            new Dictionary<string, object?> { ["ytdlp:retries"] = 3 },
            CancellationToken.None);

        var yaml = directory.ReadConfig();

        yaml.Should().Contain("ytdlp:").And.Contain("retries: 3");
        yaml.Should().NotContain("ytdlp:retries", "the separator addresses a nested mapping, not a flat key");

        // The nested key binds onto the sub-section the way the options pipeline reads it.
        configuration.GetSection("Youtube").GetSection("ytdlp").GetSection("retries").Value.Should().Be("3");
    }

    [Fact]
    public async Task A_nested_key_reuses_the_mapping_that_is_already_there()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("""
            youtube:
              enabled: true
              ytdlp:
                retries: 5
                timeout_seconds: 600

            """);

        var writer = Writer(directory, out _);

        await writer.UpdateSectionAsync(
            "youtube",
            new Dictionary<string, object?> { ["ytdlp:retries"] = 2, ["enabled"] = false },
            CancellationToken.None);

        var section = Section(Parse(directory.ReadConfig()), "youtube");

        section["enabled"].ToString().Should().Be("false");

        var nested = (YamlMappingNode)section["ytdlp"];

        nested["retries"].ToString().Should().Be("2");
        nested["timeout_seconds"].ToString().Should().Be("600", "a sibling key the write did not name keeps its value");
    }

    [Fact]
    public async Task A_nested_key_replaces_a_scalar_that_stood_in_its_way()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("""
            youtube:
              ytdlp: 5

            """);

        var writer = Writer(directory, out _);

        await writer.UpdateSectionAsync(
            "youtube",
            new Dictionary<string, object?> { ["ytdlp:retries"] = 2 },
            CancellationToken.None);

        var section = Section(Parse(directory.ReadConfig()), "youtube");

        var nested = (YamlMappingNode)section["ytdlp"];

        nested["retries"].ToString().Should().Be("2");
    }

    private static ConfigFileWriter Writer(TemporaryConfigDirectory directory, out IConfiguration configuration)
    {
        var builder = new ConfigurationBuilder();
        builder.AddWondarrConfiguration(directory.Paths, new Hashtable());
        configuration = builder.Build();

        return new ConfigFileWriter(directory.Paths, configuration, NullLogger<ConfigFileWriter>.Instance);
    }

    private static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlMappingNode Section(YamlMappingNode root, string name = "soulseek") =>
        (YamlMappingNode)root.Children[new YamlScalarNode(name)];

    /// <summary>Stands in for a bound options class: the YAML provider drops underscores, so the
    /// snake_case keys bind straight onto these properties.</summary>
    private sealed class TestSoulseekOptions
    {
        public string? Username { get; set; }

        public int ListenPort { get; set; }

        public bool ShareLibrary { get; set; }

        public List<string> SharedFolders { get; set; } = [];
    }
}
