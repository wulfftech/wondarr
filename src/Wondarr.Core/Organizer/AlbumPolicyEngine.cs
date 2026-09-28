using System.Globalization;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;

namespace Wondarr.Core.Organizer;

/// <summary>Chooses the album context of every song from a library's album policy (LIBRARY_OUTPUT.md §7.3).</summary>
public interface IAlbumPolicyEngine
{
    /// <summary>Returns one assignment per input song, in input order.</summary>
    IReadOnlyList<AlbumAssignment> Assign(AlbumPolicyInput input);
}

/// <summary>
/// The pure album-policy engine: no I/O, no clock, and pseudo-album keys come from the injected
/// <c>newGuid</c> delegate only. Assignments are sticky — this batch joins existing albums or creates
/// new ones, it never moves a song the library already holds.
/// </summary>
public sealed class AlbumPolicyEngine(Func<Guid> newGuid) : IAlbumPolicyEngine
{
    private const string OfficialStatus = "Official";
    private const string PseudoAlbumTitle = "Singles";
    private const string VariousArtistsName = "Various Artists";

    private readonly Func<Guid> _newGuid = newGuid ?? throw new ArgumentNullException(nameof(newGuid));

    /// <inheritdoc />
    public IReadOnlyList<AlbumAssignment> Assign(AlbumPolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var placements = new Placement?[input.Songs.Count];

        switch (input.Policy)
        {
            case AlbumPolicy.SinglesOnly:
                break;
            case AlbumPolicy.Compilation:
                AssignCompilation(input, placements);
                break;
            case AlbumPolicy.FewestAlbums:
                AssignFewestAlbums(input, placements);
                break;
            case AlbumPolicy.OriginalAlbum:
                AssignPerSong(input, placements, AlbumPolicy.OriginalAlbum);
                break;
            case AlbumPolicy.SingleRelease:
                AssignPerSong(input, placements, AlbumPolicy.SingleRelease);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(input), input.Policy, "Unknown album policy.");
        }

        AssignPseudoAlbums(input, placements);

        var assignments = new List<AlbumAssignment>(input.Songs.Count);
        for (var index = 0; index < input.Songs.Count; index++)
        {
            assignments.Add(ToAssignment(input.Songs[index], placements[index]!));
        }

