using Compilarr.Core.Configuration;

namespace Compilarr.Core.Tests.Configuration;

/// <summary>
/// A throwaway configuration directory so tests never touch <c>/config</c> or
/// <c>%LOCALAPPDATA%</c>.
/// </summary>
public sealed class TemporaryConfigDirectory : IDisposable
{
    public TemporaryConfigDirectory()
    {
        Paths = new CompilarrPaths(Path.Combine(Path.GetTempPath(), "compilarr-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Paths.ConfigDir);
    }

    public CompilarrPaths Paths { get; }

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
