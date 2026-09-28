using System.Text.Json;

namespace Wondarr.Core.Tests.Sources;

/// <summary>
/// Reads the golden fixtures the Soulseek parser and quality tests are pinned to. They are checked in
/// under <c>tests/fixtures</c> and are not copied next to the test binaries, so the directory is found by
/// walking up from the test output directory to the repository root.
/// </summary>
internal static class SoulseekFixtures
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The checked-in <c>tests/fixtures</c> directory.</summary>
    public static string FixtureDirectory { get; } = Path.Combine(FindRepositoryRoot(), "tests", "fixtures");

    /// <summary>Deserialises one fixture file under <c>tests/fixtures</c>.</summary>
    public static T Load<T>(string relativePath) => LoadFile<T>(Path.Combine(FixtureDirectory, relativePath));

    /// <summary>Deserialises one fixture file by absolute path.</summary>
    public static T LoadFile<T>(string path)
    {
        using var stream = File.OpenRead(path);
        var value = JsonSerializer.Deserialize<T>(stream, Options);

        return value ?? throw new InvalidOperationException($"{path} deserialised to null");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"Could not find tests/fixtures above {AppContext.BaseDirectory}");
    }
}
