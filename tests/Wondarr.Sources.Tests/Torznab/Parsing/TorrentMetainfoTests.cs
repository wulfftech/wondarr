using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Wondarr.Sources.Torznab.Parsing;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Parsing;

/// <summary>
/// A tiny bencode writer so the tests can build torrents (and malformed torrents) byte by byte.
/// </summary>
internal sealed class BencodeWriter
{
    private readonly List<byte> _bytes = [];

    public byte[] ToArray() => [.. _bytes];

    public BencodeWriter Raw(byte[] bytes)
    {
        _bytes.AddRange(bytes);
        return this;
    }

    public BencodeWriter Raw(string text)
    {
        _bytes.AddRange(Encoding.ASCII.GetBytes(text));
        return this;
    }

    public BencodeWriter Int(long value) => Raw($"i{value}e");

    public BencodeWriter Str(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Raw($"{bytes.Length}:");
        return Raw(bytes);
    }

    public BencodeWriter BeginDict() => Raw("d");

    public BencodeWriter BeginList() => Raw("l");

    public BencodeWriter End() => Raw("e");
}

/// <summary>
/// Golden tests for the bencode reader: the file list and info hash of well-formed torrents, one
/// test per malformed input, and a fuzz pass over random mutations.
/// </summary>
public class TorrentMetainfoTests
{
    [Fact]
    public void Reads_a_single_file_torrent()
    {
        var info = Info(w => w.Str("name").Str("Selected Ambient Works").Str("length").Int(123_456));
        var torrent = Wrap(info);

        var metainfo = TorrentMetainfo.Parse(torrent);

        metainfo.Name.Should().Be("Selected Ambient Works");
        metainfo.Files.Should().ContainSingle();
        metainfo.Files[0].Index.Should().Be(0);
        metainfo.Files[0].Path.Should().Be("Selected Ambient Works");
        metainfo.Files[0].Size.Should().Be(123_456);
        metainfo.TotalSize.Should().Be(123_456);
        metainfo.InfoHash.Should().Be(ExpectedInfoHash(info));
    }

    [Fact]
    public void Reads_a_multi_file_torrent_with_nested_paths()
    {
        var torrent = MultiFileTorrent(
            "Selected Ambient Works",
            (["CD1", "01.flac"], 1_000, null),
            (["CD1", "02.flac"], 2_000, null),
            (["CD2", "sub", "01.flac"], 3_000, null));

        var metainfo = TorrentMetainfo.Parse(torrent);

        metainfo.Name.Should().Be("Selected Ambient Works");
        metainfo.Files.Should().HaveCount(3);
        metainfo.Files[0].Path.Should().Be("Selected Ambient Works/CD1/01.flac");
        metainfo.Files[1].Path.Should().Be("Selected Ambient Works/CD1/02.flac");
        metainfo.Files[2].Path.Should().Be("Selected Ambient Works/CD2/sub/01.flac");
        metainfo.Files.Select(f => f.Index).Should().BeEquivalentTo([0, 1, 2]);
        metainfo.TotalSize.Should().Be(6_000);
    }

    [Fact]
    public void Prefers_the_utf8_name_and_path_when_present()
    {
        var info = Info(w => w
            .Str("name").Str("Some Album")
            .Str("name.utf-8").Str("Some Ünïcode Album")
            .Str("files").BeginList()
            .BeginDict()
            .Str("length").Int(10)
            .Str("path").BeginList().Str("01.flac").End()
            .Str("path.utf-8").BeginList().Str("01 ü.flac").End()
            .End()
            .End());

        var metainfo = TorrentMetainfo.Parse(Wrap(info));

        metainfo.Name.Should().Be("Some Ünïcode Album");
        metainfo.Files.Should().ContainSingle();
        metainfo.Files[0].Path.Should().Be("Some Ünïcode Album/01 ü.flac");
    }

    [Fact]
    public void Skips_pad_files_but_keeps_their_index_slots()
    {
        var torrent = MultiFileTorrent(
            "Some Album",
            (["01.flac"], 1_000, null),
            ([".pad", "354"], 354, "p"),
            (["02.flac"], 2_000, null));

        var metainfo = TorrentMetainfo.Parse(torrent);

        metainfo.Files.Should().HaveCount(2);
        metainfo.Files[0].Index.Should().Be(0);
        metainfo.Files[0].Path.Should().Be("Some Album/01.flac");
        metainfo.Files[1].Index.Should().Be(2);
        metainfo.Files[1].Path.Should().Be("Some Album/02.flac");
        metainfo.TotalSize.Should().Be(3_000);
    }

    [Fact]
    public void Hashes_the_exact_info_bytes_even_with_extra_keys_around_info()
    {
        var info = Info(w => w.Str("name").Str("Some Album").Str("length").Int(1));
        var torrent = Wrap(info);

        // Wrap adds keys both before and after the info dictionary.
        Encoding.ASCII.GetString(torrent).Should().Contain("comment").And.Contain("created by");
        TorrentMetainfo.Parse(torrent).InfoHash.Should().Be(ExpectedInfoHash(info));
    }

    [Fact]
    public void Reads_the_v1_part_of_a_hybrid_torrent()
    {
        var info = Info(w => w
            .Str("name").Str("Some Album")
            .Str("meta version").Int(2)
            .Str("file tree").BeginDict()
            .Str("01.flac").BeginDict().Str("").BeginDict().Str("length").Int(1).End().End()
            .End()
            .Str("files").BeginList()
            .BeginDict().Str("length").Int(1).Str("path").BeginList().Str("01.flac").End().End()
            .End());

        var metainfo = TorrentMetainfo.Parse(Wrap(info));

        metainfo.Files.Should().ContainSingle();
        metainfo.Files[0].Path.Should().Be("Some Album/01.flac");
    }

