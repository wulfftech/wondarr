using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Metadata.MusicBrainz;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Identity;

/// <summary>
/// Scores clamp at 100, so ties between recordings are common. A tie goes to the recording with a
/// first-release date (the album one), never to an undated snippet; this is the order the UI ranking
/// already uses.
/// </summary>
public sealed class IdentityResolverTieBreakTests
{
    private const string UndatedId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string DatedId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string Isrc = "USRC17607839";

    private readonly IMusicBrainzClient _musicBrainz = Substitute.For<IMusicBrainzClient>();
    private readonly IDeezerClient _deezer = Substitute.For<IDeezerClient>();

    [Fact]
    public async Task A_bare_isrc_with_an_undated_and_a_dated_recording_picks_the_dated_one()
    {
        // The undated recording is listed first, which is what an ascending sort on "" used to prefer.
        Serve(Recording(UndatedId, null), Recording(DatedId, "2013-05-17"));

        var result = await NewResolver().ResolveAsync(Isrc);

        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(DatedId);
    }

    [Fact]
    public async Task A_bridged_deezer_track_with_a_tie_picks_the_dated_recording()
    {
        Serve(Recording(UndatedId, null), Recording(DatedId, "2013-05-17"));
        _deezer.GetTrackAsync(67238735, Arg.Any<CancellationToken>()).Returns(new DeezerTrack
        {
            Id = 67238735,
            Title = "Get Lucky",
            TitleShort = "Get Lucky",
            Isrc = Isrc,
            Duration = 369,
            Artist = new DeezerArtist { Id = 27, Name = "Daft Punk" },
            Album = new DeezerAlbumRef { Id = 6575789, Title = "Random Access Memories" },
            Contributors = [new DeezerArtist { Id = 27, Name = "Daft Punk" }],
        });

        var result = await NewResolver().ResolveAsync("https://www.deezer.com/track/67238735");

        result.Status.Should().Be(ResolveStatus.Resolved);
        result.Identity!.MbRecordingId.Should().Be(DatedId);
    }

    /// <summary>Serves the given recordings for the ISRC, and each one again for its own lookup.</summary>
    private void Serve(params MbRecording[] recordings)
    {
        _musicBrainz.GetRecordingsByIsrcAsync(Isrc, Arg.Any<CancellationToken>()).Returns(recordings);

        foreach (var recording in recordings)
        {
            _musicBrainz.GetRecordingAsync(recording.Id, Arg.Any<CancellationToken>()).Returns(recording);
        }

        _musicBrainz.GetReleasesForRecordingAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
    }

    private IdentityResolver NewResolver() =>
        new(_musicBrainz, _deezer, Options.Create(new MetadataOptions()), NullLogger<IdentityResolver>.Instance);

    private static MbRecording Recording(string id, string? firstReleaseDate) => new()
    {
        Id = id,
        Title = "Get Lucky",
        Length = 369_000,
        FirstReleaseDate = firstReleaseDate,
        ArtistCredit = [new MbArtistCredit { Name = "Daft Punk", Artist = new MbArtist { Id = "cc", Name = "Daft Punk" } }],
    };
}
