using System.Text;
using ATL;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Tagging;
using Xunit;

namespace Wondarr.Core.Tests.Tagging;

/// <summary>The ReplayGain track tags: their text, their keys per format, and the in-place update.</summary>
public sealed class TagWriterReplayGainTests
{
    private static TagWriter Writer => new(NullLogger<TagWriter>.Instance);

    private static TagSet Basic => new()
    {
        Title = "Get Lucky",
        Artist = "Daft Punk",
        AlbumArtist = "Daft Punk",
        Album = "Random Access Memories",
        TrackNumber = 8,
        Date = "2013-05-17",
        FrontCover = TestMedia.CoverJpeg,
        Lyrics = "Like the legend of the phoenix",
    };

    [Theory]
    [MemberData(nameof(TestMedia.Formats), MemberType = typeof(TestMedia))]
    public async Task Writes_the_track_gain_and_peak_and_reads_them_back(string fixture)
    {
        using var media = new TestMedia();
        var path = media.Copy(fixture);

        var result = await Writer.WriteAsync(
            path,
            Basic with { ReplayGainTrackGainDb = -8.52, ReplayGainTrackPeak = 1.047129 },
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.Written["ReplayGainTrackGain"].Should().Be("-8.52 dB");
        result.Written["ReplayGainTrackPeak"].Should().Be("1.047129");
        AssertKeys(path, fixture, "-8.52 dB", "1.047129");
    }

    [Theory]
    [MemberData(nameof(TestMedia.Formats), MemberType = typeof(TestMedia))]
    public async Task A_positive_gain_carries_its_sign(string fixture)
    {
        using var media = new TestMedia();
        var path = media.Copy(fixture);

        var result = await Writer.WriteAsync(
            path,
            Basic with { ReplayGainTrackGainDb = 1.3, ReplayGainTrackPeak = 0.5 },
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        AssertKeys(path, fixture, "+1.30 dB", "0.500000");
    }

    [Theory]
    [MemberData(nameof(TestMedia.Formats), MemberType = typeof(TestMedia))]
    public async Task Without_values_no_replaygain_field_is_written(string fixture)
    {
        using var media = new TestMedia();
        var path = media.Copy(fixture);

        var result = await Writer.WriteAsync(path, Basic, CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.Written.Keys.Should().NotContain(key => key.StartsWith("ReplayGain", StringComparison.Ordinal));
        Encoding.Latin1.GetString(TestMedia.Bytes(path)).Should().NotContain("REPLAYGAIN");
    }

    [Theory]
    [MemberData(nameof(TestMedia.Formats), MemberType = typeof(TestMedia))]
    public async Task Updating_in_place_adds_the_two_tags_and_keeps_everything_else(string fixture)
    {
        using var media = new TestMedia();
        var path = media.Copy(fixture);
        (await Writer.WriteAsync(path, Basic, CancellationToken.None)).Success.Should().BeTrue();

        var result = await Writer.WriteReplayGainAsync(path, -8.52, 1.047129, CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        AssertKeys(path, fixture, "-8.52 dB", "1.047129");
        var read = new Track(path);
        read.Title.Should().Be("Get Lucky");
        read.Artist.Should().Be("Daft Punk");
        read.Album.Should().Be("Random Access Memories");
        read.TrackNumber.Should().Be(8);
        read.EmbeddedPictures.Should().HaveCount(1, "the cover survives");
        read.Lyrics.Should().ContainSingle().Which.UnsynchronizedLyrics.Should().Contain("phoenix");
        media.TempFilesLeftBehind().Should().BeEmpty();
    }

    [Fact]
    public async Task Updating_a_file_that_is_not_audio_fails_and_leaves_it_alone()
    {
        using var media = new TestMedia();
        var path = Path.Combine(media.TempDirectory, "noise.mp3");
        await File.WriteAllBytesAsync(path, new byte[256]);

        var result = await Writer.WriteReplayGainAsync(path, -1, 1, CancellationToken.None);

        result.Success.Should().BeFalse();
        TestMedia.Bytes(path).Should().Equal(new byte[256]);
        media.TempFilesLeftBehind().Should().BeEmpty();
    }

    private static void AssertKeys(string path, string fixture, string gain, string peak)
    {
        var read = new Track(path);

        read.AdditionalFields.Should().Contain("REPLAYGAIN_TRACK_GAIN", gain);
        read.AdditionalFields.Should().Contain("REPLAYGAIN_TRACK_PEAK", peak);

        var raw = Encoding.Latin1.GetString(TestMedia.Bytes(path));

        if (fixture.EndsWith(".mp3", StringComparison.Ordinal))
        {
            // ID3v2: a TXXX frame whose description is the key.
            var frames = TestMedia.Id3Frames(path);
            frames.Should().Contain(frame => frame.Id == "TXXX" && frame.Text.Contains("REPLAYGAIN_TRACK_GAIN", StringComparison.Ordinal) && frame.Text.Contains(gain, StringComparison.Ordinal));
            frames.Should().Contain(frame => frame.Id == "TXXX" && frame.Text.Contains("REPLAYGAIN_TRACK_PEAK", StringComparison.Ordinal) && frame.Text.Contains(peak, StringComparison.Ordinal));
        }
        else if (fixture.EndsWith(".m4a", StringComparison.Ordinal))
        {
            // MP4: the freeform atom ----:com.apple.iTunes:<name>.
            raw.Should().Contain("com.apple.iTunes").And.Contain("REPLAYGAIN_TRACK_GAIN").And.Contain("REPLAYGAIN_TRACK_PEAK");
            raw.Should().Contain(gain).And.Contain(peak);
        }
        else
        {
            // Vorbis comments (FLAC, Opus): KEY=value.
            raw.Should().Contain("REPLAYGAIN_TRACK_GAIN=" + gain).And.Contain("REPLAYGAIN_TRACK_PEAK=" + peak);
        }
    }
}
