namespace Wondarr.Core.Tests.Tagging;

/// <summary>
/// Locates the recorded media fixtures (<c>tests/fixtures/media/</c>) from the test output directory.
/// </summary>
public static class FixtureMedia
{
    private static readonly Lazy<string> MediaDirectory = new(Locate);

    /// <summary>Gets the absolute path of the media fixtures directory.</summary>
    public static string Directory => MediaDirectory.Value;

    /// <summary>Returns the absolute path of one recorded media fixture.</summary>
    /// <param name="fileName">Fixture file name, e.g. <c>tone-320.mp3</c>.</param>
    public static string Path(string fileName) => System.IO.Path.Combine(Directory, fileName);

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, "tests", "fixtures", "media");
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate tests/fixtures/media above the test output directory.");
    }
}