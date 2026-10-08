using System.Text.RegularExpressions;
using System.Xml;

namespace Wondarr.Sources.Torznab.Parsing;

/// <summary>One <c>&lt;file&gt;</c> entry of an NZB.</summary>
/// <param name="Index">The 0-based position of the file in the NZB.</param>
/// <param name="Subject">The raw <c>subject</c> attribute, when the entry carries one.</param>
/// <param name="FileName">The quoted name in the subject, else the yEnc name, else <c>null</c>.</param>
/// <param name="Size">The sum of the entry's <c>&lt;segment bytes&gt;</c> values.</param>
/// <param name="IsPar2">The file is a Par2 volume (<c>.par2</c>).</param>
/// <param name="IsArchive">The file is part of an archive set (<c>.rar</c>, <c>.r00</c>…, <c>.7z</c>, <c>.zip</c>, <c>.001</c>).</param>
/// <param name="IsAudio">The file is an audio file (flac, mp3, m4a, ogg, opus, wav, aiff, ape, wv).</param>
/// <param name="IsPlainName">The file has a name that is not 16+ hex/base64-like characters before the extension.</param>
public sealed record NzbFile(
    int Index,
    string? Subject,
    string? FileName,
    long Size,
    bool IsPar2,
    bool IsArchive,
    bool IsAudio,
    bool IsPlainName);

/// <summary>
/// Reads the file list out of an NZB document (the <c>http://www.newzbin.com/DTD/2003/nzb</c>
/// format). Pure parsing: the only I/O is reading the given stream, and the DTD is never resolved.
/// </summary>
public static class NzbFileList
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    // "Artist - Album - 01.flac" as it appears quoted inside a subject.
    private static readonly Regex QuotedNameRegex = new(
        @"""(?<name>[^""]+)""",
        RegexOptions.Compiled,
        MatchTimeout);

    // The bare yEnc form: everything up to the token ending in an extension, right before "yEnc".
    private static readonly Regex YEncNameRegex = new(
        @"(?<name>(?:\S+[ ])*\S+[.][A-Za-z0-9]{2,4})[ ]+yEnc",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        MatchTimeout);

    // A stem of 16+ hex or base64-like characters is an obfuscated name, not a plain one.
    private static readonly Regex ObfuscatedStemRegex = new(
        @"^(?:[0-9a-fA-F]{16,}|[A-Za-z0-9+/]{16,}={0,2})$",
        RegexOptions.Compiled,
        MatchTimeout);

    private static readonly Regex ArchiveExtensionRegex = new(@"^(?:rar|7z|zip|r\d{2}|\d{3})$", RegexOptions.Compiled, MatchTimeout);

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "mp3", "m4a", "ogg", "opus", "wav", "aiff", "ape", "wv",
    };

    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "nfo", "sfv", "jpg", "png", "cue", "log", "m3u", "txt",
    };

    /// <summary>
    /// Parses the <c>&lt;file&gt;</c> entries of an NZB document, in document order.
    /// </summary>
    /// <param name="nzb">The NZB document to read (its DTD, if any, is ignored and never resolved).</param>
    /// <returns>The files the NZB describes, one entry per <c>&lt;file&gt;</c> element.</returns>
    public static IReadOnlyList<NzbFile> Parse(Stream nzb)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };

        var files = new List<NzbFile>();
        using (var reader = XmlReader.Create(nzb, settings))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "file")
                {
                    continue;
                }

                var subject = reader.GetAttribute("subject");
                var size = 0L;
                if (!reader.IsEmptyElement)
                {
                    var fileDepth = reader.Depth;
                    while (reader.Read() &&
                           !(reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "file" && reader.Depth == fileDepth))
                    {
                        if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "segment")
                        {
                            if (long.TryParse(reader.GetAttribute("bytes"), out var bytes))
                            {
                                size += bytes;
                            }
                        }
                    }
                }

                files.Add(CreateEntry(files.Count, subject, size));
            }
        }

        return files;
    }

    /// <summary>
    /// Tells whether every file of an NZB is audio, a Par2 volume or a small sidecar
    /// (.nfo, .sfv, .jpg, .png, .cue, .log, .m3u, .txt) with a plain name — DECISIONS #10.
    /// </summary>
    /// <param name="files">The files of one NZB, as <see cref="Parse"/> read them.</param>
    /// <returns><c>true</c> when nothing in the list is an archive or an obfuscated name.</returns>
    public static bool IsClean(IReadOnlyList<NzbFile> files)
    {
        foreach (var file in files)
        {
            if (!file.IsPlainName)
            {
                return false;
            }

            if (file.IsAudio || file.IsPar2)
            {
                continue;
            }

            var extension = GetExtension(file.FileName);
            if (extension is null || !SidecarExtensions.Contains(extension))
            {
                return false;
            }
        }

        return true;
    }

    private static NzbFile CreateEntry(int index, string? subject, long size)
    {
        var fileName = ExtractFileName(subject);
        var effective = fileName ?? subject ?? string.Empty;
        var extension = GetExtension(effective);

        var isPar2 = extension == "par2" || (fileName is null && subject is not null && subject.Contains(".par2", StringComparison.OrdinalIgnoreCase));
        var isArchive = extension is not null && ArchiveExtensionRegex.IsMatch(extension);
        var isAudio = extension is not null && AudioExtensions.Contains(extension);
        var isPlainName = fileName is not null && !IsObfuscatedName(fileName);

        return new NzbFile(index, subject, fileName, size, isPar2, isArchive, isAudio, isPlainName);
    }

    private static string? ExtractFileName(string? subject)
    {
        if (string.IsNullOrEmpty(subject))
        {
            return null;
        }

        var quoted = QuotedNameRegex.Match(subject);
        if (quoted.Success)
        {
            return quoted.Groups["name"].Value;
        }

        var yEnc = YEncNameRegex.Match(subject);
        if (yEnc.Success)
        {
            return yEnc.Groups["name"].Value;
        }

        return null;
    }

    private static string? GetExtension(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        var lastDot = fileName.LastIndexOf('.');
        if (lastDot < 0 || lastDot == fileName.Length - 1)
        {
            return null;
        }

        var extension = fileName[(lastDot + 1)..];
        return extension.Length is >= 2 and <= 5 && !extension.Contains(' ') ? extension : null;
    }

    private static bool IsObfuscatedName(string fileName)
    {
        var lastDot = fileName.LastIndexOf('.');
        var stem = lastDot < 0 ? fileName : fileName[..lastDot];
        return stem.Length >= 16 && ObfuscatedStemRegex.IsMatch(stem);
    }
}
