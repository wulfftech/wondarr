using System.Collections;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Wondarr.Core.Configuration;
using Wondarr.Core.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.Configuration;

/// <summary><c>import.clean_leftover_folders</c> as <c>config.yml</c> and the environment set it.</summary>
public sealed class ImportOptionsBindingTests
{
    [Fact]
    public void Leftover_folders_are_cleaned_by_default()
    {
        new ImportOptions().CleanLeftoverFolders.Should().BeTrue();

        using var directory = new TemporaryConfigDirectory();

        Bind(directory, new Hashtable()).CleanLeftoverFolders.Should().BeTrue("a file that does not mention the option leaves it on");

        directory.WriteConfig("import:\n  fake_lossless_check: reject\n");

        Bind(directory, new Hashtable()).CleanLeftoverFolders.Should().BeTrue();
    }

    [Fact]
    public void The_option_is_read_from_the_import_section_of_config_yml()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("import:\n  clean_leftover_folders: false\n");

        Bind(directory, new Hashtable()).CleanLeftoverFolders.Should().BeFalse();
    }

    [Fact]
    public void The_environment_wins_over_config_yml()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("import:\n  clean_leftover_folders: true\n");

        var environment = new Hashtable { ["APP__IMPORT__CLEAN_LEFTOVER_FOLDERS"] = "false" };

        Bind(directory, environment).CleanLeftoverFolders.Should().BeFalse();
    }

    private static ImportOptions Bind(TemporaryConfigDirectory directory, IDictionary environment)
    {
        var builder = new ConfigurationBuilder();
        builder.AddWondarrConfiguration(directory.Paths, environment);
        var options = new ImportOptions();
        builder.Build().GetSection("Import").Bind(options);

        return options;
    }
}
