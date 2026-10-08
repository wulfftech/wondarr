namespace Wondarr.Sources.Torznab.Clients;

/// <summary>
/// Reads the infohash out of a magnet link. Only v1 hashes are supported: <c>urn:btih:</c> with 40
/// hex characters or 32 base32 characters; <c>urn:btmh:</c> (BitTorrent v2) is refused.
/// </summary>
public static class MagnetLink
{
    private const string UrnPrefix = "urn:btih:";

    /// <summary>
    /// Reads the infohash, as lower-case hex, out of <paramref name="magnet"/>.
    /// </summary>
    /// <param name="magnet">The magnet link, for example <c>magnet:?xt=urn:btih:…&amp;dn=…</c>.</param>
    /// <param name="hex">The 40-character lower-case hex infohash, or <see langword="string.Empty"/>.</param>
    /// <returns>Whether the magnet carries a v1 infohash Wondarr can use.</returns>
    public static bool TryGetInfoHash(string magnet, out string hex)
    {
        hex = string.Empty;

        if (string.IsNullOrEmpty(magnet))
        {
            return false;
        }

        // The parameters start after the first '?'; "magnet:?xt=…" and "magnet:xt=…" both work.
        var parameters = magnet[(magnet.IndexOf('?') + 1)..];

        foreach (var parameter in parameters.Split('&'))
        {
            var separator = parameter.IndexOf('=');

            if (separator < 0)
            {
                continue;
            }

            if (!string.Equals(Uri.UnescapeDataString(parameter[..separator]), "xt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var urn = Uri.UnescapeDataString(parameter[(separator + 1)..]);

            if (!urn.StartsWith(UrnPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // urn:btmh: (BitTorrent v2) and anything else: not supported.
                return false;
            }

            var hash = urn[UrnPrefix.Length..];

            if (hash.Length == 40 && IsHex(hash))
            {
                hex = hash.ToLowerInvariant();
                return true;
            }

            if (hash.Length == 32 && TryDecodeBase32(hash, out var bytes) && bytes.Length == 20)
            {
                hex = Convert.ToHexString(bytes).ToLowerInvariant();
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool IsHex(string text) =>
        text.All(character => Uri.IsHexDigit(character));

    /// <summary>Decodes RFC 4648 base32 (the alphabet A–Z and 2–7), as BitTorrent writes infohashes.</summary>
    private static bool TryDecodeBase32(string text, out byte[] bytes)
    {
        var bits = 0;
        var bitCount = 0;
        var output = new List<byte>(20);

        foreach (var character in text.ToUpperInvariant())
        {
            var value = character switch
            {
                >= 'A' and <= 'Z' => character - 'A',
                >= '2' and <= '7' => character - '2' + 26,
                _ => -1,
            };

            if (value < 0)
            {
                bytes = [];
                return false;
            }

            bits = (bits << 5) | value;
            bitCount += 5;

            if (bitCount >= 8)
            {
                bitCount -= 8;
                output.Add((byte)(bits >> bitCount));
            }
        }

        bytes = [.. output];
        return true;
    }
}
