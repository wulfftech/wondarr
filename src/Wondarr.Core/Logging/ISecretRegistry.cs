using System.Net;

namespace Wondarr.Core.Logging;

/// <summary>
/// Holds the secrets the process knows about (API key, Soulseek password, Plex token, …) so the
/// logging pipeline can replace them wherever they appear.
/// </summary>
public interface ISecretRegistry
{
    /// <summary>
    /// Registers a secret. Null, empty and values shorter than six characters are ignored, because
    /// replacing them would mangle unrelated text. The secret is replaced as it is and in the forms
    /// a server echoes it back in: percent-encoded (upper- and lower-case hex, <c>%20</c> and
    /// <c>+</c> for a space), HTML-encoded, and as the JSON log writes each of those.
    /// </summary>
    void Register(string? secret);

    /// <summary>
    /// Replaces every registered secret with <c>(removed)</c> and then applies
    /// <see cref="CleanseLogMessage"/> for the secrets Wondarr has not been told about.
    /// </summary>
    string Redact(string text);
}

/// <summary>
/// Thread-safe <see cref="ISecretRegistry"/>. Secrets are registered at startup and whenever the
/// bound options change, and are read on every log event.
/// </summary>
public sealed class SecretRegistry : ISecretRegistry
{
    /// <summary>Shorter values are too likely to occur inside unrelated text to be redacted.</summary>
    public const int MinimumSecretLength = 6;

    private const string Removed = "(removed)";

    private readonly Lock _gate = new();
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);

    // Longest first, so a registered secret is never eaten by a shorter one that contains it.
    private volatile string[] _snapshot = [];

    public void Register(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < MinimumSecretLength)
        {
            return;
        }

        // An indexer that refuses a request often echoes its URL or query back, percent-encoded, or
        // in an HTML page, so a key with a reserved character (+ / = & and the like) comes back in
        // one of those forms. A form equal to one already registered adds nothing.
        var percentEncoded = Uri.EscapeDataString(secret);
        var urlEncoded = WebUtility.UrlEncode(secret);

        lock (_gate)
        {
            var added = AddWithJsonForm(secret);
            added |= AddWithJsonForm(percentEncoded);
            added |= AddWithJsonForm(LowerCasePercentHex(percentEncoded));
            added |= AddWithJsonForm(urlEncoded);
            added |= AddWithJsonForm(LowerCasePercentHex(urlEncoded));
            added |= AddWithJsonForm(WebUtility.HtmlEncode(secret));

            if (added)
            {
                _snapshot = [.. _secrets.OrderByDescending(value => value.Length)];
            }
        }
    }

    // The JSON log writes the text already escaped, where a quote or a backslash in the secret no
    // longer matches its form as it was, so the escaped form is registered with every form.
    private bool AddWithJsonForm(string form)
    {
        var added = _secrets.Add(form);
        var escaped = JsonEscape(form);

        if (escaped != form)
        {
            added |= _secrets.Add(escaped);
        }

        return added;
    }

    /// <summary>
    /// The percent-encoded text with the hex digits of every <c>%XX</c> in lower case, as some servers
    /// write them (<c>%2b</c> for <c>%2B</c>). Text without a <c>%</c> comes back as it is.
    /// </summary>
    private static string LowerCasePercentHex(string encoded)
    {
        var first = encoded.IndexOf('%', StringComparison.Ordinal);

        if (first < 0)
        {
            return encoded;
        }

        var characters = encoded.ToCharArray();

        // A literal % is always written %25, so every % starts an escape of two hex digits.
        for (var index = first; index < characters.Length - 2; index++)
        {
            if (characters[index] == '%')
            {
                characters[index + 1] = char.ToLowerInvariant(characters[index + 1]);
                characters[index + 2] = char.ToLowerInvariant(characters[index + 2]);
                index += 2;
            }
        }

        return new string(characters);
    }

    /// <summary>
    /// The secret as Serilog's JSON formatter writes it: <c>"</c> and <c>\</c> backslash-escaped and
    /// control characters as Serilog's JsonValueFormatter escapes them (\n, \r, \f, \t, else \u plus four uppercase hex digits). Everything else is written as it is.
    /// </summary>
    private static string JsonEscape(string secret)
    {
        var builder = new System.Text.StringBuilder(secret.Length + 8);

        foreach (var character in secret)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case < ' ':
                    builder.Append("\\u").Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var snapshot = _snapshot;

        foreach (var secret in snapshot)
        {
            text = text.Replace(secret, Removed, StringComparison.Ordinal);
        }

        return CleanseLogMessage.Cleanse(text);
    }
}
