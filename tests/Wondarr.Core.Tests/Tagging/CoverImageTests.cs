using FluentAssertions;
using Wondarr.Core.Tagging;
using Xunit;

namespace Wondarr.Core.Tests.Tagging;

public sealed class CoverImageTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    [Fact]
    public void Returns_a_jpeg_unchanged()
    {
        var cover = TestMedia.CoverJpeg;

        CoverImage.PrepareFrontCover(cover).Should().BeSameAs(cover);
    }

    [Fact]
    public void Returns_a_png_unchanged()
    {
        var image = Png;

        CoverImage.PrepareFrontCover(image, maxEdge: 500).Should().BeSameAs(image);
    }

    [Fact]
    public void Rejects_a_webp_because_mp4_accepts_only_jpeg_and_png()
    {
        var webp = "RIFF____WEBPVP8 "u8.ToArray();

        var act = () => CoverImage.PrepareFrontCover(webp);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_an_empty_image()
    {
        var act = () => CoverImage.PrepareFrontCover([]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_a_non_positive_max_edge()
    {
        var act = () => CoverImage.PrepareFrontCover(TestMedia.CoverJpeg, maxEdge: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}