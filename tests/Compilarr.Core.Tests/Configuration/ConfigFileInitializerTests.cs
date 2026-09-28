using System.Collections;
using System.Text.RegularExpressions;
using Compilarr.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Compilarr.Core.Tests.Configuration;

public class ConfigFileInitializerTests
{
    [Fact]
    public void First_run_writes_config_yml_with_a_generated_api_key()
    {
        using var directory = new TemporaryConfigDirectory();
        var configuration = Configuration(directory, new Hashtable());

        var written = ConfigFileInitializer.EnsureInitialized(directory.Paths, configuration);

        written.Should().BeTrue();
        File.Exists(directory.Paths.ConfigFile).Should().BeTrue();
        ReadApiKey(directory).Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public void Second_run_leaves_the_file_byte_identical()
    {
        using var directory = new TemporaryConfigDirectory();
        var configuration = Configuration(directory, new Hashtable());

        ConfigFileInitializer.EnsureInitialized(directory.Paths, configuration);
        var first = directory.ReadConfig();

        var writtenAgain = ConfigFileInitializer.EnsureInitialized(directory.Paths, configuration);

        writtenAgain.Should().BeFalse();
        directory.ReadConfig().Should().Be(first);
    }

    [Fact]
    public void An_environment_supplied_key_is_not_written_to_the_file()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("server:\n  port: 1077\n");
        var before = directory.ReadConfig();
        var configuration = Configuration(directory, new Hashtable { ["APP__SERVER__API_KEY"] = new string('a', 32) });

        var written = ConfigFileInitializer.EnsureInitialized(directory.Paths, configuration);

        written.Should().BeFalse();
        directory.ReadConfig().Should().Be(before);
    }

    [Fact]
    public void An_existing_key_is_never_overwritten()
    {
        using var directory = new TemporaryConfigDirectory();
        var existing = new string('b', 32);
        directory.WriteConfig($"server:\n  port: 1077\n  api_key: {existing}\n");

        var written = ConfigFileInitializer.EnsureInitialized(directory.Paths, Configuration(directory, new Hashtable()));

        written.Should().BeFalse();
        ReadApiKey(directory).Should().Be(existing);
    }

    [Fact]
    public void A_missing_key_is_added_when_no_environment_key_is_present()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("server:\n  port: 2000\n");

        var written = ConfigFileInitializer.EnsureInitialized(directory.Paths, Configuration(directory, new Hashtable()));

        written.Should().BeTrue();
        ReadApiKey(directory).Should().MatchRegex("^[0-9a-f]{32}$");
        directory.ReadConfig().Should().Contain("port: 2000");
    }

    [Fact]
    public void The_written_file_binds_back_to_the_defaults()
    {
        using var directory = new TemporaryConfigDirectory();
        ConfigFileInitializer.EnsureInitialized(directory.Paths, Configuration(directory, new Hashtable()));

        var configuration = new ConfigurationBuilder()
            .AddCompilarrConfiguration(directory.Paths, new Hashtable())
            .Build();

        var options = configuration.GetSection("Server").Get<ServerOptions>();

        options.Should().NotBeNull();
        options!.Port.Should().Be(1077);
        options.BindAddress.Should().Be("*");
        options.UrlBase.Should().BeEmpty();
        options.Auth.Should().Be(AuthenticationMethod.Forms);
        options.AuthRequired.Should().Be(AuthenticationRequired.DisabledForLocalAddresses);
    }

    private static IConfigurationRoot Configuration(TemporaryConfigDirectory directory, Hashtable environment) =>
        new ConfigurationBuilder().AddCompilarrConfiguration(directory.Paths, environment).Build();

    private static string ReadApiKey(TemporaryConfigDirectory directory)
    {
        var match = Regex.Match(directory.ReadConfig(), @"api_key:\s*(\S+)");
        match.Success.Should().BeTrue("the file should carry an api_key");
        return match.Groups[1].Value;
    }
}