using System.Globalization;
using System.Text;
using ATL;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Tagging;
using Xunit;
using Xunit.Abstractions;

namespace Wondarr.Core.Tests.Tagging;

/// <summary>Round-trips the whole §7.5 tag set through every supported format.</summary>
public sealed class TagWriterTests(ITestOutputHelper output)
{
    private const string ReleaseId = "5c5e0e3a-3f5d-4a1e-9a3e-2b0d4c6f7a81";
    private const string ReleaseTrackId = "0f0f7c4a-8b2d-4d6e-9f1a-6c3b5d7e9a02";
    private const string RecordingId = "1a5b7c9d-2e3f-4a5b-8c9d-0e1f2a3b4c5d";
    private const string ReleaseGroupId = "2b6c8d0e-3f4a-4b5c-9d0e-1f2a3b4c5d6e";
    private const string ArtistId = "3c7d9e1f-4a5b-4c6d-8e9f-0a1b2c3d4e5f";
    private const string AlbumArtistId = "4d8e0f2a-5b6c-4d7e-9f0a-1b2c3d4e5f60";
    private const string AcoustId = "6a1b2c3d-4e5f-4a6b-8c9d-0e1f2a3b4c5d";
    private const string Lyrics = "Like the legend of the phoenix";
    private static readonly string[] ArtistNames = ["Daft Punk", "Pharrell Williams", "Nile Rodgers"];

    private static TagWriter Writer => new(NullLogger<TagWriter>.Instance);

    private static TagSet GetLucky => new()
    {
        Title = "Get Lucky",
        Artist = "Daft Punk",
        Artists = ArtistNames,
        AlbumArtist = "Daft Punk",
        Album = "Random Access Memories",
        TrackNumber = 8,
        TrackTotal = 13,
        DiscNumber = 1,
        DiscTotal = 1,
        Date = "2013-05-17",
        OriginalDate = "2013-04-19",
        Isrc = "USQX91300108",
        MbRecordingId = RecordingId,
        MbReleaseTrackId = ReleaseTrackId,
        MbReleaseId = ReleaseId,
        MbReleaseGroupId = ReleaseGroupId,
        MbArtistId = ArtistId,
        MbAlbumArtistId = AlbumArtistId,
        ReleaseType = "album",
        ReleaseStatus = "official",
        AcoustId = AcoustId,
        FrontCover = TestMedia.CoverJpeg,
        Lyrics = Lyrics,
        Genre = "Electronic",
        Comment = "Imported by Wondarr from Soulseek",
    };

