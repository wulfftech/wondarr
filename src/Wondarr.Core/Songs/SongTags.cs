using System.Globalization;

namespace Wondarr.Core.Songs;

/// <summary>
/// The rules for a song's free-form tags: trimmed, lower-case, no empties, no duplicates, no commas
/// (the CSV and list imports keep tags in one comma-separated cell).
/// </summary>
public static class SongTags
{
    /// <summary>The longest tag, in characters.</summary>
    public const int MaxTagLength = 64;

    /// <summary>The most tags one song carries.</summary>
    public const int MaxTagsPerSong = 50;

    /// <summary>Normalises tags, keeping the first occurrence order.</summary>
    /// <param name="tags">The tags as the caller wrote them.</param>
    /// <returns>The trimmed, lower-cased, distinct, non-empty tags.</returns>
    /// <exception cref="ArgumentException">A tag is over 64 characters or holds a comma, or there are more than 50.</exception>
    public static List<string> Normalize(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in tags)
        {
            var tag = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (tag.Length == 0)
            {
                continue;
            }

            if (tag.Length > MaxTagLength)
            {
                throw new ArgumentException(
                    $"A tag must be at most {MaxTagLength.ToString(CultureInfo.InvariantCulture)} characters.",
                    nameof(tags));
            }

            if (tag.Contains(',', StringComparison.Ordinal))
            {
                throw new ArgumentException("A tag must not contain a comma.", nameof(tags));
            }

            if (seen.Add(tag))
            {
                result.Add(tag);
            }
        }

        if (result.Count > MaxTagsPerSong)
        {
            throw new ArgumentException(
                $"A song carries at most {MaxTagsPerSong.ToString(CultureInfo.InvariantCulture)} tags.",
                nameof(tags));
        }

        return result;
    }
}
