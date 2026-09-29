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
    private const string DisconnectedPrefix = "Disconnected from the Soulseek server: ";
    private const string RejectedPrefix = "Failed to reconnect: The server rejected login attempt: ";
    private const string LoggedInPrefix = "Logged in to the Soulseek server as";
    private const string DuplicateLoginMarker = "another client logged in using the same username";
    private const string InvalidCredentialsMarker = "invalid username or password";
    private const string InvalidUsernameMarker = "INVALIDUSERNAME";
    private const string InvalidPasswordMarker = "INVALIDPASS";

    /// <summary>
    /// Classifies one line of slskd's output, or returns <see langword="null"/> when the line says
    /// nothing about the login.
    /// </summary>
    /// <remarks>
    /// The markers are anchored to the start of slskd's own message, after the optional
    /// <c>[HH:mm:ss LVL] </c> prefix. Searching the whole line would let a peer's search text or a
    /// filename — both of which reach this method through slskd's log — fake a login outcome.
    /// </remarks>
    public static SlskdLogSignal? Classify(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var message = StripLevelPrefix(line);

        if (message.StartsWith(DisconnectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var reason = message[DisconnectedPrefix.Length..];

            if (Contains(reason, DuplicateLoginMarker))
            {
                return SlskdLogSignal.DuplicateLogin;
            }

            return Contains(reason, InvalidCredentialsMarker) ? SlskdLogSignal.InvalidCredentials : null;
        }

        if (message.StartsWith(RejectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var reason = message[RejectedPrefix.Length..];

            return Contains(reason, InvalidUsernameMarker) || Contains(reason, InvalidPasswordMarker)
                ? SlskdLogSignal.InvalidCredentials
                : null;
        }

        return message.StartsWith(LoggedInPrefix, StringComparison.OrdinalIgnoreCase)
            ? SlskdLogSignal.LoggedIn
            : null;
    }

    /// <summary>
    /// Removes slskd's console prefix, as in <c>[23:05:36 INF] </c>, when the line has one. Only a
    /// real prefix — <c>HH:mm:ss</c> and a three-letter level — is removed, so a bracketed file name
    /// is left alone.
    /// </summary>
    private static string StripLevelPrefix(string line)
    {
        if (line[0] != '[')
        {
            return line;
        }

        var close = line.IndexOf(']');

        if (close < 0 || !IsLevelPrefix(line.AsSpan(1, close - 1)))
        {
            return line;
        }

        var rest = line[(close + 1)..];

        return rest.StartsWith(' ') ? rest[1..] : rest;
    }

    private static bool IsLevelPrefix(ReadOnlySpan<char> prefix) =>
        prefix.Length >= 12 &&
        prefix[2] == ':' &&
        prefix[5] == ':' &&
        prefix[8] == ' ' &&
        char.IsAsciiLetter(prefix[9]) &&
        char.IsAsciiLetter(prefix[10]) &&
        char.IsAsciiLetter(prefix[11]);

    private static bool Contains(string text, string marker) =>
        text.Contains(marker, StringComparison.OrdinalIgnoreCase);
}