        return assignments;
    }

    /// <summary>The library's single Various Artists album: the existing one, or a new one titled with the library name.</summary>
    private void AssignCompilation(AlbumPolicyInput input, Placement?[] placements)
    {
        var existing = input.ExistingAlbums.FirstOrDefault(
            album => album.Kind == AlbumContextKind.Compilation && album.IsVariousArtists);

        string? albumKey = null;
        string? date = null;
        var nextTrackNo = 1;

        if (existing is not null)
        {
            albumKey = existing.AlbumKey;
            date = existing.Date;
            nextTrackNo = existing.MaxTrackNo + 1;
        }
        else if (input.Songs.Count > 0)
        {
            albumKey = _newGuid().ToString();
            date = EarliestYear(input.Songs);
        }

        if (albumKey is null)
        {
            return;
        }

        var albumTitle = existing?.AlbumTitle ?? input.LibraryName;

        for (var index = 0; index < input.Songs.Count; index++)
        {
            placements[index] = new Placement(
                AlbumContextKind.Compilation,
                albumTitle,
                VariousArtistsName,
                albumKey,
                null,
                null,
                nextTrackNo + index,
                1,
                null,
                date,
                true);
        }
    }

    /// <summary>Joins existing albums first, then greedily covers the leftovers with real releases, per artist.</summary>
    private static void AssignFewestAlbums(AlbumPolicyInput input, Placement?[] placements)
    {
        var artistOrder = new List<string>();
        var indicesByArtist = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (var index = 0; index < input.Songs.Count; index++)
        {
            var artistKey = input.Songs[index].ArtistKey;
            if (!indicesByArtist.TryGetValue(artistKey, out var indices))
            {
                indices = [];
                indicesByArtist.Add(artistKey, indices);
                artistOrder.Add(artistKey);
            }

            indices.Add(index);
        }

        foreach (var artistKey in artistOrder)
        {
            var remaining = new List<int>();

            foreach (var index in indicesByArtist[artistKey])
            {
                if (!TryJoinExisting(input, index, placements))
                {
                    remaining.Add(index);
                }
            }

            while (true)
            {
                var cover = BestCover(input, remaining);
                if (cover is null || cover.Count < input.MinTracksPerRealAlbum)
                {
                    break;
                }

                var covered = 0;
                for (var position = remaining.Count - 1; position >= 0; position--)
                {
                    var index = remaining[position];
                    var option = FindOptionWithKey(input.Songs[index], cover.Key);
                    if (option is null)
                    {
                        continue;
                    }

                    placements[index] = PlacementFromOption(option);
                    remaining.RemoveAt(position);
                    covered++;
                }

                if (covered == 0)
                {
                    break;
                }
            }
        }
    }

    /// <summary>One song at a time: join an existing album, else the policy's own preference, else a pseudo-album.</summary>
    private static void AssignPerSong(AlbumPolicyInput input, Placement?[] placements, AlbumPolicy policy)
    {
        for (var index = 0; index < input.Songs.Count; index++)
        {
            if (TryJoinExisting(input, index, placements))
            {
                continue;
            }

            var song = input.Songs[index];
            var option = policy == AlbumPolicy.OriginalAlbum
                ? Pick(song, allowAlbum: true, allowEp: true, allowSingle: false)
                    ?? Pick(song, allowAlbum: false, allowEp: false, allowSingle: true)
                : Pick(song, allowAlbum: false, allowEp: false, allowSingle: true)
                    ?? Pick(song, allowAlbum: true, allowEp: true, allowSingle: false);

            if (option is not null)
            {
                placements[index] = PlacementFromOption(option);
            }
        }
    }

    /// <summary>Files every song still unplaced into its artist's Singles pseudo-album (or the compilation album already handled).</summary>
    private void AssignPseudoAlbums(AlbumPolicyInput input, Placement?[] placements)
    {
        var artistOrder = new List<string>();
        var indicesByArtist = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (var index = 0; index < input.Songs.Count; index++)
        {
            if (placements[index] is not null)
            {
                continue;
            }

            var artistKey = input.Songs[index].ArtistKey;
            if (!indicesByArtist.TryGetValue(artistKey, out var indices))
            {
                indices = [];
                indicesByArtist.Add(artistKey, indices);
                artistOrder.Add(artistKey);
            }

            indices.Add(index);
        }

        foreach (var artistKey in artistOrder)
        {
            var indices = indicesByArtist[artistKey];
            var existing = input.ExistingAlbums.FirstOrDefault(
                album => album.Kind == AlbumContextKind.PseudoSingles &&
                    string.Equals(album.ArtistKey, artistKey, StringComparison.Ordinal));

            string albumKey;
            string? date;
            int trackNo;

            if (existing is not null)
            {
                albumKey = existing.AlbumKey;
                date = existing.Date;
                trackNo = existing.MaxTrackNo + 1;
            }
            else
            {
                albumKey = _newGuid().ToString();
                date = EarliestYear(indices.Select(index => input.Songs[index]));
                trackNo = 1;
            }

            var albumArtist = input.Songs[indices[0]].ArtistName;

            foreach (var index in indices)
            {
                placements[index] = new Placement(
                    AlbumContextKind.PseudoSingles,
                    PseudoAlbumTitle,
                    albumArtist,
                    albumKey,
                    null,
                    null,
                    trackNo++,
                    1,
                    null,
                    date,
                    false);
            }
        }
    }

    /// <summary>Joins the song to an existing non-pseudo album of its artist, when one of its eligible options matches.</summary>
    private static bool TryJoinExisting(AlbumPolicyInput input, int index, Placement?[] placements)
    {
        var song = input.Songs[index];
        ExistingAlbum? bestAlbum = null;
        ReleaseOption? bestOption = null;
        var bestIsExact = false;

        foreach (var album in input.ExistingAlbums)
        {
            if (album.Kind == AlbumContextKind.PseudoSingles ||
                !string.Equals(album.ArtistKey, song.ArtistKey, StringComparison.Ordinal))
            {
                continue;
            }

            var option = FindMatchingOption(song, album);
            if (option is null)
            {
                continue;
            }

            var isExact = string.Equals(option.Key, album.AlbumKey, StringComparison.Ordinal);

            if (bestAlbum is null || IsBetterJoin(album, isExact, bestAlbum, bestIsExact))
            {
                bestAlbum = album;
                bestOption = option;
                bestIsExact = isExact;
            }
        }

        if (bestAlbum is null || bestOption is null)
        {
            return false;
        }

        placements[index] = new Placement(
            bestAlbum.Kind,
            bestAlbum.AlbumTitle,
            bestAlbum.AlbumArtist,
            bestAlbum.AlbumKey,
            bestAlbum.MbReleaseId,
            bestAlbum.MbReleaseGroupId,
            bestOption.TrackNo,
            bestOption.DiscNo ?? 1,
            bestOption.TotalTracks,
            bestAlbum.Date,
            bestAlbum.IsVariousArtists);

        return true;
    }

    /// <summary>The option of the song a candidate album is matched through: the exact key first, then the release group.</summary>
    private static ReleaseOption? FindMatchingOption(SongToPlace song, ExistingAlbum album)
    {
        ReleaseOption? byKey = null;
        ReleaseOption? byGroup = null;

        foreach (var option in song.Options)
        {
            if (!IsEligible(option, song.Flags))
            {
                continue;
            }

            if (byKey is null && string.Equals(option.Key, album.AlbumKey, StringComparison.Ordinal))
            {
                byKey = option;
            }

            if (byGroup is null &&
                option.MbReleaseGroupId is { Length: > 0 } groupId &&
                album.MbReleaseGroupId is { Length: > 0 } albumGroupId &&
                string.Equals(groupId, albumGroupId, StringComparison.Ordinal))
            {
                byGroup = option;
            }
        }

        return byKey ?? byGroup;
    }

    /// <summary>Most tracks, then earliest date, then key ordinal; an exact key match beats a release-group match.</summary>
    private static bool IsBetterJoin(ExistingAlbum candidate, bool candidateIsExact, ExistingAlbum current, bool currentIsExact)
    {
        if (candidateIsExact != currentIsExact)
        {
            return candidateIsExact;
        }

        if (candidate.TrackCount != current.TrackCount)
        {
            return candidate.TrackCount > current.TrackCount;
        }

        var dateOrder = CompareOptionalDates(candidate.Date, current.Date);
        if (dateOrder != 0)
        {
            return dateOrder < 0;
        }

        return StringComparer.Ordinal.Compare(candidate.AlbumKey, current.AlbumKey) < 0;
    }

    /// <summary>The best remaining option key: most songs, then Album before EP before Single, then earliest date, then key.</summary>
    private static Cover? BestCover(AlbumPolicyInput input, List<int> remaining)
    {
        var covers = new List<Cover>();
        var positionByKey = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var index in remaining)
        {
            var song = input.Songs[index];

            foreach (var option in song.Options)
            {
                if (!IsEligible(option, song.Flags))
                {
                    continue;
                }

                if (positionByKey.TryGetValue(option.Key, out var position))
                {
                    covers[position] = covers[position] with { Count = covers[position].Count + 1 };
                }
                else
                {
                    positionByKey.Add(option.Key, covers.Count);
                    covers.Add(new Cover(option.Key, option, 1));
                }
            }
        }

        Cover? best = null;

        foreach (var cover in covers)
        {
            if (best is null || IsBetterCover(cover, best))
            {
                best = cover;
            }
        }

        return best;
    }

    private static bool IsBetterCover(Cover candidate, Cover current)
    {
        if (candidate.Count != current.Count)
        {
            return candidate.Count > current.Count;
        }

        var candidateRank = TypeRank(KindOf(candidate.Representative.PrimaryType));
        var currentRank = TypeRank(KindOf(current.Representative.PrimaryType));
        if (candidateRank != currentRank)
        {
            return candidateRank < currentRank;
        }

        var dateOrder = CompareOptionalDates(candidate.Representative.Date, current.Representative.Date);
        if (dateOrder != 0)
        {
            return dateOrder < 0;
        }

        return StringComparer.Ordinal.Compare(candidate.Key, current.Key) < 0;
    }

    /// <summary>The eligible option of the requested kinds with the earliest release-group date.</summary>
    private static ReleaseOption? Pick(SongToPlace song, bool allowAlbum, bool allowEp, bool allowSingle)
    {
        ReleaseOption? best = null;

        foreach (var option in song.Options)
        {
            if (!IsEligible(option, song.Flags))
            {
                continue;
            }

            var kind = KindOf(option.PrimaryType);
            var wanted = (kind == AlbumContextKind.Album && allowAlbum) ||
                (kind == AlbumContextKind.Ep && allowEp) ||
                (kind == AlbumContextKind.Single && allowSingle);

            if (!wanted)
            {
                continue;
            }

            if (best is null || IsBetterPick(option, best))
            {
                best = option;
            }
        }

        return best;
    }

    private static bool IsBetterPick(ReleaseOption candidate, ReleaseOption current)
    {
        var dateOrder = CompareOptionalDates(
            candidate.ReleaseGroupFirstDate ?? candidate.Date,
            current.ReleaseGroupFirstDate ?? current.Date);

        if (dateOrder != 0)
        {
            return dateOrder < 0;
        }

        var candidateRank = TypeRank(KindOf(candidate.PrimaryType));
        var currentRank = TypeRank(KindOf(current.PrimaryType));
        if (candidateRank != currentRank)
        {
            return candidateRank < currentRank;
        }

        dateOrder = CompareOptionalDates(candidate.Date, current.Date);
        if (dateOrder != 0)
        {
            return dateOrder < 0;
        }

        return StringComparer.Ordinal.Compare(candidate.Key, current.Key) < 0;
    }

    /// <summary>An option is eligible when it is an official Album/EP/Single whose secondary types the song's flags allow.</summary>
    private static bool IsEligible(ReleaseOption option, VersionFlags flags)
    {
        if (!string.Equals(option.Status ?? OfficialStatus, OfficialStatus, StringComparison.OrdinalIgnoreCase) ||
            KindOf(option.PrimaryType) is null)
        {
            return false;
        }

        foreach (var secondaryType in option.SecondaryTypes)
        {
            var allowed =
                (Matches(secondaryType, "Live") && (flags & VersionFlags.Live) == VersionFlags.Live) ||
                ((Matches(secondaryType, "Remix") || Matches(secondaryType, "DJ-mix")) &&
                    (flags & VersionFlags.Remix) == VersionFlags.Remix);

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Matches(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>The album context kind of an eligibility-approved primary type.</summary>
    private static AlbumContextKind? KindOf(string? primaryType) => primaryType switch
    {
        null => null,
        _ when Matches(primaryType, "Album") => AlbumContextKind.Album,
        _ when Matches(primaryType, "EP") => AlbumContextKind.Ep,
        _ when Matches(primaryType, "Single") => AlbumContextKind.Single,
        _ => null,
    };

    /// <summary>Album before EP before Single; the enum's own order puts Single first, so rank explicitly.</summary>
    private static int TypeRank(AlbumContextKind? kind) => kind switch
    {
        AlbumContextKind.Album => 0,
        AlbumContextKind.Ep => 1,
        AlbumContextKind.Single => 2,
        _ => 3,
    };

    private static int CompareOptionalDates(string? left, string? right)
    {
        if (left is null)
        {
            return right is null ? 0 : 1;
        }

        return right is null ? -1 : StringComparer.Ordinal.Compare(left, right);
    }

    /// <summary>The first eligible option carrying a key.</summary>
    private static ReleaseOption? FindOptionWithKey(SongToPlace song, string key)
    {
        foreach (var option in song.Options)
        {
            if (string.Equals(option.Key, key, StringComparison.Ordinal) && IsEligible(option, song.Flags))
            {
                return option;
            }
        }

        return null;
    }

    private static Placement PlacementFromOption(ReleaseOption option) => new(
        KindOf(option.PrimaryType) ?? AlbumContextKind.Album,
        option.Title,
        option.AlbumArtist,
        option.Key,
        option.MbReleaseId,
        option.MbReleaseGroupId,
        option.TrackNo,
        option.DiscNo ?? 1,
        option.TotalTracks,
        option.Date,
        option.IsVariousArtists);

    /// <summary>The earliest 4-digit year among the songs' original dates, as a string; null when there is none.</summary>
    private static string? EarliestYear(IEnumerable<SongToPlace> songs)
    {
        int? earliest = null;

        foreach (var song in songs)
        {
            if (song.OriginalDate is { Length: >= 4 } originalDate &&
                int.TryParse(originalDate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year))
            {
                earliest = earliest is null || year < earliest ? year : earliest;
            }
        }

        return earliest?.ToString(CultureInfo.InvariantCulture);
    }

    private static AlbumAssignment ToAssignment(SongToPlace song, Placement placement) => new()
    {
        SongRef = song.Ref,
        Kind = placement.Kind,
        AlbumTitle = placement.AlbumTitle,
        AlbumArtist = placement.AlbumArtist,
        AlbumKey = placement.AlbumKey,
        MbReleaseId = placement.MbReleaseId,
        MbReleaseGroupId = placement.MbReleaseGroupId,
        TrackNo = placement.TrackNo,
        DiscNo = placement.DiscNo,
        TotalTracks = placement.TotalTracks,
        Date = placement.Date,
        OriginalDate = song.OriginalDate,
        IsVariousArtists = placement.IsVariousArtists,
    };

    /// <summary>The album context one song ended up in, before the song's own fields are merged in.</summary>
    private sealed record Placement(
        AlbumContextKind Kind,
        string AlbumTitle,
        string AlbumArtist,
        string AlbumKey,
        string? MbReleaseId,
        string? MbReleaseGroupId,
        int? TrackNo,
        int? DiscNo,
        int? TotalTracks,
        string? Date,
        bool IsVariousArtists);

    /// <summary>One candidate for the greedy set cover: an option key, the option that introduced it, and its song count.</summary>
    private sealed record Cover(string Key, ReleaseOption Representative, int Count);
}
