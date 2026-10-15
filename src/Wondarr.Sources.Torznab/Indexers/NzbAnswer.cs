using System.Text;
using System.Xml;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// Tells an NZB from what an indexer hands out instead of one. An indexer whose daily quota is spent
/// still answers an NZB link with HTTP 200: a plain sentence (DOGnzb: "You already reached your daily
/// restriction of 10 downloads."), or a Newznab <c>&lt;error code="…" description="…"/&gt;</c> document.
/// Neither is an NZB, and neither is ever parsed as one.
/// </summary>
public static class NzbAnswer
{
    /// <summary>The most characters of an indexer's own words that are kept.</summary>
    public const int MaxTextLength = 200;

    // Enough for a sentence; the text is cut to MaxTextLength after the secrets are removed, which a
    // key split by this limit could never reach.
    private const int MaxBytesRead = 1024;

    private const string Fallback = "The indexer did not answer with an NZB.";

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Whether the bytes are an NZB document rather than what an indexer says instead of one.</summary>
    /// <param name="content">What a download returned.</param>
    public static bool IsNzb(byte[] content) => Refusal(content, static text => text) is null;

    /// <summary>
    /// What the indexer said instead of an NZB: its text, redacted, on one line and cut to
    /// <see cref="MaxTextLength"/> characters. <see langword="null"/> when the bytes are an NZB: they
    /// start (after a BOM and whitespace) with <c>&lt;?xml</c> or <c>&lt;nzb</c> and are not an
    /// <c>&lt;error&gt;</c> document.
    /// </summary>
    /// <param name="content">What a download returned.</param>
    /// <param name="redact">Removes the secrets Wondarr knows from a text before it is kept.</param>
    public static string? Refusal(byte[] content, Func<string, string> redact)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(redact);

        var body = content.AsSpan();

        if (body.StartsWith(Utf8Bom))
        {
            body = body[Utf8Bom.Length..];
        }

        body = body.TrimStart(" \t\r\n"u8);

        var looksLikeNzb = body.StartsWith("<?xml"u8) || body.StartsWith("<nzb"u8);

        // A bare <error/> without the XML declaration is also an error document, not just text.
        if ((looksLikeNzb || body.StartsWith("<error"u8)) && TryReadError(content, content.Length - body.Length, out var error))
        {
            return Clean(error, redact);
        }

        if (looksLikeNzb)
        {
            return null;
        }

        return Clean(Encoding.UTF8.GetString(body[..Math.Min(body.Length, MaxBytesRead)]), redact);
    }

    // Only the root element is read: a real NZB is never walked here, and the DTD is never resolved.
    // Reading starts at the first non-blank byte, because an XML declaration after whitespace is an error.
    private static bool TryReadError(byte[] content, int start, out string error)
    {
        error = string.Empty;

        try
        {
            using var stream = new MemoryStream(content, start, content.Length - start, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });

            if (reader.MoveToContent() != XmlNodeType.Element || !string.Equals(reader.LocalName, "error", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var description = reader.GetAttribute("description");
            var code = reader.GetAttribute("code");

            error = !string.IsNullOrWhiteSpace(description) ? description
                : !string.IsNullOrWhiteSpace(code) ? $"The indexer answered error {code}."
                : string.Empty;

            return true;
        }
        catch (XmlException)
        {
            // Not well-formed up to its first element: left to the NZB parser, as before.
            return false;
        }
    }

    /// <summary>
    /// An indexer's own words as the user is shown them: redacted, on one line, cut to
    /// <see cref="MaxTextLength"/> characters. Redacted first and cut last, because cutting first
    /// could leave half of a secret the registry no longer matches.
    /// </summary>
    /// <param name="text">What the indexer said.</param>
    /// <param name="redact">Removes the secrets Wondarr knows from a text.</param>
    public static string Clean(string text, Func<string, string> redact)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(redact);

        var builder = new StringBuilder(text.Length);

        foreach (var character in redact(text))
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(character);
            }
        }

        var cleaned = builder.ToString().TrimEnd();

        if (cleaned.Length > MaxTextLength)
        {
            // Never split a surrogate pair.
            var length = char.IsHighSurrogate(cleaned[MaxTextLength - 1]) ? MaxTextLength - 1 : MaxTextLength;
            cleaned = cleaned[..length].TrimEnd();
        }

        return cleaned.Length == 0 ? Fallback : cleaned;
    }
}
