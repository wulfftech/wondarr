namespace Wondarr.Core.Tests.Media;

/// <summary>
/// The recorded media fixtures: real ffprobe and fpcalc output for the tone files, plus the files
/// themselves for the live tests. They are checked in under <c>tests/fixtures/media</c> and are not
/// copied next to the test binaries, so the directory is found by walking up to the repository root.
/// </summary>
internal static class MediaFixtures
{
    /// <summary>The checked-in <c>tests/fixtures/media</c> directory.</summary>
    public static string Directory { get; } = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "media");

    /// <summary>The full path of one fixture, the recorded output or the audio file itself.</summary>
    public static string File(string name) => Path.Combine(Directory, name);

    /// <summary>Reads one recorded output file.</summary>
    public static string Read(string name) => System.IO.File.ReadAllText(File(name));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (System.IO.Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"Could not find tests/fixtures above {AppContext.BaseDirectory}");
    }
}