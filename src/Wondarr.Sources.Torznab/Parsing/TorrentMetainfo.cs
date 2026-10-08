using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Wondarr.Sources.Torznab.Parsing;

/// <summary>Thrown when a torrent's metainfo cannot be read: malformed bencode, over the size limit, or v2-only.</summary>
public sealed class TorrentMetainfoException : Exception
{
    /// <summary>
    /// Creates the exception with the reason the metainfo could not be read; malformed bencode
    /// messages carry the byte offset the reader stopped at.
    /// </summary>
    /// <param name="message">The reason, with the byte offset for malformed input.</param>
    public TorrentMetainfoException(string message)
        : base(message)
    {
    }
}

/// <summary>One file inside a torrent, numbered as the bencode lists it (pad files keep their slot).</summary>
/// <param name="Index">The 0-based position of the file in the torrent's file list, pad files included.</param>
/// <param name="Path">The <c>/</c>-joined path: the torrent name as the first segment for a multi-file torrent.</param>
/// <param name="Size">The file's length in bytes.</param>
public sealed record TorrentFile(int Index, string Path, long Size);

/// <summary>
/// Reads the info hash, name and file list out of the bencoded metainfo of a v1 (or hybrid v1/v2)
/// torrent. Wondarr's own bencode reader, not ported (DECISIONS #4): it never does I/O and every
/// malformed input fails with a <see cref="TorrentMetainfoException"/> naming the byte offset.
/// </summary>
public sealed class TorrentMetainfo
{
    private const int MaxInputSize = 10 * 1024 * 1024;
    private const int MaxNesting = 64;

    private TorrentMetainfo(string infoHash, string name, IReadOnlyList<TorrentFile> files, long totalSize)
    {
        InfoHash = infoHash;
        Name = name;
        Files = files;
        TotalSize = totalSize;
    }

    /// <summary>Gets the v1 info hash: 40 lower-case hex characters, the SHA-1 of the info dictionary's exact bytes.</summary>
    public string InfoHash { get; }

    /// <summary>Gets the torrent name (the <c>name.utf-8</c> value when present, else <c>name</c>).</summary>
    public string Name { get; }

    /// <summary>
    /// Gets the torrent's files, pad files (BEP 47 <c>attr</c> containing <c>p</c>) excluded but with
    /// their <see cref="TorrentFile.Index"/> slots kept as the client numbers them.
    /// </summary>
    public IReadOnlyList<TorrentFile> Files { get; }

    /// <summary>Gets the total size in bytes of the files in <see cref="Files"/> (pad files excluded).</summary>
    public long TotalSize { get; }

    /// <summary>
    /// Parses the bencoded metainfo of a torrent.
    /// </summary>
    /// <param name="data">The raw bytes of the <c>.torrent</c> file (at most 10 MiB).</param>
    /// <returns>The torrent's info hash, name and file list.</returns>
    /// <exception cref="TorrentMetainfoException">The input is malformed, over 10 MiB, nested deeper
    /// than 64 levels, or a v2-only torrent (those are refused: "v2-only torrents are not supported").</exception>
    public static TorrentMetainfo Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxInputSize)
        {
            throw new TorrentMetainfoException($"The torrent is {data.Length} bytes; the limit is {MaxInputSize} (10 MiB).");
        }

        if (data.Length == 0)
        {
            throw new TorrentMetainfoException("Malformed bencode at byte 0: the input is empty.");
        }

        var reader = new BencodeReader(data, 0);
        if (reader.Peek() != (byte)'d')
        {
            Fail(reader.Position, "the top-level value must be a dictionary");
        }

        reader.SkipByte();

        int infoStart = -1;
        int infoEnd = -1;
        while (reader.Peek() != (byte)'e')
        {
            var key = reader.ReadStringBytes();
            if (infoStart >= 0)
            {
                reader.SkipValue(2);
                continue;
            }

            var start = reader.Position;
            reader.SkipValue(2);
            if (key.SequenceEqual("info"u8))
            {
                infoStart = start;
                infoEnd = reader.Position;
            }
        }

        if (infoStart < 0)
        {
            throw new TorrentMetainfoException("Malformed bencode: the top-level dictionary has no info dictionary.");
        }

        // The BitTorrent v1 info hash is SHA-1 by specification; this is not a secret.
#pragma warning disable CA5350
        var infoHash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(data.Slice(infoStart, infoEnd - infoStart))).ToLowerInvariant();
