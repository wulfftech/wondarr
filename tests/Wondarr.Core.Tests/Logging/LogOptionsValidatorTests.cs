using System.Collections;
using Wondarr.Core.Configuration;
using Wondarr.Core.Logging;
using Wondarr.Core.Tests.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Wondarr.Core.Tests.Logging;

public class LogOptionsValidatorTests
{
    [Fact]
    public void Missing_section_uses_the_documented_defaults()
    {
        using var directory = new TemporaryConfigDirectory();

        var options = Bind(directory, new Hashtable());

        options.Level.Should().Be("Information");
        options.ConsoleFormat.Should().Be(LogConsoleFormat.Json);
        options.RetainedFiles.Should().Be(7);
        options.FileSizeLimitMb.Should().Be(10);
    }

    [Fact]
    public void Yaml_keys_bind_with_their_snake_case_spelling()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("log:\n  level: debug\n  console_format: text\n  retained_files: 3\n  file_size_limit_mb: 25\n");

        var options = Bind(directory, new Hashtable());

        options.Level.Should().Be("debug");
        options.ConsoleFormat.Should().Be(LogConsoleFormat.Text);
        options.RetainedFiles.Should().Be(3);
        options.FileSizeLimitMb.Should().Be(25);
    }

    [Theory]
    [InlineData("log:\n  level: chatty\n", "log.level")]
    [InlineData("log:\n  retained_files: 0\n", "log.retained_files")]
    [InlineData("log:\n  file_size_limit_mb: 5000\n", "log.file_size_limit_mb")]
    public void Out_of_range_values_fail_validation_with_the_yaml_key(string yaml, string expectedKey)
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig(yaml);

        var act = () => Bind(directory, new Hashtable());

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain(expectedKey);
    }

    private static LogOptions Bind(TemporaryConfigDirectory directory, IDictionary environment)
    {
        var builder = new ConfigurationBuilder();
        builder.AddWondarrConfiguration(directory.Paths, environment);
        var configuration = builder.Build();

        var configure = new ConfigureNamedOptions<LogOptions>(
            Options.DefaultName,
            options => configuration.GetSection("Log").Bind(options));

        var factory = new OptionsFactory<LogOptions>([configure], [], [new LogOptionsValidator()]);

        return factory.Create(Options.DefaultName);
    }
}
