using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FakeSlskd;

/// <summary>
/// Deterministic identifiers for values that must map to the same id on every run: the fake's share
/// ids (slskd uses uppercase hex) and the AcoustID stub's result id for a fingerprint.
/// FNV-1a is enough here and keeps the tool free of cryptographic primitives it does not need.
/// </summary>
public static class Deterministic
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>Hashes <paramref name="value"/> to a 64-bit FNV-1a value.</summary>
    public static ulong Hash64(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var hash = FnvOffsetBasis;

        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        return hash;
    }

    /// <summary>A stable 40-character uppercase hex id for <paramref name="value"/>, in slskd's share-id style.</summary>
    public static string HexId(string value)
    {
        var first = Hash64(value).ToString("X16", CultureInfo.InvariantCulture);
        var second = Hash64(value + "#1").ToString("X16", CultureInfo.InvariantCulture);
        var third = Hash64(value + "#2").ToString("X16", CultureInfo.InvariantCulture);

        return string.Concat(first, second, third)[..40];
    }

    /// <summary>A stable UUID for <paramref name="value"/>.</summary>
    public static Guid Uuid(string value)
    {
        Span<byte> bytes = stackalloc byte[16];

        BinaryPrimitives.WriteUInt64LittleEndian(bytes, Hash64(value));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], Hash64(value + "#uuid"));

        return new Guid(bytes);
    }
}
