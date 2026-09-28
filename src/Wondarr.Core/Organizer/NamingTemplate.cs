using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;

namespace Wondarr.Core.Organizer;

/// <summary>The outcome of validating a naming template: whether it renders, and why not when it does not.</summary>
/// <param name="IsValid">True when the template can be rendered.</param>
/// <param name="Errors">One message per problem, README-able and safe to show in the UI.</param>
public sealed record NamingValidationResult(bool IsValid, IReadOnlyList<string> Errors);

/// <summary>
/// Renders a library's Lidarr-style <c>{Token}</c> naming template into the relative path of an imported
/// file (LIBRARY_OUTPUT.md §7.1). Pure and deterministic: no file system, no clock, no settings lookup.
/// </summary>
/// <remarks>
/// The rendered path uses <c>/</c> separators and carries no extension — the caller appends it. Illegal
/// characters are replaced inside each token value, so a <c>/</c> in a title never creates a folder.
/// </remarks>
public static class NamingTemplate
{
    /// <summary>The longest a single path segment may be, in UTF-8 bytes (the file system's limit).</summary>
    private const int MaxSegmentBytes = 255;

    private static readonly char[] SegmentSeparators = ['/', '\\'];

    private static readonly Regex AbsoluteTemplateRegex = new(@"^\s*[\\/]", RegexOptions.Compiled);

    private static readonly Regex DriveLetterRegex = new(@"(^|[\\/])[A-Za-z]:", RegexOptions.Compiled);

    /// <summary>An empty <c>()</c> or <c>[]</c> pair, together with the space in front of it.</summary>
    private static readonly Regex EmptyBracketPairRegex = new(@"\s*\(\s*\)|\s*\[\s*\]", RegexOptions.Compiled);

    private static readonly Regex CollapseSpacesRegex = new(@" {2,}", RegexOptions.Compiled);

    /// <summary>The default template of every layout preset; a library may override any of them.</summary>
    public static IReadOnlyDictionary<LibraryLayout, string> PresetTemplates { get; } =
        new Dictionary<LibraryLayout, string>
        {
            [LibraryLayout.Flat] = "{Artist Name} - {Track Title}",
            [LibraryLayout.Artist] = "{Artist Name}/{Artist Name} - {Track Title}",
            [LibraryLayout.ArtistAlbum] = "{Artist Name}/{Album Title} ({Release Year})/{track:00} - {Track Title}",
            [LibraryLayout.Plexamp] = "{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}",
        }.ToFrozenDictionary();

