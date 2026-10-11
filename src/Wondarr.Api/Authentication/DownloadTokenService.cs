using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Wondarr.Api.Authentication;

/// <summary>
/// Mints and checks the short-lived tokens that let a plain link download a backup or a log file
/// without the API key in its URL. A token is good for one path and 60 seconds: an HMAC-SHA256 over
/// the path and the expiry, keyed with a random value made at start-up, so a restart voids every
/// outstanding token.
/// </summary>
public sealed partial class DownloadTokenService
{
    /// <summary>How long a token stays valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>The query parameter a download link carries the token in.</summary>
    public const string QueryName = "token";

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly TimeProvider _time;

    /// <summary>Initialises a new instance of the <see cref="DownloadTokenService"/> class.</summary>
    public DownloadTokenService(TimeProvider time) => _time = time;

    /// <summary>
    /// Whether a token may be minted for <paramref name="path"/>: only the backup download and the
    /// log file download, without the URL base.
    /// </summary>
    public static bool IsDownloadPath(string? path) => path is not null && DownloadPathPattern().IsMatch(path);

    /// <summary>Mints a token for exactly <paramref name="path"/>.</summary>
    public string Mint(string path)
    {
        var expires = _time.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds();
        var expiry = expires.ToString(CultureInfo.InvariantCulture);

        return $"{expiry}.{Base64Url.EncodeToString(Sign(path, expiry))}";
    }

    /// <summary>Whether <paramref name="token"/> was minted for exactly <paramref name="path"/> and has not expired.</summary>
    public bool IsValid(string? token, string path)
    {
        if (string.IsNullOrEmpty(token) || !IsDownloadPath(path))
        {
            return false;
        }

        var dot = token.IndexOf('.', StringComparison.Ordinal);

        if (dot <= 0 || !long.TryParse(token.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var expires))
        {
            return false;
        }

        if (expires < _time.GetUtcNow().ToUnixTimeSeconds())
        {
            return false;
        }

        byte[] provided;

        try
        {
            provided = Base64Url.DecodeFromChars(token.AsSpan(dot + 1));
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Sign(path, expires.ToString(CultureInfo.InvariantCulture)),
            provided);
    }

    private byte[] Sign(string path, string expiry) =>
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{path}\n{expiry}"));

    [GeneratedRegex(@"^(/api/v1/system/backup/[^/]+/download|/api/v1/log/file/[^/]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadPathPattern();
}
