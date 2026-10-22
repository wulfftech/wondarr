using Wondarr.Sources.YouTube;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// Reading the video id out of a pasted link (DECISIONS build session 14 #33): every shape YouTube shares,
/// and nothing else, so yt-dlp is never pointed at a page that is not a YouTube video.
/// </summary>
public class YouTubeVideoLinksTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=aBcDeFgHiJk")]
    [InlineData("https://www.youtube.com/watch?v=aBcDeFgHiJk&t=42s&list=PL123")]
    [InlineData("https://www.youtube.com/watch?feature=share&v=aBcDeFgHiJk")]
    [InlineData("http://youtube.com/watch?v=aBcDeFgHiJk")]
    [InlineData("https://m.youtube.com/watch?v=aBcDeFgHiJk")]
    [InlineData("https://music.youtube.com/watch?v=aBcDeFgHiJk")]
    [InlineData("https://youtu.be/aBcDeFgHiJk")]
    [InlineData("https://youtu.be/aBcDeFgHiJk?si=tracking")]
    [InlineData("https://www.youtube.com/shorts/aBcDeFgHiJk")]
    [InlineData("https://www.youtube.com/live/aBcDeFgHiJk")]
    [InlineData("https://www.youtube.com/embed/aBcDeFgHiJk")]
    [InlineData("www.youtube.com/watch?v=aBcDeFgHiJk")]
    [InlineData("  youtu.be/aBcDeFgHiJk  ")]
    public void Reads_the_video_id_from_every_link_youtube_shares(string link)
    {
        YouTubeVideoLinks.TryParse(link, out var videoId).Should().BeTrue();
        videoId.Should().Be("aBcDeFgHiJk");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("aBcDeFgHiJk")]
    [InlineData("https://www.youtube.com/@HypeDuo")]
    [InlineData("https://www.youtube.com/playlist?list=PL123")]
    [InlineData("https://www.youtube.com/watch")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://www.youtube.com/watch?v=aBcDeFgHiJk%22")]
    [InlineData("https://youtu.be/aBcDeFgHiJk/extra")]
    [InlineData("https://www.youtube.com.evil.example/watch?v=aBcDeFgHiJk")]
    [InlineData("https://evil.example/watch?v=aBcDeFgHiJk")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://youtube.com/watch?v=aBcDeFgHiJk")]
    [InlineData("https://soundcloud.com/hype-duo/yeah-the-boys")]
    public void Anything_that_is_not_one_youtube_video_is_refused(string? link)
    {
        YouTubeVideoLinks.TryParse(link, out var videoId).Should().BeFalse();
        videoId.Should().BeEmpty();
    }

    [Fact]
    public void The_watch_link_is_the_one_musicbrainz_expects()
    {
        YouTubeVideoLinks.WatchUrl("aBcDeFgHiJk").Should().Be("https://www.youtube.com/watch?v=aBcDeFgHiJk");
    }
}
