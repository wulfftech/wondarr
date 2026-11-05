using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Metadata.ITunes;
using Wondarr.Core.Metadata.MusicBrainz;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Identity;

/// <summary>
/// An ISRC Deezer stores in a shape MusicBrainz would refuse is normalised before the bridge, or the
/// bridge is skipped: it never throws out of the resolver and takes a whole list run with it.
/// </summary>
public sealed class IdentityResolverIsrcValidationTests
{
    private const string Hyphenated = "FR-Z03-14-00030";
    private const string Canonical = "FRZ031400030";
    private const string TooShort = "FRZ03140003";

    private readonly IMusicBrainzClient _musicBrainz = Substitute.For<IMusicBrainzClient>();
    private readonly IDeezerClient _deezer = Substitute.For<IDeezerClient>();
    private readonly IITunesClient _iTunes = Substitute.For<IITunesClient>();

    public IdentityResolverIsrcValidationTests()
    {
        // The real client refuses anything but a canonical ISRC.
        _musicBrainz
            .GetRecordingsByIsrcAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<MbRecording>>(call => call.Arg<string>() == Canonical
                ? []
                : throw new ArgumentException("not an ISRC"));
        _musicBrainz
            .SearchRecordingsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MbRecordingSearchResult());
        _iTunes
            .SearchSongsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
    }

    [Fact]
    public async Task A_hyphenated_deezer_isrc_reaches_musicbrainz_in_its_normalised_form_from_a_text_line()
    {
        ServeSearch(Hyphenated);

        var result = await NewResolver().ResolveAsync("Daft Punk - Get Lucky");

        result.Should().NotBeNull();
        await _musicBrainz.Received().GetRecordingsByIsrcAsync(Canonical, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_eleven_character_deezer_isrc_skips_the_bridge_from_a_text_line()
    {
        ServeSearch(TooShort);

        var act = () => NewResolver().ResolveAsync("Daft Punk - Get Lucky");

        await act.Should().NotThrowAsync();
        await _musicBrainz.DidNotReceive().GetRecordingsByIsrcAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_hyphenated_deezer_isrc_reaches_musicbrainz_in_its_normalised_form_from_a_track_link()
    {
        _deezer.GetTrackAsync(67238735, Arg.Any<CancellationToken>()).Returns(Track(Hyphenated));

        var result = await NewResolver().ResolveAsync("https://www.deezer.com/track/67238735");

        result.Should().NotBeNull();
        await _musicBrainz.Received().GetRecordingsByIsrcAsync(Canonical, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_eleven_character_deezer_isrc_skips_the_bridge_from_a_track_link()
    {
        _deezer.GetTrackAsync(67238735, Arg.Any<CancellationToken>()).Returns(Track(TooShort));

        var act = () => NewResolver().ResolveAsync("https://www.deezer.com/track/67238735");

        await act.Should().NotThrowAsync();
        await _musicBrainz.DidNotReceive().GetRecordingsByIsrcAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_all_zero_id_is_unsupported_and_never_reaches_musicbrainz()
    {
        var result = await NewResolver().ResolveAsync("00000000-0000-0000-0000-000000000000");

        result.Status.Should().Be(ResolveStatus.Unsupported);
        await _musicBrainz.DidNotReceiveWithAnyArgs().GetRecordingAsync(default!, default);
    }

    private void ServeSearch(string isrc) =>
        _deezer
            .SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DeezerSearchResult { Data = [Track(isrc)], Total = 1 });

    private IdentityResolver NewResolver() =>
        new(_musicBrainz, _deezer, _iTunes, Options.Create(new MetadataOptions()), NullLogger<IdentityResolver>.Instance);

    private static DeezerTrack Track(string isrc) => new()
    {
        Id = 67238735,
        Title = "Get Lucky",
        TitleShort = "Get Lucky",
        Isrc = isrc,
        Duration = 369,
        Artist = new DeezerArtist { Id = 27, Name = "Daft Punk" },
        Album = new DeezerAlbumRef { Id = 6575789, Title = "Random Access Memories" },
        Contributors = [new DeezerArtist { Id = 27, Name = "Daft Punk" }],
    };
}
