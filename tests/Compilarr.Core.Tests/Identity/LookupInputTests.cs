using Compilarr.Core.Identity;
using FluentAssertions;
using Xunit;

namespace Compilarr.Core.Tests.Identity;

/// <summary>Every shape of input the add box accepts, and what it has to be read as.</summary>
public sealed class LookupInputTests
{
    private const string BohemianRhapsodyId = "b1a9c0e9-d987-4042-ae91-78d6a3267d69";

    [Fact]
    public void A_dash_separated_line_is_an_artist_and_a_title()
    {
        var input = LookupInput.Parse("Daft Punk - Get Lucky");

        input.Kind.Should().Be(LookupKind.ArtistTitle);
        input.Artist.Should().Be("Daft Punk");
        input.Title.Should().Be("Get Lucky");
        input.Raw.Should().Be("Daft Punk - Get Lucky");
    }

    [Theory]
    [InlineData("Daft Punk – Get Lucky")]
    [InlineData("Daft Punk — Get Lucky")]
    public void An_en_or_em_dash_separates_too(string raw)
    {
        var input = LookupInput.Parse(raw);

        input.Kind.Should().Be(LookupKind.ArtistTitle);
        input.Artist.Should().Be("Daft Punk");
        input.Title.Should().Be("Get Lucky");
    }

    [Fact]
    public void The_first_separator_splits_a_title_that_holds_a_second_one()
    {
        var input = LookupInput.Parse("Daft Punk - Get Lucky - Remix");

        input.Kind.Should().Be(LookupKind.ArtistTitle);
        input.Artist.Should().Be("Daft Punk");
        input.Title.Should().Be("Get Lucky - Remix");
    }

    [Fact]
    public void A_line_with_an_empty_side_is_free_text()
    {
        var input = LookupInput.Parse("Get Lucky - ");

        input.Kind.Should().Be(LookupKind.FreeText);
        input.Artist.Should().BeNull();
        input.Title.Should().BeNull();
    }

    [Fact]
    public void Free_text_stays_free_text()
    {
        var input = LookupInput.Parse("queen bohemian rhapsody");

        input.Kind.Should().Be(LookupKind.FreeText);
        input.Raw.Should().Be("queen bohemian rhapsody");
    }

    [Fact]
    public void A_bare_guid_is_a_recording()
    {
        var input = LookupInput.Parse(BohemianRhapsodyId.ToUpperInvariant());

        input.Kind.Should().Be(LookupKind.MbRecordingId);
        input.MbRecordingId.Should().Be(BohemianRhapsodyId);
    }

    [Theory]
    [InlineData("https://musicbrainz.org/recording/b1a9c0e9-d987-4042-ae91-78d6a3267d69")]
    [InlineData("http://beta.musicbrainz.org/recording/b1a9c0e9-d987-4042-ae91-78d6a3267d69/details")]
    [InlineData("B1A9C0E9-D987-4042-AE91-78D6A3267D69")]
    public void A_recording_link_is_a_recording(string raw)
    {
        var input = LookupInput.Parse(raw);

        input.Kind.Should().Be(LookupKind.MbRecordingId);
        input.MbRecordingId.Should().Be(BohemianRhapsodyId);
    }

    [Theory]
    [InlineData("GBUM71029604")]
    [InlineData("gbum71029604")]
    [InlineData("GB-UM7-1029604")]
    public void An_isrc_is_an_isrc(string raw)
    {
        var input = LookupInput.Parse(raw);

        input.Kind.Should().Be(LookupKind.Isrc);
        input.Isrc.Should().Be("GBUM71029604");
    }

    [Fact]
    public void A_twelve_character_title_is_not_an_isrc()
    {
        var input = LookupInput.Parse("Wonderwall!");

        input.Kind.Should().Be(LookupKind.FreeText);
    }

    [Theory]
    [InlineData("https://www.deezer.com/en/track/3541552051", 3541552051L)]
    [InlineData("https://deezer.com/track/3541552051", 3541552051L)]
    [InlineData("deezer:track:3541552051", 3541552051L)]
    [InlineData("DEEZER:TRACK:123", 123L)]
    public void A_deezer_track_link_is_a_deezer_track(string raw, long expected)
    {
        var input = LookupInput.Parse(raw);

        input.Kind.Should().Be(LookupKind.DeezerTrackId);
        input.DeezerTrackId.Should().Be(expected);
    }

    [Fact]
    public void A_spotify_link_is_unsupported_with_a_reason()
    {
        var input = LookupInput.Parse("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT");

        input.Kind.Should().Be(LookupKind.Unsupported);
        input.UnsupportedReason.Should().Contain("Spotify");
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    public void A_youtube_link_is_unsupported_with_a_reason(string raw)
    {
        var input = LookupInput.Parse(raw);

        input.Kind.Should().Be(LookupKind.Unsupported);
        input.UnsupportedReason.Should().Contain("YouTube");
    }

    [Fact]
    public void Any_other_link_is_unsupported()
    {
        var input = LookupInput.Parse("https://example.com/song/42");

        input.Kind.Should().Be(LookupKind.Unsupported);
        input.UnsupportedReason.Should().Be("Unsupported link");
    }

    [Fact]
    public void An_empty_input_is_unsupported()
    {
        var input = LookupInput.Parse("   ");

        input.Kind.Should().Be(LookupKind.Unsupported);
        input.UnsupportedReason.Should().NotBeNullOrEmpty();
    }
}
