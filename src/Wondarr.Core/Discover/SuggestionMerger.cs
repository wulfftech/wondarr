using Wondarr.Core.Identity;

namespace Wondarr.Core.Discover;

/// <summary>What one source returned across every seed of a request.</summary>
/// <param name="Name">The source's name.</param>
/// <param name="Weight">The source's weight.</param>
/// <param name="Status">Whether it answered for at least one seed, failed for all, or had nothing to ask.</param>
/// <param name="Lists">One ranked list per seed the source answered for.</param>
internal sealed record SourceRun(
    string Name,
    double Weight,
    RecommendationSourceStatus Status,
    IReadOnlyList<IReadOnlyList<RecommendationCandidate>> Lists);

/// <summary>The columns of a library song the filters compare a suggestion against.</summary>
/// <param name="MbRecordingId">The recording MBID.</param>
/// <param name="DeezerId">The Deezer track id.</param>
/// <param name="Isrcs">The ISRCs.</param>
/// <param name="Title">The title.</param>
/// <param name="ArtistCredit">The display credit.</param>
/// <param name="ArtistName">The main artist's name.</param>
internal sealed record LibraryRow(
    string? MbRecordingId,
    long? DeezerId,
    IReadOnlyList<string> Isrcs,
    string Title,
    string ArtistCredit,
    string ArtistName);

/// <summary>
/// The pure part of Discover: merging what the sources returned into one ranked list, and filtering and
/// capping it. Matching uses <see cref="TextMatching"/>, the same normaliser the identity resolver uses.
/// </summary>
internal static class SuggestionMerger
{
    /// <summary>
    /// Merges the sources' lists. Two candidates are one suggestion when they share a recording MBID, else an
    /// ISRC, else a normalised artist and title. A source's rank becomes a score of
    /// <c>weight × (n − index) / n</c>; a candidate keeps the best score each source gave it, and the
    /// scores of different sources add up.
    /// </summary>
    /// <param name="runs">The sources, in the order whose first answer wins a field.</param>
    public static List<DiscoverSuggestion> Merge(IReadOnlyList<SourceRun> runs)
    {
        var groups = new List<Group>();
        var byMbid = new Dictionary<string, Group>(StringComparer.Ordinal);
        var byIsrc = new Dictionary<string, Group>(StringComparer.Ordinal);
        var byText = new Dictionary<string, Group>(StringComparer.Ordinal);

        foreach (var run in runs)
        {
            foreach (var list in run.Lists)
            {
                for (var index = 0; index < list.Count; index++)
                {
                    var candidate = list[index];

                    if (string.IsNullOrWhiteSpace(candidate.Title) || string.IsNullOrWhiteSpace(candidate.Artist))
                    {
                        continue;
                    }

                    var mbid = NormalizeMbid(candidate.MbRecordingId);
                    var isrc = NormalizeIsrc(candidate.Isrc);
                    var text = TextKey(candidate.Artist, candidate.Title);

                    var group = (mbid is not null && byMbid.TryGetValue(mbid, out var found) ? found : null)
                        ?? (isrc is not null && byIsrc.TryGetValue(isrc, out var foundByIsrc) ? foundByIsrc : null)
                        ?? (byText.TryGetValue(text, out var foundByText) ? foundByText : null);

                    if (group is null)
                    {
                        group = new Group(candidate);
                        groups.Add(group);
                    }
                    else
                    {
                        group.Absorb(candidate);
                    }

                    if (mbid is not null)
                    {
                        byMbid.TryAdd(mbid, group);
                    }

                    if (isrc is not null)
                    {
                        byIsrc.TryAdd(isrc, group);
                    }

                    byText.TryAdd(text, group);

                    var score = run.Weight * (list.Count - index) / list.Count;
                    group.Credit(run.Name, score);
                }
            }
        }

        return [.. groups.Select(group => group.ToSuggestion()).OrderByDescending(suggestion => suggestion.Score)];
    }

