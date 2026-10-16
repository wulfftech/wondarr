using System.Globalization;
using System.Text;
using System.Xml;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>What an indexer's answer to an NZB link turned out to be.</summary>
public enum NzbAnswerKind
{
    /// <summary>
    /// An NZB document, or something that starts like one and is left to the NZB parser: the root
    /// element is <c>nzb</c>, or the XML is broken before its first element.
    /// </summary>
    Nzb,

    /// <summary>
    /// The indexer will not hand this account NZBs (quota spent, credentials, API switched off) or does
    /// not answer with a document at all: no more NZBs are asked of it in the search.
    /// </summary>
    AccountRefusal,

    /// <summary>The indexer cannot give this one release (removed, bad parameters); the others may be fine.</summary>
    ReleaseFailure,
}

/// <summary>A classified answer, with the indexer's own words when it is not an NZB.</summary>
/// <param name="Kind">What the answer is.</param>
/// <param name="Text">The indexer's words, redacted, on one line and cut short; empty for an NZB.</param>
public sealed record NzbVerdict(NzbAnswerKind Kind, string Text)
{
    /// <summary>Gets a value indicating whether the answer is an NZB.</summary>
    public bool IsNzb => Kind == NzbAnswerKind.Nzb;
}

/// <summary>
/// Tells an NZB from what an indexer hands out instead of one. An indexer whose daily quota is spent
/// still answers an NZB link with HTTP 200: a plain sentence (DOGnzb: "You already reached your daily
/// restriction of 10 downloads."), or a Newznab <c>&lt;error code="…" description="…"/&gt;</c> document.
/// Neither is an NZB, and neither is ever parsed as one; nor is any other XML whose root element is not
/// <c>nzb</c>, such as the web page an indexer's front end can answer with.
/// </summary>
public static class NzbAnswer
{
    /// <summary>The most characters of an indexer's own words that are kept.</summary>
    public const int MaxTextLength = 200;

    // Far more than the MaxTextLength characters that are shown, so a key that sits anywhere in a
    // refusal that quotes the request is whole when it is redacted; the text is cut to MaxTextLength
    // only after the redaction, which a key split by this limit could never reach.
    private const int MaxBytesRead = 8 * 1024;

    private const string Fallback = "The indexer did not answer with an NZB.";

    private const string WebPage = "The indexer answered with a web page, not an NZB.";

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Whether the bytes are an NZB document rather than what an indexer says instead of one.</summary>
    /// <param name="content">What a download returned.</param>
    public static bool IsNzb(byte[] content) => Check(content, static text => text).IsNzb;

    /// <summary>
    /// Classifies what a download returned. Markup is judged by its root element, found after any
    /// whitespace, comment or DOCTYPE, so an NZB without an XML declaration is still an NZB:
    /// <c>nzb</c> is an NZB; <c>error</c> is a Newznab error, which refuses the account only for the
    /// account-level codes (100 to 199 credentials and privileges, 500 to 599 request and download
    /// limits, 900 to 999 unknown error or API disabled) and otherwise fails just this release (200 to
    /// 299 parameters, 300 to 399 no such item); any other root is a web page. Anything else is the
    /// indexer's plain text, which refuses the account.
    /// </summary>
    /// <param name="content">What a download returned.</param>
    /// <param name="redact">Removes the secrets Wondarr knows from a text before it is kept.</param>
    public static NzbVerdict Check(byte[] content, Func<string, string> redact)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(redact);

        var body = content.AsSpan();

        if (body.StartsWith(Utf8Bom))
        {
            body = body[Utf8Bom.Length..];
        }

        body = body.TrimStart(" \t\r\n"u8);

        if (body.StartsWith("<"u8))
        {
            // Broken XML in front of its first element is left to the NZB parser, as it always was,
            // when the document starts like an NZB; any other markup is not one.
            var startsLikeNzb = body.StartsWith("<?xml"u8) || body.StartsWith("<nzb"u8);

            return ReadRoot(content, content.Length - body.Length, out var code, out var description) switch
            {
                Root.Nzb => new NzbVerdict(NzbAnswerKind.Nzb, string.Empty),
                Root.Error => new NzbVerdict(ErrorKind(code), Clean(ErrorText(code, description), redact)),
                Root.Other => new NzbVerdict(NzbAnswerKind.AccountRefusal, WebPage),
                _ => startsLikeNzb ? new NzbVerdict(NzbAnswerKind.Nzb, string.Empty) : new NzbVerdict(NzbAnswerKind.AccountRefusal, WebPage),
            };
        }

        return new NzbVerdict(
            NzbAnswerKind.AccountRefusal,
            Clean(Encoding.UTF8.GetString(body[..Math.Min(body.Length, MaxBytesRead)]), redact));
    }

    private enum Root
    {
        Unreadable,
        Nzb,
        Error,
        Other,
    }

    // Only the root element is read: a real NZB is never walked here, and the DTD is never resolved.
    // Reading starts at the first non-blank byte, because an XML declaration after whitespace is an error.
    private static Root ReadRoot(byte[] content, int start, out string? code, out string? description)
    {
        code = null;
        description = null;

        try
        {
            using var stream = new MemoryStream(content, start, content.Length - start, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });

            if (reader.MoveToContent() != XmlNodeType.Element)
            {
                return Root.Unreadable;
            }

            if (string.Equals(reader.LocalName, "nzb", StringComparison.OrdinalIgnoreCase))
            {
                return Root.Nzb;
            }

            if (!string.Equals(reader.LocalName, "error", StringComparison.OrdinalIgnoreCase))
            {
                return Root.Other;
            }

            code = reader.GetAttribute("code");
            description = reader.GetAttribute("description");

            return Root.Error;
        }
        catch (XmlException)
        {
            return Root.Unreadable;
        }
    }

    // Newznab's error codes: 1xx account (credentials, privileges), 2xx parameters, 3xx no such item,
    // 5xx limits, 9xx unknown error or API disabled. A missing or odd code does not cut an indexer off:
    // only the account-level ones say the indexer will not serve this account.
    private static NzbAnswerKind ErrorKind(string? code) =>
        int.TryParse(code?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is (>= 100 and <= 199) or (>= 500 and <= 599) or (>= 900 and <= 999)
            ? NzbAnswerKind.AccountRefusal
            : NzbAnswerKind.ReleaseFailure;

    private static string ErrorText(string? code, string? description) =>
        !string.IsNullOrWhiteSpace(description) ? description
            : !string.IsNullOrWhiteSpace(code) ? $"The indexer answered error {code}."
            : string.Empty;

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