    /// <summary>The token names the engine knows, as they read in a template (lower-cased for lookup).</summary>
    private static readonly FrozenSet<string> ValueTokens = new[]
    {
        "artist name", "artist cleanname", "artist namethe", "artist namefirstcharacter", "artist mbid",
        "album artist name", "album title", "album cleantitle", "album type", "album mbid",
        "release year", "original year",
        "track title", "track cleantitle", "track artistname", "recording mbid", "release mbid", "isrc",
        "quality full", "quality title",
        "mediainfo audiocodec", "mediainfo audiobitrate", "mediainfo audiosamplerate", "mediainfo audiobitspersample",
        "source", "version",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The tokens whose format is a zero-padding width rather than a truncation length.</summary>
    private static readonly FrozenSet<string> NumericTokens = new[] { "track", "medium" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The tokens that only describe the album folder; a file name built from them alone is unusable.</summary>
    private static readonly FrozenSet<string> AlbumContextTokens = new[]
    {
        "album artist name", "album title", "album cleantitle", "album type", "album mbid",
        "release year", "original year",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Checks a template without rendering it.</summary>
    /// <param name="template">The template as the user typed it.</param>
    /// <returns>Every problem found, in the order the renderer would hit them.</returns>
    public static NamingValidationResult Validate(string template)
    {
        var errors = new List<string>();
        _ = Parse(template, errors);

        return new NamingValidationResult(errors.Count == 0, errors);
    }

    /// <summary>Renders a template into the relative path of an imported file, without its extension.</summary>
    /// <param name="template">The template as the user typed it.</param>
    /// <param name="values">The song's naming values.</param>
    /// <param name="options">Folding and extension; the defaults are no folding and <c>mp3</c>.</param>
    /// <returns>A path with <c>/</c> separators, safe to join onto a library root.</returns>
    /// <exception cref="ArgumentException">The template is invalid; the message is the first validation error.</exception>
    public static string Render(string template, NamingValues values, NamingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(values);

        var errors = new List<string>();
        var nodes = Parse(template, errors);
        if (errors.Count > 0)
        {
            throw new ArgumentException(errors[0], nameof(template));
        }

        var effectiveOptions = options ?? new NamingOptions();
        var rendered = RenderNodes(nodes, values, effectiveOptions, out _);

        var segments = new List<string>();
        foreach (var segment in rendered.Split('/'))
        {
            var cleaned = CleanSegment(segment);
            if (cleaned.Length > 0)
            {
                segments.Add(cleaned);
            }
        }

        if (segments.Count == 0)
        {
            return string.Empty;
        }

        // The file name also has to hold the dot and the extension, so its cap is smaller than a folder's.
        var fileNameCap = MaxSegmentBytes - (1 + Encoding.UTF8.GetByteCount(effectiveOptions.Extension));
        for (var index = 0; index < segments.Count; index++)
        {
            var cap = index == segments.Count - 1 ? fileNameCap : MaxSegmentBytes;
            segments[index] = TrimSegment(CapToBytes(segments[index], Math.Max(cap, 1)));
        }

        return string.Join('/', segments);
    }

    private static List<Node> Parse(string template, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            errors.Add("Template is empty");
            return [];
        }

        if (AbsoluteTemplateRegex.IsMatch(template) || DriveLetterRegex.IsMatch(template))
        {
            errors.Add("Template must be relative");
        }

        var nodes = ParseNodes(template, 0, terminator: null, errors).Nodes;

        if (template.Split(SegmentSeparators).Any(segment => segment.Trim() is "." or ".."))
        {
            errors.Add("Template must not contain '..' or '.' segments");
        }

        if (!LastSegmentIdentifiesTheTrack(nodes))
        {
            errors.Add("The file name must contain {Track Title}");
        }

        return nodes;
    }

    /// <summary>Parses one nesting level; <paramref name="terminator"/> is the group's closing bracket, if any.</summary>
    private static (List<Node> Nodes, int Next) ParseNodes(string template, int index, char? terminator, List<string> errors)
    {
        var nodes = new List<Node>();
        var literal = new StringBuilder();

        void Flush()
        {
            if (literal.Length == 0)
            {
                return;
            }

            nodes.Add(new LiteralNode(literal.ToString()));
            literal.Clear();
        }

        while (index < template.Length)
        {
            var current = template[index];

            // Literal brackets are written doubled; this wins over everything else, including the terminator.
            if (current == '[' && index + 1 < template.Length && template[index + 1] == '[')
            {
                literal.Append('[');
                index += 2;
                continue;
            }

            if (current == ']' && index + 1 < template.Length && template[index + 1] == ']')
            {
                literal.Append(']');
                index += 2;
                continue;
            }

            if (terminator is { } closing && current == closing)
            {
                Flush();
                return (nodes, index + 1);
            }

            if (current == '[')
            {
                var (children, next) = ParseNodes(template, index + 1, ']', errors);
                Flush();
                nodes.Add(new GroupNode(children));
                index = next;
                continue;
            }

            if (current == '{')
            {
                var close = template.IndexOf('}', index + 1);
                var nested = template.IndexOf('{', index + 1);
                if (close < 0 || (nested >= 0 && nested < close))
                {
                    errors.Add("Unbalanced '{'");
                    index = template.Length;
                    break;
                }

                var raw = template[(index + 1)..close];
                Flush();

                var token = ParseToken(raw, errors);
                if (token is not null)
                {
                    nodes.Add(token);
                }

                index = close + 1;
                continue;
            }

            if (current == '}')
            {
                errors.Add("Unbalanced '}'");
                index++;
                continue;
            }

            literal.Append(current);
            index++;
        }

        Flush();
        return (nodes, index);
    }

    private static TokenNode? ParseToken(string raw, List<string> errors)
    {
        var colon = raw.IndexOf(':');
        var name = colon >= 0 ? raw[..colon] : raw;
        var format = colon >= 0 ? raw[(colon + 1)..] : string.Empty;

        var canonical = CanonicalName(name);
        if (!ValueTokens.Contains(canonical) && !NumericTokens.Contains(canonical))
        {
            errors.Add($"Unknown token {{{raw}}}");
            return null;
        }

        // "Artist.Name" renders "Daft.Punk": the first separator between the token's words is also used
        // to replace the spaces in the value.
        var separator = name.FirstOrDefault(character => character is '-' or '.' or '_');

        return new TokenNode(
            canonical,
            separator is '-' or '.' or '_' ? separator : null,
            CasingOf(raw),
            format);
    }

    /// <summary>Folds a token name to its lookup form: separators become spaces, then lower case.</summary>
    private static string CanonicalName(string name)
    {
        var spaced = name.Replace('-', ' ').Replace('.', ' ').Replace('_', ' ');

        return CollapseSpacesRegex.Replace(spaced, " ").Trim().ToLowerInvariant();
    }

    /// <summary>Lidarr's rule: an all-lower-case token lower-cases its value, an all-upper-case one upper-cases it.</summary>
    private static CasingMode CasingOf(string token)
    {
        if (token.All(character => !char.IsLetter(character) || char.IsLower(character)))
        {
            return CasingMode.Lower;
        }

        if (token.All(character => !char.IsLetter(character) || char.IsUpper(character)))
        {
            return CasingMode.Upper;
        }

        return CasingMode.AsIs;
    }

    private static string RenderNodes(IReadOnlyList<Node> nodes, NamingValues values, NamingOptions options, out bool anyEmpty)
    {
        var builder = new StringBuilder();
        anyEmpty = false;

        foreach (var node in nodes)
        {
            switch (node)
            {
                case LiteralNode literal:
                    builder.Append(literal.Text);
                    break;

                case TokenNode token:
                    builder.Append(RenderToken(token, values, options, out var tokenEmpty));
                    anyEmpty |= tokenEmpty;
                    break;

                case GroupNode group:
                    // An optional group renders without its brackets, or not at all when a token inside is empty.
                    var inner = RenderNodes(group.Children, values, options, out var groupEmpty);
                    if (!groupEmpty)
                    {
                        builder.Append(inner);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private static string RenderToken(TokenNode token, NamingValues values, NamingOptions options, out bool isEmpty)
    {
        var value = token.Canonical switch
        {
            "track" => values.TrackNo is { } trackNo ? Padded(trackNo, token.Format) : string.Empty,
            "medium" => values.DiscCount is > 1 && values.DiscNo is { } discNo ? Padded(discNo, token.Format) : string.Empty,
            _ => ValueOf(token.Canonical, values),
        };

        if (options.AsciiFold && value.Length > 0)
        {
            // Not string.Normalize: InvariantGlobalization turns that into a no-op for non-ASCII.
            value = TextFolding.RemoveDiacritics(value);
        }

        value = value.Trim();

        if (TruncationLength(token.Format) is { } length && value.Length > length)
        {
            value = value[..length].Trim();
        }

        isEmpty = value.Length == 0;
        if (isEmpty)
        {
            return string.Empty;
        }

        value = token.Casing switch
        {
            CasingMode.Lower => value.ToLowerInvariant(),
            CasingMode.Upper => value.ToUpperInvariant(),
            _ => value,
        };

        if (token.Separator is { } separator)
        {
            value = value.Replace(" ", separator.ToString(), StringComparison.Ordinal);
        }

        return NamingTokens.CleanFileName(value);
    }

    private static string ValueOf(string canonical, NamingValues values) => canonical switch
    {
        "artist name" => values.ArtistName,
        "artist cleanname" => NamingTokens.CleanTitle(values.ArtistName),
        "artist namethe" => NamingTokens.TitleThe(values.ArtistName),
        "artist namefirstcharacter" => NamingTokens.TitleFirstCharacter(NamingTokens.TitleThe(values.ArtistName)),
        "artist mbid" => values.ArtistMbId ?? string.Empty,
        "album artist name" => values.AlbumArtistName ?? string.Empty,
        "album title" => values.AlbumTitle ?? string.Empty,
        "album cleantitle" => NamingTokens.CleanTitle(values.AlbumTitle ?? string.Empty),
        "album type" => values.AlbumType ?? string.Empty,
        "album mbid" => values.AlbumMbId ?? string.Empty,
        "release year" => Year(values.ReleaseYear),
        "original year" => Year(values.OriginalYear),
        "track title" => values.TrackTitle,
        "track cleantitle" => NamingTokens.CleanTitle(values.TrackTitle),
        "track artistname" => values.TrackArtistName ?? string.Empty,
        "recording mbid" => values.RecordingMbId ?? string.Empty,
        "release mbid" => values.ReleaseMbId ?? string.Empty,
        "isrc" => values.Isrc ?? string.Empty,
        "quality full" => values.QualityFull ?? string.Empty,
        "quality title" => values.QualityTitle ?? string.Empty,
        "mediainfo audiocodec" => values.AudioCodec ?? string.Empty,
        "mediainfo audiobitrate" => values.AudioBitRate is { } bitRate
            ? bitRate.ToString(CultureInfo.InvariantCulture) + " kbps"
            : string.Empty,
        "mediainfo audiosamplerate" => values.AudioSampleRate is { } sampleRate
            ? (sampleRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz"
            : string.Empty,
        "mediainfo audiobitspersample" => values.AudioBitsPerSample is { } bitsPerSample
            ? bitsPerSample.ToString(CultureInfo.InvariantCulture) + "bit"
            : string.Empty,
        "source" => values.Source ?? string.Empty,
        "version" => values.Version ?? string.Empty,
        _ => string.Empty,
    };

    private static string Year(int? year) => year is { } value ? value.ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>A zero-pad pattern like <c>00</c> pads the number to that many digits; anything else leaves it alone.</summary>
    private static string Padded(int number, string format)
    {
        var pattern = format.Length > 0 && format.All(character => character == '0') ? format : "0";

        return number.ToString(pattern, CultureInfo.InvariantCulture);
    }

    private static int? TruncationLength(string format) =>
        int.TryParse(format, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) && length > 0 ? length : null;

    /// <summary>
    /// Whether the file name (the last segment) says anything about the track itself: any token that is
    /// not the album context counts, which keeps "{Artist MbId}/{Recording MbId} {ISRC}" and
    /// "{Artist NameFirstCharacter}/{Artist Name}" valid while "{Artist Name}/{Album Title}" is not.
    /// </summary>
    private static bool LastSegmentIdentifiesTheTrack(IReadOnlyList<Node> nodes)
    {
        var lastSeparator = -1;
        for (var index = 0; index < nodes.Count; index++)
        {
            if (nodes[index] is LiteralNode literal && literal.Text.Contains('/', StringComparison.Ordinal))
            {
                lastSeparator = index;
            }
        }

        for (var index = lastSeparator + 1; index < nodes.Count; index++)
        {
            if (ContainsTokenOutsideTheAlbumContext(nodes[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsTokenOutsideTheAlbumContext(Node node) => node switch
    {
        TokenNode token => !AlbumContextTokens.Contains(token.Canonical),
        GroupNode group => group.Children.Any(ContainsTokenOutsideTheAlbumContext),
        _ => false,
    };

    /// <summary>Tidies one rendered segment into something every file system accepts.</summary>
    private static string CleanSegment(string segment)
    {
        var result = EmptyBracketPairRegex.Replace(segment, string.Empty);
        result = CollapseSpacesRegex.Replace(result, " ").Trim();

        // A separator that lost its other side ("{Version} - {Track Title}" with no version).
        while (result.StartsWith("- ", StringComparison.Ordinal))
        {
            result = result[2..].TrimStart();
        }

        while (result.EndsWith(" -", StringComparison.Ordinal))
        {
            result = result[..^2].TrimEnd();
        }

        return TrimSegment(result);
    }

    private static string TrimSegment(string value) => value.Trim(' ').TrimStart('.').TrimEnd('.', ' ');

    /// <summary>
    /// Cuts a segment to <paramref name="maxBytes"/> UTF-8 bytes, only ever at a character boundary, so a
    /// truncated segment is never half a surrogate pair or half of a multi-byte character.
    /// </summary>
    private static string CapToBytes(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var used = 0;
        var length = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maxBytes)
            {
                break;
            }

            used += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }

        return value[..length];
    }

    private enum CasingMode
    {
        AsIs,
        Lower,
        Upper,
    }

    private abstract record Node;

    private sealed record LiteralNode(string Text) : Node;

    private sealed record TokenNode(string Canonical, char? Separator, CasingMode Casing, string Format) : Node;

    private sealed record GroupNode(IReadOnlyList<Node> Children) : Node;
}