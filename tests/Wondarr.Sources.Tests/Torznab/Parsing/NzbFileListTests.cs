using System.Text;
using FluentAssertions;
using Wondarr.Sources.Torznab.Parsing;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Parsing;

/// <summary>
/// Golden tests over the NZB fixtures: subject forms, summed segment sizes, the archive/par2/audio
/// classification, plain vs obfuscated names and the IsClean rule (DECISIONS #10).
/// </summary>
public class NzbFileListTests
{
    [Fact]
    public void Reads_the_clean_album_fixture()
    {
        var files = ParseFixture("clean-album.nzb");

        files.Should().HaveCount(14);
        files.Select(f => f.Index).Should().BeInAscendingOrder();
        files.Take(12).Should().OnlyContain(f => f.IsAudio);
        files.Skip(12).Should().OnlyContain(f => f.IsPar2);
        files.Should().OnlyContain(f => f.IsPlainName);
        NzbFileList.IsClean(files).Should().BeTrue();
    }

    [Fact]
    public void Does_not_resolve_the_doctype_of_the_clean_album_fixture()
    {
        // clean-album.nzb carries the newzbin DOCTYPE with an external URL; parsing must not
        // resolve it (DtdProcessing.Ignore, XmlResolver null) and still succeed offline.
        var files = ParseFixture("clean-album.nzb");

        files.Should().HaveCount(14);
    }

    [Fact]
    public void Sums_the_segment_bytes_per_file()
    {
        var files = ParseFixture("clean-album.nzb");

        files[0].Size.Should().Be(1_000_000 + 500_000);
        files[13].Size.Should().Be(768_000);
    }

    [Fact]
    public void Reads_the_rar_album_fixture()
    {
        var files = ParseFixture("rar-album.nzb");

        files.Should().HaveCount(9);
        files.Take(7).Should().OnlyContain(f => f.IsArchive);
        files[7].IsPar2.Should().BeTrue();
        files[7].IsArchive.Should().BeFalse();
        NzbFileList.IsClean(files).Should().BeFalse();
    }

    [Fact]
    public void Reads_the_obfuscated_fixture()
    {
        var files = ParseFixture("obfuscated.nzb");

        files.Should().HaveCount(5);
        files.Should().OnlyContain(f => !f.IsPlainName);
        files.Take(4).Should().OnlyContain(f => f.IsAudio);
        files[4].IsPar2.Should().BeTrue();
        NzbFileList.IsClean(files).Should().BeFalse();
    }

    [Fact]
    public void Reads_the_quoted_yenc_and_nameless_subject_forms()
    {
        var cleanFiles = ParseFixture("clean-album.nzb");
        var rarFiles = ParseFixture("rar-album.nzb");

        // The quoted form.
        cleanFiles[0].FileName.Should().Be("Aphex Twin - Selected Ambient Works 85-92 - 01.flac");

        // The bare yEnc form: everything before "yEnc" ending in an extension.
        rarFiles[0].FileName.Should().Be("Aphex Twin - SAW 85-92 Part01.rar");
        rarFiles[2].FileName.Should().Be("Aphex Twin - SAW 85-92.r00");

        // No quoted name and no yEnc name: FileName stays null.
        rarFiles[8].FileName.Should().BeNull();
        rarFiles[8].Subject.Should().Contain("no name in this one");
    }

    [Fact]
    public void Classifies_par2_archive_and_audio_files()
    {
        var files = ParseFixture("clean-album.nzb");

        files[0].IsAudio.Should().BeTrue();
        files[0].IsPar2.Should().BeFalse();
        files[0].IsArchive.Should().BeFalse();
        files[12].IsPar2.Should().BeTrue();
        files[12].IsAudio.Should().BeFalse();
    }

    [Fact]
    public void Flags_sixteen_character_hex_and_base64_like_names_as_not_plain()
    {
        var files = ParseFixture("obfuscated.nzb");

        files[0].FileName.Should().Be("0123456789abcdef.flac");
        files[0].IsPlainName.Should().BeFalse();
        files[2].FileName.Should().Be("AbCdEfGhIjKlMnOp.flac");
        files[2].IsPlainName.Should().BeFalse();
    }

    [Fact]
    public void Treats_sidecars_with_plain_names_as_clean()
    {
        var nzb = """
            <?xml version="1.0" encoding="UTF-8"?>
            <nzb>
              <files>
                <file subject="&quot;Album - 01.flac&quot; yEnc (1/3)" poster="a@example.com" date="1700000000">
                  <segments><segment bytes="1000">bw==</segment></segments>
                </file>
                <file subject="&quot;Album.nfo&quot; yEnc (2/3)" poster="a@example.com" date="1700000000">
                  <segments><segment bytes="10">bg==</segment></segments>
                </file>
                <file subject="&quot;Album.par2&quot; yEnc (3/3)" poster="a@example.com" date="1700000000">
                  <segments><segment bytes="10">cA==</segment></segments>
                </file>
              </files>
            </nzb>
            """;

        var files = NzbFileList.Parse(new MemoryStream(Encoding.UTF8.GetBytes(nzb)));

        files.Should().HaveCount(3);
        NzbFileList.IsClean(files).Should().BeTrue();
    }

    [Fact]
    public void Treats_an_archive_or_obfuscated_name_as_not_clean()
    {
        var nzb = """
            <?xml version="1.0" encoding="UTF-8"?>
            <nzb>
              <files>
                <file subject="&quot;Album - 01.flac&quot; yEnc (1/2)" poster="a@example.com" date="1700000000">
                  <segments><segment bytes="1000">bw==</segment></segments>
                </file>
                <file subject="&quot;Album.rar&quot; yEnc (2/2)" poster="a@example.com" date="1700000000">
                  <segments><segment bytes="1000">cg==</segment></segments>
                </file>
              </files>
            </nzb>
            """;

        var files = NzbFileList.Parse(new MemoryStream(Encoding.UTF8.GetBytes(nzb)));

        files[1].IsArchive.Should().BeTrue();
        NzbFileList.IsClean(files).Should().BeFalse();
    }

    private static IReadOnlyList<NzbFile> ParseFixture(string name)
    {
        using var stream = File.OpenRead(FixturePath(name));
        return NzbFileList.Parse(stream);
    }

    private static string FixturePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "tests", "fixtures", "nzb", name)))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "tests", "fixtures", "nzb", name);
    }
}
