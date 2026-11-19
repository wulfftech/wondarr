using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Wondarr.Core.MusicVideos;

/// <summary>What a Kodi/Jellyfin <c>&lt;musicvideo&gt;</c> <c>.nfo</c> says about the video beside it.</summary>
/// <param name="Title">The <c>title</c> element, or <see langword="null"/>.</param>
/// <param name="Artist">The <c>artist</c> elements, joined by <c>" &amp; "</c>, or <see langword="null"/>.</param>
public sealed record MusicVideoNfoData(string? Title, string? Artist);

/// <summary>
/// Reads the <c>.nfo</c> beside a video. The file is the user's own and may be anything: it is read with the DTD off,
/// no resolver and a size cap, and whatever is not a <c>musicvideo</c> document is ignored.
/// </summary>
public static class MusicVideoNfo
{
    /// <summary>The largest <c>.nfo</c> that is read, in bytes. A real one is a few hundred bytes.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>Reads an <c>.nfo</c> that is already in memory.</summary>
    /// <param name="bytes">The file's bytes, at most <see cref="MaxBytes"/>.</param>
    /// <returns>What it says, or <see langword="null"/> when it is not a readable <c>musicvideo</c> document.</returns>
    public static MusicVideoNfoData? Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length > MaxBytes)
        {
            return null;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root;

            if (root is null || !string.Equals(root.Name.LocalName, "musicvideo", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var title = root.Elements()
                .Where(element => string.Equals(element.Name.LocalName, "title", StringComparison.OrdinalIgnoreCase))
                .Select(element => Clean(element.Value))
                .FirstOrDefault(text => text is not null);

            var artists = root.Elements()
                .Where(element => string.Equals(element.Name.LocalName, "artist", StringComparison.OrdinalIgnoreCase))
                .Select(element => Clean(element.Value))
                .OfType<string>()
                .ToList();

            return new MusicVideoNfoData(title, artists.Count == 0 ? null : string.Join(" & ", artists));
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Reads the <c>.nfo</c> at <paramref name="path"/>; call it through the bounded disk call.</summary>
    /// <param name="path">The <c>.nfo</c> file, which may not exist.</param>
    /// <returns>What it says, or <see langword="null"/> when there is none or it cannot be used.</returns>
    public static MusicVideoNfoData? ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var info = new FileInfo(path);

            if (!info.Exists || info.Length > MaxBytes)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[MaxBytes + 1];
            var read = 0;
            int count;

            while (read < buffer.Length && (count = stream.Read(buffer, read, buffer.Length - read)) > 0)
            {
                read += count;
            }

            return read > MaxBytes ? null : Parse(buffer[..read]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>An artist and a title read from a video's file name.</summary>
/// <param name="Artist">The artist as written, or <see langword="null"/> when the form names none.</param>
/// <param name="Title">The title as written, decorations (<c>(Official Video)</c>) still on it.</param>
/// <param name="FromTemplate">Whether the configured naming template read it, rather than a common form.</param>
public sealed record MusicVideoParsedName(string? Artist, string Title, bool FromTemplate);

/// <summary>
/// Reads an artist and a title out of a video's path under the video root: first by the configured naming template turned
/// into a pattern, then by the common forms <c>Artist - Title</c>, <c>Artist – Title</c> and
/// <c>Artist - Title (Official Video)</c>.
/// </summary>
public static partial class MusicVideoNameParser
{
    /// <summary>A dash, an en dash or an em dash with a space on both sides, which separates an artist from a title.</summary>
    [GeneratedRegex(@"\s[-–—]\s", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DashRegex();

    [GeneratedRegex(@"\{(Artist|AlbumArtist|Title|Album|Year)\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TokenRegex();

    /// <summary>Every artist and title the path can be read as, the template's reading first.</summary>
    /// <param name="relativePath">The path under the video root, <c>/</c>-separated, with its extension.</param>
    /// <param name="template">The configured naming template, for example <c>{Artist}/{Artist} - {Title}</c>.</param>
    public static IReadOnlyList<MusicVideoParsedName> Parse(string relativePath, string? template)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return [];
        }

        segments[^1] = Path.GetFileNameWithoutExtension(segments[^1]);

        var names = new List<MusicVideoParsedName>();

        if (!string.IsNullOrWhiteSpace(template) && ByTemplate(segments, template) is { } fromTemplate)
        {
            names.Add(fromTemplate);
        }

        var parts = DashRegex().Split(segments[^1]);

        // "A - B - C" can be (A, "B - C") or ("A - B", C): both are offered, and the library decides which one is real.
        for (var split = 1; split < parts.Length; split++)
        {
            var artist = string.Join(" - ", parts.Take(split)).Trim();
            var title = string.Join(" - ", parts.Skip(split)).Trim();

            if (artist.Length > 0 && title.Length > 0)
            {
                names.Add(new MusicVideoParsedName(artist, title, FromTemplate: false));
            }
        }

        return names;
    }

    /// <summary>The template's reading of the last segments of the path, or <see langword="null"/> when it does not fit.</summary>
    private static MusicVideoParsedName? ByTemplate(string[] segments, string template)
    {
        var templateSegments = template.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (templateSegments.Length == 0 || segments.Length < templateSegments.Length)
        {
            return null;
        }

        var pattern = new StringBuilder("^");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;

        // The template's slashes join its segments; every other character is literal, escaped. A token that comes up
        // again must read the same text as its first appearance, which is how "{Artist}/{Artist} - {Title}" ties the
        // folder to the file name.
        var joined = string.Join('/', templateSegments);

        foreach (var match in TokenRegex().Matches(joined).Cast<Match>())
        {
            pattern.Append(Regex.Escape(joined[position..match.Index]));
            var name = match.Groups[1].Value;
            var group = name.ToLowerInvariant();

            if (!seen.Add(name))
            {
                pattern.Append(@"\k<").Append(group).Append('>');
            }
            else if (name == "Year")
            {
                pattern.Append(@"(?<year>\d{4})");
            }
            else
            {
                pattern.Append("(?<").Append(group).Append(">.+?)");
            }

            position = match.Index + match.Length;
        }

        pattern.Append(Regex.Escape(joined[position..])).Append('$');

        var tail = string.Join('/', segments.Skip(segments.Length - templateSegments.Length));

        try
        {
            var found = Regex.Match(
                tail,
                pattern.ToString(),
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));

            if (!found.Success || !found.Groups["title"].Success)
            {
                return null;
            }

            var artist = found.Groups["artist"].Success
                ? found.Groups["artist"].Value
                : found.Groups["albumartist"].Success ? found.Groups["albumartist"].Value : null;
            var title = found.Groups["title"].Value.Trim();

            return title.Length == 0 ? null : new MusicVideoParsedName(artist?.Trim(), title, FromTemplate: true);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
