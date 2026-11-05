using System.Collections;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Wondarr.Core.Configuration;
using Xunit;

namespace Wondarr.Core.Tests.Configuration;

/// <summary><c>server.require_local_host</c> as <c>config.yml</c> sets it.</summary>
public sealed class ServerOptionsRequireLocalHostBindingTests
{
    [Fact]
    public void Only_local_host_names_are_answered_by_default()
    {
        new ServerOptions().RequireLocalHost.Should().BeTrue();

        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("server:\n  auth: none\n");

        Bind(directory, new Hashtable()).RequireLocalHost.Should().BeTrue("a file that does not mention the option leaves it on");
    }

    [Fact]
    public void The_option_is_read_from_the_server_section_of_config_yml()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("server:\n  require_local_host: false\n");

        Bind(directory, new Hashtable()).RequireLocalHost.Should().BeFalse();
    }

    private static ServerOptions Bind(TemporaryConfigDirectory directory, IDictionary environment)
    {
        var builder = new ConfigurationBuilder();
        builder.AddWondarrConfiguration(directory.Paths, environment);
        var options = new ServerOptions();
        builder.Build().GetSection("Server").Bind(options);

        return options;
    }
}
