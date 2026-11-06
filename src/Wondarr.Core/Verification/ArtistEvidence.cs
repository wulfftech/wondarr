using System.Text.RegularExpressions;
using Wondarr.Core.Identity;
using Wondarr.Core.Sources;
using Wondarr.Core.Tagging;

namespace Wondarr.Core.Verification;

/// <summary>What a downloaded file says about whose song it is, judged against the song's artists.</summary>
public enum ArtistEvidenceKind
{
    /// <summary>The file's artist tag, or its name, names one of the song's artists.</summary>
    Named,

    /// <summary>The file's artist tag, or its name, names someone else.</summary>
    Other,

    /// <summary>Neither the tags nor the name say who the artist is.</summary>
    Unknown,
}

/// <summary>The verdict of <see cref="ArtistEvidence.Judge"/>.</summary>
/// <param name="Kind">What the file says.</param>
/// <param name="Source">Where it says it: <c>artist tag</c>, <c>album-artist tag</c> or <c>file name</c>; null when unknown.</param>
/// <param name="Artist">The artist named there, as written; null when unknown.</param>
public sealed record ArtistEvidenceResult(ArtistEvidenceKind Kind, string? Source, string? Artist);

/// <summary>
/// Whether a file names the song's artist, for a file AcoustID cannot vouch for (MATCHING_ENGINE.md §6.5).
/// The file's own tags come first, then the artist field of its file name. A credit is split into the
/// names it lists (<c>Al Storm &amp; Orbit1 Feat Lisa Abbott</c> is three), and one of them has to be one
/// of the song's artists as a whole name: "Lisa" is not "Lisa Abbott", and "Club" is not "The Saddle Club".
/// </summary>
public static class ArtistEvidence
{
    /// <summary>The words that join or credit artists inside one tag or file-name field.</summary>
    private static readonly Regex CreditSeparatorRegex = new(
        @"\s*(?:[,;/&+×]|\s(?:feat|ft|featuring|vs|x|with|and|presents|pres)\.?(?=\s))\s*",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>The separators between the parts of a file name: a dash with spaces round it.</summary>
    private static readonly Regex NameSegmentRegex = new(
        @"\s+[-–—]\s+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>A track number in front of a part: <c>03 </c>, <c>03. </c>, <c>1-06 </c>.</summary>
    private static readonly Regex LeadingNumberRegex = new(
        @"^\s*\d{1,3}(?:[-.]\d{1,3})?[.)]?\s+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Tag values that name nobody, once normalised.</summary>
    private static readonly HashSet<string> NoArtist = new(StringComparer.Ordinal)
    {
        "various", "various artists", "va", "v a", "unknown", "unknown artist", "artist", "track", "none", "n a",
    };

    /// <summary>Judges <paramref name="tags"/>, then <paramref name="fileName"/>, against <paramref name="songArtists"/>.</summary>
    /// <param name="tags">The file's tags, or <see langword="null"/> when it carries none.</param>
    /// <param name="fileName">The downloaded file's name.</param>
    /// <param name="songArtists">The song's main artists.</param>
    public static ArtistEvidenceResult Judge(FileTags? tags, string fileName, IReadOnlyList<string> songArtists)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(songArtists);

        var wanted = songArtists.Select(Key).Where(key => key.Length > 0).ToHashSet(StringComparer.Ordinal);

        if (wanted.Count == 0)
        {
            return new ArtistEvidenceResult(ArtistEvidenceKind.Unknown, null, null);
        }

        // Either tag naming the artist is enough: a compilation's album artist is "Various Artists" and
        // its artist tag is the track's own, while a single's artist tag may carry the featured guests.
        var tagged = new[] { ("artist tag", tags?.Artist), ("album-artist tag", tags?.AlbumArtist) }
            .Where(field => IsArtist(field.Item2))
            .ToList();

        if (tagged.Any(field => NamesAny(field.Item2!, wanted)))
        {
            var named = tagged.First(field => NamesAny(field.Item2!, wanted));

            return new ArtistEvidenceResult(ArtistEvidenceKind.Named, named.Item1, named.Item2);
        }

        if (tagged.Count > 0)
        {
            return new ArtistEvidenceResult(ArtistEvidenceKind.Other, tagged[0].Item1, tagged[0].Item2);
        }

        // Any dash-separated part of the name may be the artist ("oz2004_069 - The Saddle Club - On Top Of
        // The World"), so the parts are looked at first and the parser's artist field only after them.
        var name = Path.GetFileName(fileName);
        var stem = Path.GetFileNameWithoutExtension(name);

        foreach (var segment in NameSegmentRegex.Split(stem))
        {
            var part = LeadingNumberRegex.Replace(segment, string.Empty);

            if (part.Length > 0 && NamesAny(part, wanted))
            {
                return new ArtistEvidenceResult(ArtistEvidenceKind.Named, "file name", part.Trim());
            }
        }

        var fromName = SoulseekFilenameParser.Parse(name).Parsed.Artist;

        if (!IsArtist(fromName))
        {
            return new ArtistEvidenceResult(ArtistEvidenceKind.Unknown, null, null);
        }

        return NamesAny(fromName!, wanted)
            ? new ArtistEvidenceResult(ArtistEvidenceKind.Named, "file name", fromName)
            : new ArtistEvidenceResult(ArtistEvidenceKind.Other, "file name", fromName);
    }

    /// <summary>Whether the credit, or one of the names it lists, is one of the wanted artists.</summary>
    private static bool NamesAny(string credit, HashSet<string> wanted) =>
        wanted.Contains(Key(credit))
        || CreditSeparatorRegex.Split(credit).Select(Key).Any(wanted.Contains);

    private static bool IsArtist(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Key(value) is { Length: > 0 } key
        && !NoArtist.Contains(key)
        && !key.All(character => char.IsAsciiDigit(character) || character == ' ');

    /// <summary>
    /// An artist name for comparing: normalised, without "the" at either end ("Saddle Club, The") and
    /// without "and", which <c>&amp;</c> becomes.
    /// </summary>
    private static string Key(string name)
    {
        var words = TextMatching.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        if (words.Count > 1 && words[0] == "the")
        {
            words.RemoveAt(0);
        }

        if (words.Count > 1 && words[^1] == "the")
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Join(' ', words.Where(word => word != "and"));
    }
}
