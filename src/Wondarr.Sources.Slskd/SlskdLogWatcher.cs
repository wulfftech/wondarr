namespace Wondarr.Sources.Slskd;

/// <summary>What slskd's own log says about the Soulseek login.</summary>
public enum SlskdLogSignal
{
    /// <summary>slskd logged in to the Soulseek server.</summary>
    LoggedIn,

    /// <summary>The server rejected the account.</summary>
    InvalidCredentials,

    /// <summary>Another client logged in with the same account, so this session was disconnected.</summary>
    DuplicateLogin,
}

/// <summary>
/// Reads the three login outcomes slskd only ever puts in its log — a successful login, a rejected
/// account, and a duplicate-login kick (after which slskd does <em>not</em> reconnect by itself) —
/// out of the lines the supervisor already receives (<see cref="SlskdHost"/>'s child output).
/// </summary>
/// <remarks>
/// Pure: it decides nothing and holds nothing; the caller records what it returns.
/// The message texts are the ones slskd 0.26.0 writes (<c>src/slskd/Application.cs</c>), verified
/// live on <c>ch01</c> on 2026-09-29 (<c>docs/research/research_soulseek.md</c> §"Verified 2026-09-29").
/// </remarks>
public static class SlskdLogWatcher
{
    private const string DuplicateLoginMarker = "another client logged in using the same username";
    private const string InvalidCredentialsMarker = "invalid username or password";
    private const string InvalidUsernameMarker = "INVALIDUSERNAME";
    private const string InvalidPasswordMarker = "INVALIDPASS";
    private const string LoggedInMarker = "Logged in to the Soulseek server as";

    /// <summary>
    /// Classifies one line of slskd's output, or returns <see langword="null"/> when the line says
    /// nothing about the login.
    /// </summary>
    public static SlskdLogSignal? Classify(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        // Checked first: the kick line also says "logged in".
        if (Contains(line, DuplicateLoginMarker))
        {
            return SlskdLogSignal.DuplicateLogin;
        }

        if (Contains(line, InvalidCredentialsMarker) ||
            Contains(line, InvalidUsernameMarker) ||
            Contains(line, InvalidPasswordMarker))
        {
            return SlskdLogSignal.InvalidCredentials;
        }

        return Contains(line, LoggedInMarker) ? SlskdLogSignal.LoggedIn : null;
    }

    private static bool Contains(string line, string marker) =>
        line.Contains(marker, StringComparison.OrdinalIgnoreCase);
}