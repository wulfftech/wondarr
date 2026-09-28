namespace Compilarr.Core.Authentication;

/// <summary>
/// The persisted form of the local user. Never holds the password: only the PBKDF2 parameters
/// needed to verify it.
/// </summary>
public sealed record StoredCredentials
{
    /// <summary>The username, as entered.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Base64 of the random per-user salt.</summary>
    public string Salt { get; init; } = string.Empty;

    /// <summary>PBKDF2 iteration count used for <see cref="Hash"/>.</summary>
    public int Iterations { get; init; }

    /// <summary>Base64 of the PBKDF2 hash.</summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>Stable identifier for the user, kept across password changes.</summary>
    public Guid Identifier { get; init; }
}
