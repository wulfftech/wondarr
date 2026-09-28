using System.Collections;
using Wondarr.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Wondarr.Core.Tests.Configuration;

public class ServerOptionsBindingTests
{
    [Fact]
    public void Environment_variables_override_config_yml()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("server:\n  port: 2000\n");

        var environment = new Hashtable
        {
            ["APP__SERVER__PORT"] = "1077",
            ["APP__SERVER__URL_BASE"] = "/music",
        };

        var options = Bind(directory, environment);

        options.Port.Should().Be(1077);
        options.UrlBase.Should().Be("/music");
    }

    [Fact]
    public void Missing_file_and_environment_use_defaults()
    {
        using var directory = new TemporaryConfigDirectory();

        var options = Bind(directory, new Hashtable());

        options.Port.Should().Be(1077);
        options.UrlBase.Should().BeEmpty();
        options.Auth.Should().Be(AuthenticationMethod.Forms);
        options.AuthRequired.Should().Be(AuthenticationRequired.DisabledForLocalAddresses);
    }

    [Fact]
    public void Out_of_range_port_fails_validation_with_the_yaml_key()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("server:\n  port: 70000\n");

        var act = () => Bind(directory, new Hashtable());

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("server.port");
    }

    [Fact]
    public void Non_numeric_port_fails_with_the_configuration_key()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("server:\n  port: abc\n");

        var act = () => Bind(directory, new Hashtable());

        act.Should().Throw<Exception>()
            .Which.Message.Should().Match(message => message.Contains("Server:Port") || message.Contains("server.port"));
    }

    [Fact]
    public void Url_base_with_spaces_fails_validation_with_the_yaml_key()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("server:\n  url_base: \"bad base\"\n");

        var act = () => Bind(directory, new Hashtable());

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("server.url_base");
    }

    [Theory]
    [InlineData("wondarr/", "/wondarr")]
    [InlineData("/wondarr", "/wondarr")]
    [InlineData("/", "")]
    [InlineData("  /music/  ", "/music")]
    public void Url_base_is_normalised(string configured, string expected)
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig($"server:\n  url_base: \"{configured}\"\n");

        var options = Bind(directory, new Hashtable());

        options.UrlBase.Should().Be(expected);
    }

    [Fact]
    public void Sequences_and_scalar_nulls_are_flattened()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("server:\n  port: 1077\n  url_base: ~\nsources:\n  - type: slskd\n  - type: youtube\n");

        var configuration = BuildConfiguration(directory, new Hashtable());

        configuration["Server:Port"].Should().Be("1077");
        configuration["Server:UrlBase"].Should().BeNull();
        configuration["Sources:0:Type"].Should().Be("slskd");
        configuration["Sources:1:Type"].Should().Be("youtube");
    }

    [Fact]
    public void Malformed_yaml_names_the_file_and_line()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("server:\n  port: [unclosed\n");

        var act = () => BuildConfiguration(directory, new Hashtable())["Server:Port"];

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(directory.Paths.ConfigFile);
    }

    private static ServerOptions Bind(TemporaryConfigDirectory directory, IDictionary environment)
    {
        var configuration = BuildConfiguration(directory, environment);

        // The same pipeline Program.cs registers: bind, post-configure, validate.
        var configure = new ConfigureNamedOptions<ServerOptions>(
            Options.DefaultName,
            options => configuration.GetSection("Server").Bind(options));

        var factory = new OptionsFactory<ServerOptions>(
            [configure],
            [new ServerOptionsPostConfigure()],
            [new ServerOptionsValidator()]);

        return factory.Create(Options.DefaultName);
    }

    private static IConfigurationRoot BuildConfiguration(TemporaryConfigDirectory directory, IDictionary environment)
    {
        var builder = new ConfigurationBuilder();
        builder.AddWondarrConfiguration(directory.Paths, environment);
        return builder.Build();
    }
}
