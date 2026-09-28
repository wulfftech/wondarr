using Wondarr.Core.Configuration;
using Wondarr.Core.HealthCheck;
using FluentAssertions;
using Xunit;

namespace Wondarr.Core.Tests.HealthCheck;

public class FolderHealthCheckTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "wondarr-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Reports_ok_and_names_the_path_when_the_folder_is_writable()
    {
        var check = new LogFolderHealthCheck(new WondarrPaths(_directory));

        var result = await check.CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Ok);
        result.Source.Should().Be("LogFolderHealthCheck");
        result.Message.Should().Contain(Path.Combine(_directory, "logs"));
    }

    [Fact]
    public async Task Reports_an_error_when_the_folder_cannot_be_created()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        await File.WriteAllTextAsync(_directory, string.Empty);

        var check = new ConfigFolderHealthCheck(new WondarrPaths(_directory));

        var result = await check.CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().Contain("is not writable");
    }

    public void Dispose()
    {
        if (File.Exists(_directory))
        {
            File.Delete(_directory);
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
