using System.Globalization;
using System.Text.RegularExpressions;

namespace Wondarr.Core.Updates;

/// <summary>
/// A Semantic Versioning 2.0 version: <c>MAJOR.MINOR.PATCH[-pre.release][+build]</c>, ordered by the
/// spec's precedence rules (build metadata is ignored, a pre-release sorts below its release, numeric
/// identifiers sort below alphanumeric ones and compare as numbers).
/// </summary>
public sealed partial class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private readonly string[] _preRelease;

    private SemanticVersion(long major, long minor, long patch, string[] preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _preRelease = preRelease;
    }

    /// <summary>Gets the major number.</summary>
    public long Major { get; }

    /// <summary>Gets the minor number.</summary>
    public long Minor { get; }

    /// <summary>Gets the patch number.</summary>
    public long Patch { get; }

    /// <summary>Gets a value indicating whether the version has a pre-release part (<c>-rc.1</c>).</summary>
    public bool IsPreRelease => _preRelease.Length > 0;

    /// <summary>Gets the pre-release identifiers, empty for a release.</summary>
    public IReadOnlyList<string> PreReleaseIdentifiers => _preRelease;

    /// <summary>
    /// Parses a version, with an optional leading <c>v</c>. Returns <see langword="null"/> for anything
    /// that is not a strict Semantic Versioning 2.0 version.
    /// </summary>
    /// <param name="text">The text to parse, for example <c>v0.1.0-rc.1</c>.</param>
    public static SemanticVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Pattern().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        if (!long.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !long.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !long.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return null;
        }

        var pre = match.Groups["pre"].Success
            ? match.Groups["pre"].Value.Split('.')
            : [];

        return new SemanticVersion(major, minor, patch, pre);
    }

    /// <summary>Orders two versions; <see langword="null"/> sorts below everything.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    public static int Compare(SemanticVersion? left, SemanticVersion? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        return right is null ? 1 : left.CompareTo(right);
    }

    /// <summary>Gets a value indicating whether two versions have the same precedence.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    public static bool operator ==(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) == 0;

    /// <summary>Gets a value indicating whether two versions differ in precedence.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) != 0;

    /// <summary>Gets a value indicating whether <paramref name="left"/> is newer than <paramref name="right"/>.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    public static bool operator >(SemanticVersion left, SemanticVersion right) => Compare(left, right) > 0;

    /// <summary>Gets a value indicating whether <paramref name="left"/> is older than <paramref name="right"/>.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    public static bool operator <(SemanticVersion left, SemanticVersion right) => Compare(left, right) < 0;

    /// <summary>Gets a value indicating whether <paramref name="left"/> is newer than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => Compare(left, right) >= 0;

    /// <summary>Gets a value indicating whether <paramref name="left"/> is older than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => Compare(left, right) <= 0;

    /// <inheritdoc />
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major.CompareTo(other.Major);
        if (core != 0)
        {
            return core;
        }

        core = Minor.CompareTo(other.Minor);
        if (core != 0)
        {
            return core;
        }

        core = Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }

        // A version without a pre-release part is higher than one with it.
        if (_preRelease.Length == 0 || other._preRelease.Length == 0)
        {
            return other._preRelease.Length.CompareTo(_preRelease.Length);
        }

        var shared = Math.Min(_preRelease.Length, other._preRelease.Length);

        for (var index = 0; index < shared; index++)
        {
            var result = CompareIdentifier(_preRelease[index], other._preRelease[index]);
            if (result != 0)
            {
                return result;
            }
        }

        // All shared identifiers equal: the longer set has the higher precedence.
        return _preRelease.Length.CompareTo(other._preRelease.Length);
    }

    /// <inheritdoc />
    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', _preRelease));

    /// <summary>The version without a <c>v</c> prefix or build metadata, for example <c>0.1.0-rc.1</c>.</summary>
    public override string ToString()
    {
        var core = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

        return _preRelease.Length == 0 ? core : string.Concat(core, "-", string.Join('.', _preRelease));
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
        var rightNumeric = long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

        if (leftNumeric && rightNumeric)
        {
            return leftNumber.CompareTo(rightNumber);
        }

        // Numeric identifiers always have lower precedence than alphanumeric ones.
        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        return string.CompareOrdinal(left, right);
    }

    // The semver.org expression, with an optional leading "v". A numeric identifier has no leading zero.
    [GeneratedRegex(
        @"^v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+(?<build>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
