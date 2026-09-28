using Wondarr.Core.Configuration;

namespace Wondarr.Core.Tests.Configuration;

/// <summary>
/// A throwaway configuration directory so tests never touch <c>/config</c> or
/// <c>%LOCALAPPDATA%</c>.
/// </summary>
public sealed class TemporaryConfigDirectory : IDisposable
{
    public TemporaryConfigDirectory()
    {
        Paths = new WondarrPaths(Path.Combine(Path.GetTempPath(), "wondarr-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Paths.ConfigDir);
    }

    public WondarrPaths Paths { get; }

    public void WriteConfig(string yaml) => File.WriteAllText(Paths.ConfigFile, yaml);

    public string ReadConfig() => File.ReadAllText(Paths.ConfigFile);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Paths.ConfigDir, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory must never fail a test run.
        }
    }
}
