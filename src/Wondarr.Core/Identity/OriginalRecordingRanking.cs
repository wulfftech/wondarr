using Wondarr.Core.Metadata.MusicBrainz;

namespace Wondarr.Core.Identity;

/// <summary>
/// Puts the original recording of a song ahead of its compilation, live and re-recorded namesakes
/// (MATCHING_ENGINE.md §6.3). A song such as "Nine Days – Story of a Girl" exists on MusicBrainz as
/// several recordings with the same artist and title, and the one the owner means is the album
/// recording, not the 2007 compilation's. Only recordings that share a folded artist and title are
/// ranked against each other this way; across different titles the score still decides.
/// </summary>
internal static class OriginalRecordingRanking
{
    /// <summary>Two lengths this close (either way) count as the same length when finding the most common one.</summary>
    internal const int LengthBucketMs = 2000;

    /// <summary>Sorts after every real date.</summary>
    private const string MissingDate = "￿";

    /// <summary>The secondary release-group types that make an album something other than the album.</summary>
    private static readonly string[] ExcludedSecondaryTypes = ["Compilation", "Soundtrack"];

    /// <summary>The folded artist and title two recordings must share to be ranked against each other.</summary>
    /// <param name="artistCredit">The display credit.</param>
    /// <param name="title">The recording title.</param>
    internal static string GroupKey(string artistCredit, string title) =>
        string.Concat(TextMatching.NormalizeArtist(artistCredit), "\u001f", TextMatching.Normalize(title));

    /// <summary>
    /// The date of the recording's earliest Official Album release: an official, dated release whose
    /// release group is of primary type Album with no secondary type Compilation or Soundtrack, and
    /// none of Live either unless the query asked for a live version. Singles, EPs, promos and
    /// bootlegs do not count, so they never hide the album. <c>null</c> when it is on no such album.
    /// </summary>
    /// <param name="recording">The recording, with the releases MusicBrainz listed for it.</param>
    /// <param name="queryAskedLive">Whether the query itself asked for a live version.</param>
    internal static string? OfficialAlbumDate(MbRecording recording, bool queryAskedLive)
    {
        ArgumentNullException.ThrowIfNull(recording);

        string? earliest = null;

        foreach (var release in recording.Releases)
        {
            if (!IsOfficial(release)
                || string.IsNullOrWhiteSpace(release.Date)
                || !IsAlbumGroup(release.ReleaseGroup, queryAskedLive))
            {
                continue;
            }

            if (earliest is null || IsEarlier(release.Date, earliest))
            {
                earliest = release.Date;
            }
        }

        return earliest;
    }

    /// <summary>
    /// Whether <paramref name="date"/> is strictly earlier than <paramref name="other"/>, compared by
    /// the prefix both have: "2000" and "2000-05-30" are the same year, so neither is earlier.
    /// </summary>
    /// <param name="date">A date, as MusicBrainz writes it (year, year-month or full).</param>
    /// <param name="other">The date to compare with.</param>
    internal static bool IsEarlier(string date, string other)
    {
        ArgumentNullException.ThrowIfNull(date);
        ArgumentNullException.ThrowIfNull(other);

        var length = Math.Min(date.Length, other.Length);

        return string.CompareOrdinal(date, 0, other, 0, length) < 0;
    }

    /// <summary>
    /// The order in which entries rank, as indices into <paramref name="entries"/>. Entries that share
    /// a group key are ranked together: the original ones first (those on an Official Album no other
    /// member's Official Album date precedes), then the higher score (as before: scores clamp at 100,
    /// so ties are common), then the earlier date, then the length closest to the group's most common
    /// one. Groups are ordered by their best score and never interleave. A tie on everything keeps
    /// the order the entries came in.
    /// </summary>
    /// <param name="entries">The entries, already in the order that should settle every remaining tie.</param>
    internal static IReadOnlyList<int> Order(IReadOnlyList<Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var groups = new List<List<int>>();
        var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (var index = 0; index < entries.Count; index++)
        {
            if (!byKey.TryGetValue(entries[index].GroupKey, out var members))
            {
                members = [];
                byKey[entries[index].GroupKey] = members;
                groups.Add(members);
            }

            members.Add(index);
        }

        var ordered = groups
            .Select((members, position) => (Members: RankWithinGroup(entries, members), Position: position))
            .OrderByDescending(group => group.Members.Max(member => entries[member].Score))
            .ThenBy(
                group => group.Members.Select(member => DateKey(entries[member].Date)).Order(StringComparer.Ordinal).First(),
                StringComparer.Ordinal)
            .ThenBy(group => group.Position);

        var result = new List<int>(entries.Count);

        foreach (var group in ordered)
        {
            result.AddRange(group.Members);
        }

        return result;
    }