    [Theory]
    [MemberData(nameof(TestMedia.Formats), MemberType = typeof(TestMedia))]
    public async Task Writes_every_field_and_reads_every_field_back(string fixture)
    {
        using var media = new TestMedia();
        var path = media.Copy(fixture);
        var before = TestMedia.Probe(path);

        var result = await Writer.WriteAsync(path, GetLucky, CancellationToken.None);

        DumpOnFailure(result, path);
        result.Success.Should().BeTrue(result.Error);
        var read = new Track(path);
        var vorbis = IsVorbis(fixture);

        read.Title.Should().Be("Get Lucky");
        read.Artist.Should().Be("Daft Punk");
        read.AlbumArtist.Should().Be("Daft Punk");
        read.Album.Should().Be("Random Access Memories");
        read.TrackNumber.Should().Be(8);
        read.TrackTotal.Should().Be(13);
        read.DiscNumber.Should().Be(1);
        read.DiscTotal.Should().Be(1);
        read.Date.Should().Be(new DateTime(2013, 5, 17));
        read.ISRC.Should().Be("USQX91300108");
        read.Genre.Should().Be("Electronic");
        read.Comment.Should().Be("Imported by Wondarr from Soulseek");
        read.Lyrics.Should().ContainSingle().Which.UnsynchronizedLyrics.Should().Contain(Lyrics);

        Additional(read, "ARTISTS").Should().Be(string.Join(';', ArtistNames));
        Additional(read, vorbis ? "MUSICBRAINZ_TRACKID" : "MusicBrainz Track Id").Should().Be(RecordingId);
        Additional(read, vorbis ? "MUSICBRAINZ_RELEASETRACKID" : "MusicBrainz Release Track Id").Should().Be(ReleaseTrackId);
        Additional(read, vorbis ? "MUSICBRAINZ_ALBUMID" : "MusicBrainz Album Id").Should().Be(ReleaseId);
        Additional(read, vorbis ? "MUSICBRAINZ_RELEASEGROUPID" : "MusicBrainz Release Group Id").Should().Be(ReleaseGroupId);
        Additional(read, vorbis ? "MUSICBRAINZ_ARTISTID" : "MusicBrainz Artist Id").Should().Be(ArtistId);
        Additional(read, vorbis ? "MUSICBRAINZ_ALBUMARTISTID" : "MusicBrainz Album Artist Id").Should().Be(AlbumArtistId);
        Additional(read, vorbis ? "RELEASETYPE" : "MusicBrainz Album Type").Should().Be("album");
        Additional(read, vorbis ? "RELEASESTATUS" : "MusicBrainz Album Status").Should().Be("official");
        Additional(read, vorbis ? "ACOUSTID_ID" : "Acoustid Id").Should().Be(AcoustId);

        // The original date has no MP4 atom; ID3v2 keeps it in TDOR and Vorbis in ORIGINALDATE.
        if (vorbis)
        {
            read.OriginalReleaseDate.Should().Be(new DateTime(2013, 4, 19));
        }
        else if (fixture.EndsWith(".mp3", StringComparison.Ordinal))
        {
            read.OriginalReleaseDate.Should().Be(new DateTime(2013, 4, 19));
        }

        // The value must land in the frame/atom §7.5 names, not merely somewhere in the file.
        var raw = Encoding.Latin1.GetString(TestMedia.Bytes(path));
        if (vorbis)
        {
            raw.Should().Contain($"MUSICBRAINZ_ALBUMID={ReleaseId}");
            raw.Should().Contain("ARTISTS=Daft Punk");
            raw.Should().Contain("ARTISTS=Nile Rodgers");
            // ATL writes the Vorbis original-date field as "ORIGINALDATE " (its own date-precision quirk).
            raw.Should().Contain("ORIGINALDATE");
            raw.Should().Contain("2013-04-19");
            // FLAC has a native PICTURE metadata block; Opus has none, so its cover goes into a
            // METADATA_BLOCK_PICTURE Vorbis comment instead.
            raw.Should().Contain(
                fixture.EndsWith(".flac", StringComparison.Ordinal) ? "image/jpeg" : "METADATA_BLOCK_PICTURE",
                "the cover is embedded per the format's picture convention");
            raw.Should().Contain("RELEASETYPE=album");
        }
        else if (fixture.EndsWith(".m4a", StringComparison.Ordinal))
        {
            raw.Should().Contain("com.apple.iTunes");
            raw.Should().Contain("MusicBrainz Album Id");
            raw.Should().Contain("covr", "the cover is embedded as the MP4 covr atom");
        }

        read.EmbeddedPictures.Should().ContainSingle();
        read.EmbeddedPictures[0].PictureData.Should().Equal(TestMedia.CoverJpeg);
        if (!fixture.EndsWith(".m4a", StringComparison.Ordinal))
        {
            // MP4's covr atom has no picture type, so ATL reports the embedded cover as Generic there.
            read.EmbeddedPictures[0].PicType.Should().Be(PictureInfo.PIC_TYPE.Front);
        }

        result.Written["Album"].Should().Be("Random Access Memories");
        result.Written["TrackNumber"].Should().Be("8");
        result.Written["MbReleaseId"].Should().Be(ReleaseId);
        result.Written["Artists"].Should().Be(string.Join(';', ArtistNames));
        result.Written["FrontCover"].Should().Be(TestMedia.CoverJpeg.Length.ToString(CultureInfo.InvariantCulture));
        result.Written.Keys.Should().NotContain("Compilation");

        // Tagging must not touch the audio itself.
        var after = TestMedia.Probe(path);
        after.DurationMs.Should().BeApproximately(before.DurationMs, 50);
        after.Bitrate.Should().Be(before.Bitrate);
    }

