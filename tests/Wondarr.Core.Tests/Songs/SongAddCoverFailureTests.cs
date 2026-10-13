using System.Net;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.CoverArt;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Metadata.ITunes;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Organizer;
using Wondarr.Core.Songs;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Songs;

/// <summary>
/// R-030: a cover host that cannot be reached is a missing cover, never a failed add. The real cover
/// resolver sits under the real song service; only the three cover hosts' clients are fakes, and every
/// one of them fails.
/// </summary>
public sealed class SongAddCoverFailureTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _timeProvider = new();

    [Fact]
    public async Task A_song_add_goes_ahead_without_a_cover_when_every_cover_host_fails()
    {
        await _database.MigrateAsync(_timeProvider);
        await using var context = _database.CreateContext(_timeProvider);

        var coverArtArchive = Substitute.For<ICoverArtArchiveClient>();
        coverArtArchive.GetReleaseGroupFrontUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<string?>(_ => throw new HttpRequestException("Name or service not known"));
        coverArtArchive.GetReleaseFrontUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<string?>(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var deezer = Substitute.For<IDeezerClient>();
        deezer.GetAlbumAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<DeezerAlbum?>(_ => throw new System.Text.Json.JsonException("'<' is an invalid start of a value."));
        deezer.SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<DeezerSearchResult>(_ => throw new MetadataProviderException("deezer", HttpStatusCode.BadGateway, "Deezer answered 502."));

        var itunes = Substitute.For<IITunesClient>();
        itunes.SearchSongsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ITunesTrack>>(_ => throw new HttpRequestException("Connection refused"));

        var covers = new CoverArtResolver(coverArtArchive, deezer, itunes, NullLogger<CoverArtResolver>.Instance);

        var counter = 0;
        var service = new SongService(
            context,
            Substitute.For<IIdentityResolver>(),
            new AlbumPolicyEngine(() => new Guid(++counter, 0, 0, new byte[8])),
            Substitute.For<IMusicBrainzClient>(),
            covers,
            SearchOnAddOff.Commands,
            SearchOnAddOff.Options,
            NullLogger<SongService>.Instance);

        var results = await service.AddIdentitiesAsync(
            [Identity("m-a", "Get Lucky"), Identity("m-b", "Instant Crush")],
            new SongAddOptions(),
            CancellationToken.None);

        results.Should().AllSatisfy(result => result.Outcome.Should().Be(SongAddOutcome.Added));
        (await context.Songs.CountAsync()).Should().Be(2);
    }

    public void Dispose() => _database.Dispose();

    private static SongIdentity Identity(string mbRecordingId, string title) => new()
    {
        Source = "musicbrainz",
        MbRecordingId = mbRecordingId,
        Title = title,
        ArtistCredit = "Daft Punk",
        Artists = [new IdentityArtist("Daft Punk", "Daft Punk", "a1", null, ArtistRole.Main, 0)],
        OriginalDate = "2013",
        ReleaseOptions =
        [
            new ReleaseOption
            {
                Key = "r1",
                MbReleaseId = "r1",
                MbReleaseGroupId = "r1-group",
                Title = "Random Access Memories",
                AlbumArtist = "Daft Punk",
                PrimaryType = "Album",
                Status = "Official",
                Date = "2013-05-17",
            },
        ],
    };
}
