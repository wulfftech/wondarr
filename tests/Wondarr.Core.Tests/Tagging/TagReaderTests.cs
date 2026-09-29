using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Tagging;
using Xunit;

namespace Wondarr.Core.Tests.Tagging;

/// <summary>
/// Reads the Picard-style fixtures of <c>tests/fixtures/media/reference/</c> (see <c>make.py</c> there):
/// they carry the §7.5 tag set as Picard writes it, which is what a reference library holds.
/// </summary>
public sealed class TagReaderTests
{
    private const string RecordingId = "b1a9c0e9-d987-4042-ae91-78d6a3267d69";
    private const string ReleaseId = "6defd963-fe91-4550-b18e-82c685603c2b";
    private const string ReleaseGroupId = "6b47c9a0-b9e1-3df9-a5e8-50a6ce0dbdbd";
    private const string ArtistId = "0383dadf-2a4e-4d10-a46a-e9e041da8eb3";

    private static TagReader Reader => new(NullLogger<TagReader>.Instance);

    private static string Fixture(string name) => FixtureMedia.Path("reference/" + name);

    [Theory]
    [InlineData("picard-v24.mp3")]
    [InlineData("picard-v23.mp3")]
    [InlineData("picard.flac")]
    [InlineData("picard.m4a")]
    public void Reads_the_picard_tag_set(string fixture)
    {
        var tags = Reader.Read(Fixture(fixture));

        tags.Should().NotBeNull();
        tags!.Title.Should().Be("Bohemian Rhapsody");
        tags.Artist.Should().Be("Queen");
        tags.AlbumArtist.Should().Be("Queen");
        tags.Album.Should().Be("A Night at the Opera");
        tags.TrackNumber.Should().Be(11);
        tags.TrackTotal.Should().Be(12);
        tags.DiscNumber.Should().Be(1);
        tags.Isrc.Should().Be("GBUM71029604");
        tags.MbReleaseId.Should().Be(ReleaseId);
        tags.MbReleaseGroupId.Should().Be(ReleaseGroupId);
        tags.MbArtistId.Should().Be(ArtistId);
        tags.Date.Should().StartWith("1975");
        tags.DurationMs.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("picard-v24.mp3")]
    [InlineData("picard-v23.mp3")]
    public void Reads_the_recording_id_from_the_ufid_frame(string fixture)
    {
        // Picard writes the recording id as a UFID frame owned by musicbrainz.org, not as a TXXX; the
        // fixture keeps that shape so this test fails if the reader ever only looks at the TXXX key.
        var tags = Reader.Read(Fixture(fixture));

        tags!.MbRecordingId.Should().Be(RecordingId);
    }

    [Fact]
    public void Reads_the_recording_id_of_a_vorbis_and_an_mp4_file()
    {
        Reader.Read(Fixture("picard.flac"))!.MbRecordingId.Should().Be(RecordingId);
        Reader.Read(Fixture("picard.m4a"))!.MbRecordingId.Should().Be(RecordingId);
    }

    [Fact]
    public void Reads_a_file_that_carries_only_plain_text_tags()
    {
        var tags = Reader.Read(Fixture("text-only.mp3"));

        tags.Should().NotBeNull();
        tags!.Title.Should().Be("Get Lucky");
        tags.Artist.Should().Be("Daft Punk feat. Pharrell Williams");
        tags.MbRecordingId.Should().BeNull();
        tags.MbReleaseId.Should().BeNull();
        tags.MbReleaseGroupId.Should().BeNull();
        tags.MbArtistId.Should().BeNull();
        tags.Isrc.Should().BeNull();
    }

    [Fact]
    public void Reads_nothing_but_the_duration_from_an_untagged_file()
    {
        var tags = Reader.Read(Fixture("untagged.opus"));

        tags.Should().NotBeNull();
        tags!.DurationMs.Should().BeGreaterThan(0);
        tags.Should().BeEquivalentTo(new FileTags(
            Title: null,
            Artist: null,
            AlbumArtist: null,
            Album: null,
            Date: null,
            Isrc: null,
            MbRecordingId: null,
            MbReleaseId: null,
            MbReleaseGroupId: null,
            MbArtistId: null,
            AcoustId: null,
            TrackNumber: null,
            TrackTotal: null,
            DiscNumber: null,
            DurationMs: tags.DurationMs));
    }

    [Fact]
    public void Returns_null_for_a_file_that_is_not_audio()
    {
        using var media = new TestMedia();
        var path = Path.Combine(media.TempDirectory, "random.mp3");
        File.WriteAllBytes(path, [.. Enumerable.Range(0, 4096).Select(index => (byte)(index * 31 % 251))]);

        Reader.Read(path).Should().BeNull();
    }

    [Fact]
    public async Task Reads_back_the_recording_id_the_writer_wrote()
    {
        // The writer cannot write UFID (ATL 7.17), so this is the TXXX fallback path.
        using var media = new TestMedia();
        var path = media.Copy("tone-320.mp3");
        var written = "1a5b7c9d-2e3f-4a5b-8c9d-0e1f2a3b4c5d";

        var result = await new TagWriter(NullLogger<TagWriter>.Instance)
            .WriteAsync(path, new TagSet { Title = "Get Lucky", Artist = "Daft Punk", MbRecordingId = written }, CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        Reader.Read(path)!.MbRecordingId.Should().Be(written);
    }
}