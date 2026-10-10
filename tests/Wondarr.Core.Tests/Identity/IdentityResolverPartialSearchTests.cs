using System.Net;
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
/// The add-by-search lookup keeps working when one of its two providers is busy: the other one's
/// candidates are returned with the failed provider named, and only both failing is an error.
/// </summary>
public sealed class IdentityResolverPartialSearchTests
{
    private readonly IMusicBrainzClient _musicBrainz = Substitute.For<IMusicBrainzClient>();
    private readonly IDeezerClient _deezer = Substitute.For<IDeezerClient>();

    [Fact]
    public async Task A_busy_musicbrainz_leaves_deezers_candidates_and_names_musicbrainz()
    {
        _deezer
            .SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DeezerSearchResult { Data = [GetLucky()], Total = 1 });
        _musicBrainz
            .SearchRecordingsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<MbRecordingSearchResult>(_ => throw Busy("musicbrainz"));

        var found = await NewResolver().SearchPartialAsync("Daft Punk - Get Lucky", 10);

        found.FailedProviders.Should().Equal("musicbrainz");
        found.Items.Should().NotBeEmpty();
        found.Items.Should().OnlyContain(candidate => candidate.DeezerId == 67238735);
        found.Items.Should().OnlyContain(candidate => candidate.MbRecordingId == null);

        // Once MusicBrainz has failed the search does not queue a second request behind it.
        await _musicBrainz.Received(1)
            .SearchRecordingsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_provider_answering_html_is_a_failed_provider_not_a_crash()
    {
        _deezer
            .SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DeezerSearchResult { Data = [GetLucky()], Total = 1 });
        _musicBrainz
            .SearchRecordingsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<MbRecordingSearchResult>(_ => throw new System.Text.Json.JsonException("'<' is an invalid start of a value."));

        var found = await NewResolver().SearchPartialAsync("Daft Punk - Get Lucky", 10);

        found.FailedProviders.Should().Equal("musicbrainz");
        found.Items.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_busy_deezer_still_searches_musicbrainz_for_an_artist_and_title_line()
    {
        _deezer
            .SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<DeezerSearchResult>(_ => throw Busy("deezer"));
        _musicBrainz
            .SearchRecordingsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MbRecordingSearchResult());

        var found = await NewResolver().SearchPartialAsync("Daft Punk - Get Lucky", 10);

        found.FailedProviders.Should().Equal("deezer");
        await _musicBrainz.Received().SearchRecordingsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Both_providers_failing_is_an_unavailable_search_not_an_empty_one()
    {
        _deezer
            .SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<DeezerSearchResult>(_ => throw Busy("deezer"));
        _musicBrainz
            .SearchRecordingsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<MbRecordingSearchResult>(_ => throw Busy("musicbrainz"));

        var search = () => NewResolver().SearchPartialAsync("Daft Punk - Get Lucky", 10);

        var thrown = await search.Should().ThrowAsync<ProvidersUnavailableException>();
        thrown.Which.Providers.Should().BeEquivalentTo(["deezer", "musicbrainz"]);
    }

    [Fact]
    public async Task The_plain_search_still_surfaces_a_provider_failure()
    {
        _deezer
            .SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DeezerSearchResult { Data = [GetLucky()], Total = 1 });
        _musicBrainz
            .SearchRecordingsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<MbRecordingSearchResult>(_ => throw Busy("musicbrainz"));

        var search = () => NewResolver().SearchAsync("Daft Punk - Get Lucky", 10);

        await search.Should().ThrowAsync<MetadataProviderException>();
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_a_failed_provider()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        _deezer
            .SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<DeezerSearchResult>(_ => throw new TaskCanceledException());

        var search = () => NewResolver().SearchPartialAsync("Daft Punk - Get Lucky", 10, cancelled.Token);

        await search.Should().ThrowAsync<TaskCanceledException>();
    }

    private IdentityResolver NewResolver() =>
        new(_musicBrainz, _deezer, Options.Create(new MetadataOptions()), NullLogger<IdentityResolver>.Instance);

    private static MetadataProviderException Busy(string provider) =>
        new(provider, HttpStatusCode.ServiceUnavailable, $"{provider} answered 503 for a GET request.");

    private static DeezerTrack GetLucky() => new()
    {
        Id = 67238735,
        Title = "Get Lucky",
        TitleShort = "Get Lucky",
        Duration = 369,
        Artist = new DeezerArtist { Id = 27, Name = "Daft Punk" },
        Album = new DeezerAlbumRef { Id = 6575789, Title = "Random Access Memories" },
        Contributors = [new DeezerArtist { Id = 27, Name = "Daft Punk" }],
    };
}