    /// <summary>
    /// Drops what the library holds (by MBID, ISRC, Deezer id, or artist and title), the seeds and the
    /// dismissed, then keeps the best <paramref name="maxPerArtist"/> of each artist. The input is already
    /// best first, and the output keeps that order.
    /// </summary>
    /// <param name="merged">The merged suggestions, best first.</param>
    /// <param name="library">Every song in the library.</param>
    /// <param name="seeds">The seeds of the request.</param>
    /// <param name="dismissals">What the user turned down.</param>
    /// <param name="maxPerArtist">How many suggestions one artist may keep.</param>
    public static List<DiscoverSuggestion> Filter(
        IReadOnlyList<DiscoverSuggestion> merged,
        IReadOnlyList<LibraryRow> library,
        IReadOnlyList<RecommendationSeed> seeds,
        IReadOnlyList<DiscoverDismissal> dismissals,
        int maxPerArtist)
    {
        var ownedMbids = new HashSet<string>(StringComparer.Ordinal);
        var ownedIsrcs = new HashSet<string>(StringComparer.Ordinal);
        var ownedDeezer = new HashSet<long>();
        var ownedText = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in library)
        {
            AddIfPresent(ownedMbids, NormalizeMbid(row.MbRecordingId));

            if (row.DeezerId is { } deezerId)
            {
                ownedDeezer.Add(deezerId);
            }

            foreach (var isrc in row.Isrcs)
            {
                AddIfPresent(ownedIsrcs, NormalizeIsrc(isrc));
            }

            ownedText.Add(TextKey(row.ArtistName, row.Title));
            ownedText.Add(TextKey(row.ArtistCredit, row.Title));
        }

        foreach (var seed in seeds)
        {
            AddIfPresent(ownedMbids, NormalizeMbid(seed.MbRecordingId));

            if (seed.DeezerId is { } deezerId)
            {
                ownedDeezer.Add(deezerId);
            }

            ownedText.Add(TextKey(seed.Artist, seed.Title));
        }

        var dismissedMbids = new HashSet<string>(StringComparer.Ordinal);
        var dismissedText = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dismissal in dismissals)
        {
            AddIfPresent(dismissedMbids, NormalizeMbid(dismissal.MbRecordingId));
            dismissedText.Add(dismissal.ArtistKey + "|" + dismissal.TitleKey);
        }

        var perArtist = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new List<DiscoverSuggestion>();

        foreach (var suggestion in merged)
        {
            var mbid = NormalizeMbid(suggestion.MbRecordingId);
            var isrc = NormalizeIsrc(suggestion.Isrc);
            var text = TextKey(suggestion.Artist, suggestion.Title);

            var owned = (mbid is not null && ownedMbids.Contains(mbid))
                || (isrc is not null && ownedIsrcs.Contains(isrc))
                || (suggestion.DeezerId is { } deezerId && ownedDeezer.Contains(deezerId))
                || ownedText.Contains(text);

            var dismissed = (mbid is not null && dismissedMbids.Contains(mbid)) || dismissedText.Contains(text);

            if (owned || dismissed)
            {
                continue;
            }

            var artistKey = TextMatching.NormalizeArtist(suggestion.Artist);
            perArtist.TryGetValue(artistKey, out var count);

            if (count >= maxPerArtist)
            {
                continue;
            }

            perArtist[artistKey] = count + 1;
            kept.Add(suggestion);
        }

        return kept;
    }

    private static void AddIfPresent(HashSet<string> set, string? value)
    {
        if (value is not null)
        {
            set.Add(value);
        }
    }

    private static string TextKey(string artist, string title) =>
        TextMatching.NormalizeArtist(artist) + "|" + TextMatching.Normalize(title);

    private static string? NormalizeMbid(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? NormalizeIsrc(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    /// <summary>One suggestion being assembled from the candidates that agree on what it is.</summary>
    private sealed class Group
    {
        private readonly Dictionary<string, double> _scores = new(StringComparer.Ordinal);
        private readonly List<string> _sources = [];

        private string? _album;
        private string? _coverUrl;
        private string? _mbid;
        private string? _isrc;
        private long? _deezerId;
        private int? _durationMs;

        public Group(RecommendationCandidate first)
        {
            Title = first.Title.Trim();
            Artist = first.Artist.Trim();
            Absorb(first);
        }

        public string Title { get; }

        public string Artist { get; }

        /// <summary>Fills what this suggestion does not know yet from another candidate for the same song.</summary>
        public void Absorb(RecommendationCandidate other)
        {
            _album ??= NullIfEmpty(other.Album);
            _coverUrl ??= NullIfEmpty(other.CoverUrl);
            _mbid ??= NormalizeMbid(other.MbRecordingId);
            _isrc ??= NormalizeIsrc(other.Isrc);
            _deezerId ??= other.DeezerId;
            _durationMs ??= other.DurationMs;
        }

        public void Credit(string source, double score)
        {
            if (!_scores.TryGetValue(source, out var best))
            {
                _sources.Add(source);
                _scores[source] = score;

                return;
            }

            if (score > best)
            {
                _scores[source] = score;
            }
        }

        public DiscoverSuggestion ToSuggestion() => new(
            Title,
            Artist,
            _album,
            _coverUrl,
            _mbid,
            _isrc,
            _deezerId,
            _durationMs,
            _sources,
            _scores.Values.Sum());

        private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