    [Theory]
    [InlineData("tone-320.mp3")]
    [InlineData("tone.flac")]
    [InlineData("tone-256.m4a")]
    public async Task Removes_stale_tags_from_the_source_file(string fixture)
    {
        using var media = new TestMedia();
        var path = media.Copy(fixture);
        var vorbis = IsVorbis(fixture);
        var albumIdKey = vorbis ? "MUSICBRAINZ_ALBUMID" : "MusicBrainz Album Id";

        var stale = new Track(path);
        stale.AdditionalFields[vorbis ? "REPLAYGAIN_TRACK_GAIN" : "REPLAYGAIN_TRACK_GAIN"] = "-3.40 dB";
        stale.AdditionalFields[albumIdKey] = "11111111-2222-3333-4444-555555555555";
        stale.EmbeddedPictures.Add(PictureInfo.fromBinaryData(
            TestMedia.CoverJpeg,
            PictureInfo.PIC_TYPE.Back,
            ATL.AudioData.MetaDataIOFactory.TagType.ANY,
            null,
            2));
        stale.Save().Should().BeTrue();

        (await Writer.WriteAsync(path, GetLucky, CancellationToken.None)).Success.Should().BeTrue();

        var read = new Track(path);
        Additional(read, "REPLAYGAIN_TRACK_GAIN").Should().BeNull();
        Additional(read, albumIdKey).Should().Be(ReleaseId);
        read.EmbeddedPictures.Should().ContainSingle("the stale back cover is replaced by the front cover");
        read.EmbeddedPictures[0].PictureData.Should().Equal(TestMedia.CoverJpeg);
    }

    [Theory]
    [MemberData(nameof(TestMedia.Formats), MemberType = typeof(TestMedia))]
    public async Task Sets_the_compilation_flag_per_format(string fixture)
    {
        using var media = new TestMedia();
        var path = media.Copy(fixture);

        var result = await Writer.WriteAsync(path, GetLucky with { Compilation = true }, CancellationToken.None);
        result.Success.Should().BeTrue(result.Error);

        var read = new Track(path);
        var key = IsVorbis(fixture) ? "COMPILATION" : fixture.EndsWith(".mp3", StringComparison.Ordinal) ? "TCMP" : "cpil";
        Additional(read, key).Should().Be("1");
    }

    [Fact]
    public async Task Writes_id3v24_with_the_musicbrainz_frames()
    {
        using var media = new TestMedia();
        var path = media.Copy("tone-320.mp3");

        var result = await Writer.WriteAsync(path, GetLucky, CancellationToken.None);
        result.Success.Should().BeTrue(result.Error);

        var bytes = TestMedia.Bytes(path);
        bytes.Take(5).Should().Equal((byte)'I', (byte)'D', (byte)'3', 4, 0);

        var frames = TestMedia.Id3Frames(path);
        foreach (var frame in frames)
        {
            output.WriteLine($"{frame.Id}: {frame.Text}");
        }

        frames.Should().Contain(frame => frame.Id == "TXXX" && frame.Text.Contains("MusicBrainz Album Id", StringComparison.Ordinal));
        frames.Should().Contain(frame => frame.Id == "TXXX" && frame.Text.Contains("ARTISTS", StringComparison.Ordinal));
        // ATL 7.17 cannot write a UFID frame, so the recording id lives only in its TXXX frame.
        frames.Should().Contain(frame => frame.Id == "TXXX" && frame.Text.Contains("MusicBrainz Track Id", StringComparison.Ordinal));
        frames.Should().NotContain(frame => frame.Text.Contains("TXXX:", StringComparison.Ordinal));
    }

    private void DumpOnFailure(TagWriteResult result, string path)
    {
        if (result.Success)
        {
            return;
        }

        output.WriteLine($"write failed: {result.Error}");
        output.WriteLine(Encoding.Latin1.GetString(TestMedia.Bytes(path)));
    }

    private static string? Additional(Track track, string key) =>
        track.AdditionalFields.TryGetValue(key, out var value) ? value : null;

    private static bool IsVorbis(string fixture) =>
        fixture.EndsWith(".flac", StringComparison.Ordinal) || fixture.EndsWith(".opus", StringComparison.Ordinal);
}