    /// <summary>The most common length among the given ones, in buckets of ±2 s; the shorter one when two are equally common.</summary>
    /// <param name="lengthsMs">The lengths, in milliseconds.</param>
    internal static int? MostCommonLength(IReadOnlyList<int> lengthsMs)
    {
        ArgumentNullException.ThrowIfNull(lengthsMs);

        int? best = null;
        var bestCount = 0;

        foreach (var length in lengthsMs.Order())
        {
            var count = lengthsMs.Count(other => Math.Abs(other - length) <= LengthBucketMs);

            if (count > bestCount)
            {
                best = length;
                bestCount = count;
            }
        }

        return best;
    }

    private static List<int> RankWithinGroup(IReadOnlyList<Entry> entries, List<int> members)
    {
        var lengths = members
            .Where(member => entries[member].LengthMs is not null)
            .Select(member => entries[member].LengthMs!.Value)
            .ToList();
        var common = MostCommonLength(lengths);

        string? earliestAlbum = null;

        foreach (var member in members)
        {
            if (entries[member].AlbumDate is { } albumDate && (earliestAlbum is null || IsEarlier(albumDate, earliestAlbum)))
            {
                earliestAlbum = albumDate;
            }
        }

        return
        [
            .. members
                .OrderByDescending(member => IsOriginal(entries[member], earliestAlbum))
                .ThenByDescending(member => entries[member].Score)
                .ThenBy(member => DateKey(entries[member].Date), StringComparer.Ordinal)
                .ThenBy(member => LengthDistance(entries[member].LengthMs, common))
                .ThenBy(member => member),
        ];
    }

    /// <summary>On an Official Album, and no other member of the group is on an earlier one.</summary>
    private static bool IsOriginal(Entry entry, string? earliestAlbum) =>
        entry.AlbumDate is { } albumDate && earliestAlbum is not null && !IsEarlier(earliestAlbum, albumDate);

    /// <summary>The distance from the common length; an unknown length is as far as it gets.</summary>
    private static int LengthDistance(int? lengthMs, int? commonMs) =>
        lengthMs is { } length && commonMs is { } common ? Math.Abs(length - common) : int.MaxValue;

    /// <summary>A date as a sort key: a missing date sorts after every real one.</summary>
    private static string DateKey(string? date) =>
        string.IsNullOrWhiteSpace(date) ? MissingDate : date;

    private static bool IsOfficial(MbRelease release) =>
        string.IsNullOrWhiteSpace(release.Status)
        || string.Equals(release.Status, "Official", StringComparison.OrdinalIgnoreCase);

    private static bool IsAlbumGroup(MbReleaseGroup? group, bool queryAskedLive)
    {
        if (group is null || !string.Equals(group.PrimaryType, "Album", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var secondary in group.SecondaryTypes)
        {
            if (ExcludedSecondaryTypes.Contains(secondary, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!queryAskedLive && string.Equals(secondary, "Live", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One thing to rank: its score, the group it competes in, and what the tie-breaks look at.</summary>
    /// <param name="Score">The candidate score, 0 to 100.</param>
    /// <param name="GroupKey">From <see cref="GroupKey"/>.</param>
    /// <param name="AlbumDate">From <see cref="OfficialAlbumDate"/>; <c>null</c> when the entry is not on an Official Album, or its length is not acceptable against the reference.</param>
    /// <param name="Date">The first release date, as MusicBrainz writes it.</param>
    /// <param name="LengthMs">The length, when known.</param>
    internal readonly record struct Entry(double Score, string GroupKey, string? AlbumDate, string? Date, int? LengthMs);
}