#pragma warning restore CA5350

        var torrent = ReadInfo(data.Slice(infoStart, infoEnd - infoStart), infoStart, infoHash);
        return torrent;
    }

    private static TorrentMetainfo ReadInfo(ReadOnlySpan<byte> infoBytes, int baseOffset, string infoHash)
    {
        var info = new BencodeReader(infoBytes, baseOffset);
        if (info.Peek() != (byte)'d')
        {
            Fail(info.Position, "the info dictionary must be a dictionary");
        }

        info.SkipByte();

        string? name = null;
        string? nameUtf8 = null;
        long singleLength = 0;
        var hasLength = false;
        var hasFiles = false;
        var hasMetaVersion = false;
        long metaVersion = 0;
        var rawFiles = new List<RawFile>();

        while (info.Peek() != (byte)'e')
        {
            var key = info.ReadStringBytes();
            if (key.SequenceEqual("name"u8))
            {
                name = Decode(info.ReadStringBytes());
            }
            else if (key.SequenceEqual("name.utf-8"u8))
            {
                nameUtf8 = Decode(info.ReadStringBytes());
            }
            else if (key.SequenceEqual("length"u8))
            {
                singleLength = ReadNonNegativeLength(ref info);
                hasLength = true;
            }
            else if (key.SequenceEqual("meta version"u8))
            {
                metaVersion = info.ReadInteger();
                hasMetaVersion = true;
            }
            else if (key.SequenceEqual("file tree"u8))
            {
                // A v2 file tree is present but the v1 part wins for hybrid torrents.
                info.SkipValue(2);
            }
            else if (key.SequenceEqual("files"u8))
            {
                hasFiles = true;
                ReadFiles(ref info, rawFiles);
            }
            else
            {
                info.SkipValue(2);
            }
        }

        if (!hasFiles && !hasLength)
        {
            if (hasMetaVersion && metaVersion == 2)
            {
                throw new TorrentMetainfoException("v2-only torrents are not supported");
            }

            Fail(baseOffset, "the info dictionary has neither a files list nor a single-file length");
        }

        var torrentName = nameUtf8 ?? name;
        if (string.IsNullOrEmpty(torrentName))
        {
            Fail(baseOffset, "the info dictionary has no name");
        }

        List<TorrentFile> files;
        if (hasFiles)
        {
            // A hybrid torrent (v1 files and a v2 file tree) reads its v1 part.
            files = [];
            foreach (var raw in rawFiles)
            {
                if (raw.IsPad)
                {
                    continue;
                }

                files.Add(new TorrentFile(raw.Index, $"{torrentName}/{string.Join("/", raw.Segments)}", raw.Size));
            }
        }
        else
        {
            files = [new TorrentFile(0, torrentName, singleLength)];
        }

        long totalSize = 0;
        foreach (var file in files)
        {
            totalSize += file.Size;
        }

        return new TorrentMetainfo(infoHash, torrentName, files, totalSize);
    }

    private static void ReadFiles(ref BencodeReader info, List<RawFile> rawFiles)
    {
        if (info.Peek() != (byte)'l')
        {
            Fail(info.Position, "the files value must be a list");
        }

        info.SkipByte();

        var index = 0;
        while (info.Peek() != (byte)'e')
        {
            rawFiles.Add(ReadFileEntry(ref info, index));
            index++;
        }

        info.SkipByte();
    }

    private static RawFile ReadFileEntry(ref BencodeReader info, int index)
    {
        if (info.Peek() != (byte)'d')
        {
            Fail(info.Position, "a files entry must be a dictionary");
        }

        info.SkipByte();

        List<string>? segments = null;
        List<string>? segmentsUtf8 = null;
        long size = -1;
        var isPad = false;

        while (info.Peek() != (byte)'e')
        {
            var key = info.ReadStringBytes();
            if (key.SequenceEqual("path"u8))
            {
                segments = ReadStringList(ref info);
            }
            else if (key.SequenceEqual("path.utf-8"u8))
            {
                segmentsUtf8 = ReadStringList(ref info);
            }
            else if (key.SequenceEqual("length"u8))
            {
                size = ReadNonNegativeLength(ref info);
            }
            else if (key.SequenceEqual("attr"u8))
            {
                isPad = Decode(info.ReadStringBytes()).Contains('p');
            }
            else
            {
                info.SkipValue(3);
            }
        }

        info.SkipByte();

        var path = segmentsUtf8 ?? segments;
        if (path is null || path.Count == 0)
        {
            Fail(info.Position, "a files entry has no path");
        }

        if (size < 0)
        {
            Fail(info.Position, "a files entry has no length");
        }

        return new RawFile(index, path, size, isPad);
    }

    private static List<string> ReadStringList(ref BencodeReader info)
    {
        if (info.Peek() != (byte)'l')
        {
            Fail(info.Position, "a path must be a list of strings");
        }

        info.SkipByte();

        var segments = new List<string>();
        while (info.Peek() != (byte)'e')
        {
            segments.Add(Decode(info.ReadStringBytes()));
        }

        info.SkipByte();

        return segments;
    }

    private static long ReadNonNegativeLength(ref BencodeReader info)
    {
        var length = info.ReadInteger();
        if (length < 0)
        {
            Fail(info.Position, "a file length must not be negative");
        }

        return length;
    }

    private static string Decode(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    [DoesNotReturn]
    private static void Fail(int offset, string why) => throw new TorrentMetainfoException($"Malformed bencode at byte {offset}: {why}.");

    private sealed record RawFile(int Index, List<string> Segments, long Size, bool IsPad);

    private ref struct BencodeReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly int _base;
        private int _pos;

        public BencodeReader(ReadOnlySpan<byte> data, int baseOffset)
        {
            _data = data;
            _base = baseOffset;
            _pos = 0;
        }

        public readonly int Position => _base + _pos;

        public byte Peek()
        {
            if (_pos >= _data.Length)
            {
                Fail(Position, "unexpected end of input (a missing 'e'?)");
            }

            return _data[_pos];
        }

        public void SkipByte() => _pos++;

        public long ReadInteger()
        {
            if (Peek() != (byte)'i')
            {
                Fail(Position, "expected an integer ('i')");
            }

            _pos++;

            var negative = false;
            if (_pos < _data.Length && _data[_pos] == (byte)'-')
            {
                negative = true;
                _pos++;
            }

            var digitsStart = Position;
            if (_pos >= _data.Length || !IsDigit(_data[_pos]))
            {
                Fail(digitsStart, "integer with no digits");
            }

            // A leading zero is not allowed (and neither is a negative zero).
            if (_data[_pos] == (byte)'0' && _pos + 1 < _data.Length && IsDigit(_data[_pos + 1]))
            {
                Fail(digitsStart, "integer with a leading zero");
            }

            long value = 0;
            while (_pos < _data.Length && IsDigit(_data[_pos]))
            {
                if (value > (long.MaxValue - 9) / 10)
                {
                    Fail(digitsStart, "integer too large");
                }

                value = (value * 10) + (_data[_pos] - (byte)'0');
                _pos++;
            }

            if (negative && value == 0)
            {
                Fail(digitsStart, "negative zero integer");
            }

            if (_pos >= _data.Length || _data[_pos] != (byte)'e')
            {
                Fail(Position, "missing 'e' after the integer");
            }

            _pos++;
            return negative ? -value : value;
        }

        public ReadOnlySpan<byte> ReadStringBytes()
        {
            var lengthStart = Position;
            long length = 0;
            var sawDigit = false;
            while (_pos < _data.Length && IsDigit(_data[_pos]))
            {
                sawDigit = true;
                if (length > (long.MaxValue - 9) / 10)
                {
                    Fail(lengthStart, "string length too large");
                }

                length = (length * 10) + (_data[_pos] - (byte)'0');
                _pos++;
            }

            if (!sawDigit)
            {
                Fail(lengthStart, "expected a string length (a non-digit length)");
            }

            if (_pos >= _data.Length || _data[_pos] != (byte)':')
            {
                Fail(Position, "expected ':' after the string length");
            }

            _pos++;

            if (length > _data.Length - _pos)
            {
                Fail(lengthStart, $"string length {length} runs past the end of the input");
            }

            var bytes = _data.Slice(_pos, (int)length);
            _pos += (int)length;
            return bytes;
        }

        public void SkipValue(int depth)
        {
            if (depth > MaxNesting)
            {
                Fail(Position, $"container nesting deeper than {MaxNesting} levels");
            }

            var current = Peek();
            if (current == (byte)'i')
            {
                ReadInteger();
                return;
            }

            if (current == (byte)'l' || current == (byte)'d')
            {
                _pos++;
                while (Peek() != (byte)'e')
                {
                    if (current == (byte)'d')
                    {
                        ReadStringBytes();
                    }

                    SkipValue(depth + 1);
                }

                _pos++;
                return;
            }

            if (IsDigit(current))
            {
                ReadStringBytes();
                return;
            }

            Fail(Position, $"unexpected byte 0x{current:X2}");
        }

        private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';
    }
}
