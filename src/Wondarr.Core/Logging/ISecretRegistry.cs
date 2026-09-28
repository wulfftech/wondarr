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
            if (_secrets.Add(secret))
            {
                _snapshot = [.. _secrets.OrderByDescending(value => value.Length)];
            }
        }
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
