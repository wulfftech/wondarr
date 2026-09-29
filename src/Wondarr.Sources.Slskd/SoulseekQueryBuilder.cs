using System.Text;
using System.Text.RegularExpressions;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// Turns a wanted song into the ordered, budget-aware sequence of search texts the Soulseek source
/// submits (MATCHING_ENGINE.md §6.4, adapted).
/// </summary>
/// <remarks>
/// <para>
/// Four queries, each submitted only when it says something the earlier ones did not: the artist and
/// the base title (plus a word per hard version flag), the same text with the diacritics folded, the
/// base title alone when it is long enough to mean something, and the artist with the album title —
/// Soulseek matches words against the whole remote path, so an album query returns the album folder's
/// files, the wanted track among them.
/// </para>
/// <para>
/// There is deliberately no <c>title artist</c> query and no separate <c>feat.</c>-stripped query:
/// Soulseek matches words in any order, and the base title and the main artist already have their
/// <c>feat.</c> clauses removed, so both would repeat the first query word for word.
/// </para>
/// </remarks>
public static class SoulseekQueryBuilder
{
    /// <summary>The version flags that add a word to the query, in the order the words are appended.</summary>
    private static readonly VersionFlags[] QueryFlags =
    [
        VersionFlags.Live,
        VersionFlags.Acoustic,
        VersionFlags.Remix,
        VersionFlags.Instrumental,
        VersionFlags.Demo,
    ];

    /// <summary>Characters that mean syntax to Soulseek rather than part of a word.</summary>
    private static readonly HashSet<char> SpecialCharacters =
    [
        '(', ')', '[', ']', '{', '}', '"', '\'', '!', '?', '.', ',', ':', ';', '&', '/', '\\', '|', '*', '#', '+', '=', '~', '<', '>',
    ];

    /// <summary>Whitespace, collapsed once the special characters have become spaces.</summary>
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>A <c>feat.</c>/<c>ft.</c>/<c>featuring</c> clause trailing an artist credit.</summary>
    private static readonly Regex FeatClauseRegex = new(
        @"\s+(?:feat\.?|ft\.?|featuring)\s+.*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The pseudo-album a song with no real album context is assigned to.</summary>
    private const string SinglesAlbum = "Singles";

    /// <summary>A base title shorter than this is not searched on its own: "One" or "Home" floods the network.</summary>
    private const int MinimumTitleOnlyLength = 8;

    /// <summary>
    /// The search texts to submit, in order, de-duplicated case-insensitively.
    /// </summary>
    /// <param name="request">The wanted song.</param>
    public static IReadOnlyList<string> Build(SongSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var artist = MainArtist(request);
        var flags = FlagWords(request.VersionFlags);
        var baseTitle = VersionFlagParser.Parse(request.Title).BaseTitle;

        var texts = new List<string>();

        // 1. artist + base title (+ one word per version flag), and 2. the same with the diacritics folded.
        var primary = Combine(artist, baseTitle, flags);
        Add(texts, Sanitise(primary));
        Add(texts, Sanitise(TextFolding.RemoveDiacritics(primary)));

        // 3. the base title alone, when it is long enough to be worth a search on its own.
        if (TextMatching.Normalize(baseTitle).Length >= MinimumTitleOnlyLength)
        {
            Add(texts, Sanitise(Combine(baseTitle, flags)));
        }

        // 4. artist + album: Soulseek matches the whole path, so this finds the album folder holding the track.
        var album = request.AlbumTitle;
        if (!string.IsNullOrWhiteSpace(album) &&
            !string.Equals(album.Trim(), SinglesAlbum, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(album.Trim(), baseTitle, StringComparison.OrdinalIgnoreCase))
        {
            Add(texts, Sanitise(Combine(artist, album)));
        }

        return texts;
    }

    /// <summary>
    /// The main artist: the first credited main artist, or the credit line with its <c>feat.</c>
    /// clause removed when there is none.
    /// </summary>
    private static string MainArtist(SongSearchRequest request)
    {
        if (request.MainArtists.Count > 0 && !string.IsNullOrWhiteSpace(request.MainArtists[0]))
        {
            return request.MainArtists[0];
        }

        return FeatClauseRegex.Replace(request.ArtistCredit, string.Empty);
    }

    /// <summary>The wire names of the version flags that earn a word, in order.</summary>
    private static string FlagWords(VersionFlags flags)
    {
        var words = new List<string>();

        foreach (var flag in QueryFlags)
        {
            if (flags.HasFlag(flag))
            {
                words.Add(VersionFlagNames.ToWireName(flag));
            }
        }

        return string.Join(' ', words);
    }

    /// <summary>Joins the non-empty parts of a query with single spaces.</summary>
    private static string Combine(params string?[] parts)
    {
        var kept = new List<string>();

        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                kept.Add(part.Trim());
            }
        }

        return string.Join(' ', kept);
    }

    /// <summary>
    /// Rewrites one query the way Soulseek wants it: syntax characters become spaces, a token that
    /// starts with <c>-</c> loses it (a leading <c>-</c> excludes the word), whitespace is collapsed
    /// and everything is lower-cased.
    /// </summary>
    private static string Sanitise(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            builder.Append(SpecialCharacters.Contains(character) ? ' ' : character);
        }

        var kept = new List<string>();

        foreach (var token in WhitespaceRegex.Split(builder.ToString()))
        {
            if (token.Length == 0)
            {
                continue;
            }

            var trimmed = token.StartsWith('-') ? token.TrimStart('-') : token;
            if (trimmed.Length > 0)
            {
                kept.Add(trimmed.ToLowerInvariant());
            }
        }

        return string.Join(' ', kept);
    }

    /// <summary>Appends a query, unless it is empty or already in the list in any casing.</summary>
    private static void Add(List<string> texts, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        foreach (var existing in texts)
        {
            if (string.Equals(existing, text, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        texts.Add(text);
    }
}
