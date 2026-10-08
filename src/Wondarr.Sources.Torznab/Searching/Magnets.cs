namespace Wondarr.Sources.Torznab.Searching;

/// <summary>Reads the info-hash out of a magnet link (<c>xt=urn:btih:</c>, hex or base32).</summary>
public static class Magnets
{
    private const string Prefix = "urn:btih:";
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>The magnet's v1 info-hash as 40 lower-case hex digits, or null when it carries none.</summary>
    /// <param name="magnet">The magnet link, or null.</param>
    public static string? InfoHash(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet) || !magnet.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var parameter in magnet[8..].Split('&'))
        {
            var separator = parameter.IndexOf('=', StringComparison.Ordinal);

            if (separator < 0 || !parameter.AsSpan(0, separator).Equals("xt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(parameter[(separator + 1)..]);

            if (!value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hash = value[Prefix.Length..];

            if (hash.Length == 40 && hash.All(Uri.IsHexDigit))
            {
                return hash.ToLowerInvariant();
            }

            if (hash.Length == 32 && FromBase32(hash) is { } bytes)
            {
                return Convert.ToHexStringLower(bytes);
            }
        }

        return null;
    }

    /// <summary>Decodes 32 base32 characters into the 20 bytes of a SHA-1, or null when one is not base32.</summary>
    private static byte[]? FromBase32(string text)
    {
        var bytes = new byte[20];
        var buffer = 0;
        var bits = 0;
        var position = 0;

        foreach (var character in text.ToUpperInvariant())
        {
            var value = Base32Alphabet.IndexOf(character, StringComparison.Ordinal);

            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | value;
            bits += 5;

            if (bits >= 8)
            {
                bits -= 8;
                bytes[position++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }

        return bytes;
    }
}
