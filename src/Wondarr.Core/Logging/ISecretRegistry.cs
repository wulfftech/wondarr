namespace Wondarr.Core.Logging;

/// <summary>
/// Holds the secrets the process knows about (API key, Soulseek password, Plex token, …) so the
/// logging pipeline can replace them wherever they appear.
/// </summary>
public interface ISecretRegistry
{
    /// <summary>
    /// Registers a secret. Null, empty and values shorter than six characters are ignored, because
    /// replacing them would mangle unrelated text.
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

        lock (_gate)
        {
            // The JSON log writes the text already escaped, where a quote or a backslash in the
            // secret no longer matches its raw form, so the escaped form is registered with it.
            var added = _secrets.Add(secret);

            var escaped = JsonEscape(secret);

            if (escaped != secret)
            {
                added |= _secrets.Add(escaped);
            }

            if (added)
            {
                _snapshot = [.. _secrets.OrderByDescending(value => value.Length)];
            }
        }
    }

    /// <summary>
    /// The secret as Serilog's JSON formatter writes it: <c>"</c> and <c>\</c> backslash-escaped and
    /// control characters as escapes. Everything else is written as it is.
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
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case < ' ':
                    builder.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
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
