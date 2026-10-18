namespace Wondarr.Core.References;

/// <summary>
/// When a reference file that has gone missing stops owning its song.
/// Releasing at the first scan would mass-search a library whenever its share is half unmounted, so a
/// song keeps its missing file through a grace period, and a scan that finds too many files gone at once
/// releases nothing at all.
/// </summary>
public static class ReferenceMissingRules
{
    /// <summary>How long a row has to have been missing before its song is let go.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromHours(24);

    /// <summary>The share of a library's owned files that may be missing before it is a mount problem.</summary>
    private const double MountFraction = 0.10;

    /// <summary>The number of missing files above which it is a mount problem, however large the library.</summary>
    private const int MountMaximum = 200;

    /// <summary>
    /// A floor for tiny libraries: without it, a library of five files would call one deleted file a
    /// mount problem (10 % of 5 is 0.5), and a deleted file could never release its song.
    /// </summary>
    private const int MountFloor = 3;

    /// <summary>The most files that may be missing at once before the scan stops trusting the disk.</summary>
    /// <param name="ownedFiles">How many rows own a song: identified, or missing with a song.</param>
    /// <returns>The threshold; strictly more missing files than this is a mount problem.</returns>
    public static int MountThreshold(int ownedFiles) =>
        Math.Min(MountMaximum, Math.Max(MountFloor, (int)Math.Floor(ownedFiles * MountFraction)));

    /// <summary>Whether this many missing files out of this many owned ones looks like a share that is not mounted.</summary>
    /// <param name="ownedFiles">How many rows own a song: identified, or missing with a song.</param>
    /// <param name="missingFiles">How many of them are missing.</param>
    /// <returns><see langword="true"/> when nothing should be released.</returns>
    public static bool IsMountProblem(int ownedFiles, int missingFiles) =>
        missingFiles > MountThreshold(ownedFiles);
}