    [Fact]
    public void Refuses_a_v2_only_torrent()
    {
        var info = Info(w => w
            .Str("name").Str("Some Album")
            .Str("meta version").Int(2)
            .Str("file tree").BeginDict()
            .Str("01.flac").BeginDict().Str("").BeginDict().Str("length").Int(1).End().End()
            .End());

        var act = () => TorrentMetainfo.Parse(Wrap(info));

        act.Should().Throw<TorrentMetainfoException>().WithMessage("v2-only torrents are not supported");
    }

    [Fact]
    public void Rejects_input_over_ten_mebibytes()
    {
        var act = () => TorrentMetainfo.Parse(new byte[10 * 1024 * 1024 + 1]);

        act.Should().Throw<TorrentMetainfoException>();
    }

    [Fact]
    public void Rejects_empty_input()
    {
        var act = () => TorrentMetainfo.Parse([]);

        act.Should().Throw<TorrentMetainfoException>().WithMessage("*byte 0*");
    }

    [Fact]
    public void Rejects_nesting_deeper_than_sixty_four_levels()
    {
        var text = new StringBuilder();
        for (var i = 0; i < 70; i++)
        {
            text.Append("d1:a");
        }

        text.Append("i1e");
        for (var i = 0; i < 70; i++)
        {
            text.Append('e');
        }

        var act = () => TorrentMetainfo.Parse(Encoding.ASCII.GetBytes(text.ToString()));

        act.Should().Throw<TorrentMetainfoException>().WithMessage("*nesting*");
    }

    [Fact]
    public void Rejects_a_string_length_that_runs_past_the_end()
    {
        var act = () => TorrentMetainfo.Parse("d1:a10:abc"u8);

        act.Should().Throw<TorrentMetainfoException>().WithMessage("*runs past the end*");
    }

    [Fact]
    public void Rejects_a_non_digit_string_length()
    {
        var act = () => TorrentMetainfo.Parse("d1:a0x:abc"u8);

        act.Should().Throw<TorrentMetainfoException>().WithMessage("*string length*");
    }

    [Fact]
    public void Rejects_a_missing_e()
    {
        var act = () => TorrentMetainfo.Parse("d1:ai12"u8);

        act.Should().Throw<TorrentMetainfoException>().WithMessage("*missing 'e'*");
    }

    [Fact]
    public void Rejects_an_integer_with_a_leading_zero()
    {
        var act = () => TorrentMetainfo.Parse("d1:ai01ee"u8);

        act.Should().Throw<TorrentMetainfoException>().WithMessage("*leading zero*");
    }

    [Fact]
    public void Never_throws_anything_but_the_metainfo_exception_under_random_mutations()
    {
        var valid = MultiFileTorrent(
            "Some Album",
            (["01.flac"], 1_000, null),
            ([".pad", "354"], 354, "p"),
            (["sub", "02.flac"], 2_000, null));
        var random = new Random(20240707);

        for (var i = 0; i < 2_000; i++)
        {
            var mutated = Mutate(valid, random);
            MutateInPlace(mutated, random);

            try
            {
                TorrentMetainfo.Parse(mutated);
            }
            catch (TorrentMetainfoException)
            {
                // The only failure the reader is allowed to report.
            }
        }
    }

    private static byte[] Mutate(byte[] source, Random random)
    {
        var bytes = source.ToArray();
        switch (random.Next(3))
        {
            case 0:
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                break;
            case 1:
                var without = new List<byte>(bytes);
                without.RemoveAt(random.Next(without.Count));
                bytes = [.. without];
                break;
            default:
                var with = new List<byte>(bytes);
                with.Insert(random.Next(with.Count), (byte)random.Next(256));
                bytes = [.. with];
                break;
        }

        return bytes;
    }

    private static void MutateInPlace(byte[] bytes, Random random)
    {
        if (random.Next(2) == 0)
        {
            bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
        }
    }

    private static string ExpectedInfoHash(byte[] info)
    {
        // The BitTorrent v1 info hash is SHA-1 by specification; this is not a secret.
#pragma warning disable CA5350
        return Convert.ToHexString(SHA1.HashData(info)).ToLowerInvariant();
#pragma warning restore CA5350
    }

    private static byte[] Info(Func<BencodeWriter, BencodeWriter> write)
    {
        var writer = new BencodeWriter().BeginDict();
        write(writer);
        return writer.End().ToArray();
    }

    private static byte[] Wrap(byte[] info) =>
        new BencodeWriter()
            .BeginDict()
            .Str("announce").Str("http://tracker.example/announce")
            .Str("comment").Str("a key before the info dictionary")
            .Str("info").Raw(info)
            .Str("created by").Str("a key after the info dictionary")
            .End()
            .ToArray();

    private static byte[] MultiFileTorrent(string name, params (string[] Path, long Size, string? Attr)[] entries)
    {
        var info = Info(w =>
        {
            w.Str("name").Str(name);
            w.Str("files").BeginList();
            foreach (var (path, size, attr) in entries)
            {
                w.BeginDict();
                w.Str("length").Int(size);
                if (attr is not null)
                {
                    w.Str("attr").Str(attr);
                }

                w.Str("path").BeginList();
                foreach (var segment in path)
                {
                    w.Str(segment);
                }

                w.End();
                w.End();
            }

            w.End();
            w.Str("piece length").Int(32_768);
            return w;
        });

        return Wrap(info);
    }
}
