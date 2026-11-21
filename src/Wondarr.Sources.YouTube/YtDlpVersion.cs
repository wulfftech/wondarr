using System.Globalization;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// yt-dlp's version strings: a date, <c>YYYY.MM.DD</c>, with an optional <c>.N</c> suffix for a second
/// release on the same day (stable) or the build time (nightly, <c>2026.10.27.232859</c>). They are
/// compared as numbers, part by part, never as text.
/// </summary>
public static class YtDlpVersion
{
    /// <summary>Reads a version into its numeric parts.</summary>
    /// <param name="version">The string <c>yt-dlp --version</c> printed.</param>
    /// <param name="parts">The numeric parts, year first, when the string is a version.</param>
    /// <returns>Whether <paramref name="version"/> is a yt-dlp version.</returns>
    public static bool TryParse(string? version, out long[] parts)
    {
        parts = [];

        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var pieces = version.Trim().Split('.');

        if (pieces.Length is < 3 or > 4)
        {
            return false;
        }

        var parsed = new long[pieces.Length];

        for (var index = 0; index < pieces.Length; index++)
        {
            if (!long.TryParse(pieces[index], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[index]))
            {
                return false;
            }
        }

        parts = parsed;

        return true;
    }

    /// <summary>Compares two versions as numbers.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns>
    /// Negative when <paramref name="left"/> is older, positive when it is newer, zero when they are the same
    /// release, or <see langword="null"/> when either is not a version.
    /// </returns>
    public static int? Compare(string? left, string? right)
    {
        if (!TryParse(left, out var first) || !TryParse(right, out var second))
        {
            return null;
        }

        var length = Math.Max(first.Length, second.Length);

        for (var index = 0; index < length; index++)
        {
            var a = index < first.Length ? first[index] : 0;
            var b = index < second.Length ? second[index] : 0;

            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }

        return 0;
    }

    /// <summary>The calendar date a version carries.</summary>
    /// <param name="version">The version string.</param>
    /// <returns>The date, or <see langword="null"/> when it is not a version or not a real date.</returns>
    public static DateOnly? DateOf(string? version)
    {
        if (!TryParse(version, out var parts)
            || parts[0] is < 2000 or > 9999
            || parts[1] is < 1 or > 12
            || parts[2] is < 1 or > 31)
        {
            return null;
        }

        try
        {
            return new DateOnly((int)parts[0], (int)parts[1], (int)parts[2]);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
