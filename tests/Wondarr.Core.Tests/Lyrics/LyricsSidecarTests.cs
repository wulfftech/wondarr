using FluentAssertions;
using Wondarr.Core.Lyrics;
using Xunit;

namespace Wondarr.Core.Tests.Lyrics;

/// <summary>Which sidecar a lookup earns, and what goes in it (LIBRARY_OUTPUT.md §7.4).</summary>
public sealed class LyricsSidecarTests
{
    private const string AudioPath = "/music/Daft Punk/Random Access Memories/08 - Get Lucky.flac";

    [Theory]
    [InlineData("plain", "synced", "/music/Daft Punk/Random Access Memories/08 - Get Lucky.lrc")]
    [InlineData(null, "synced", "/music/Daft Punk/Random Access Memories/08 - Get Lucky.lrc")]
    [InlineData("plain", null, "/music/Daft Punk/Random Access Memories/08 - Get Lucky.txt")]
    [InlineData("plain", "   ", "/music/Daft Punk/Random Access Memories/08 - Get Lucky.txt")]
    [InlineData(null, null, null)]
    [InlineData("", "", null)]
    public void The_sidecar_is_lrc_when_there_is_synced_text_and_txt_when_there_is_only_plain_text(
        string? plain,
        string? synced,
        string? expected)
    {
        var lookup = new LyricsLookup(LyricsLookupStatus.Found, plain, synced, 1);

        LyricsSidecar.PathFor(AudioPath, lookup).Should().Be(expected);
    }

    [Fact]
    public void The_content_of_an_lrc_is_the_synced_text_and_a_txt_gets_the_plain_text()
    {
        var both = new LyricsLookup(LyricsLookupStatus.Found, "plain text", "[00:01.00] synced text", 1);
        var plainOnly = new LyricsLookup(LyricsLookupStatus.Found, "plain text", null, 1);

        LyricsSidecar.ContentFor(both).Should().Be("[00:01.00] synced text\n");
        LyricsSidecar.ContentFor(plainOnly).Should().Be("plain text\n");
    }

    [Theory]
    [InlineData("one\r\ntwo\r\n", "one\ntwo\n")]
    [InlineData("one\rtwo", "one\ntwo\n")]
    [InlineData("one\ntwo", "one\ntwo\n")]
    [InlineData("one\n\n\n", "one\n")]
    [InlineData("one\ntwo\n\n", "one\ntwo\n")]
    public void The_content_has_unix_line_endings_and_exactly_one_trailing_newline(string text, string expected)
    {
        var lookup = new LyricsLookup(LyricsLookupStatus.Found, text, null, 1);

        LyricsSidecar.ContentFor(lookup).Should().Be(expected);
    }

    [Fact]
    public void A_lookup_with_no_text_at_all_has_no_content()
    {
        var lookup = new LyricsLookup(LyricsLookupStatus.NotFound, null, null, null);

        LyricsSidecar.ContentFor(lookup).Should().BeEmpty();
    }
}
