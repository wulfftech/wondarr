using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Jobs;
using Wondarr.Core.Media;
using Wondarr.Core.Metadata.CoverArt;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Organizer;
using Wondarr.Core.Paging;
using Wondarr.Core.Persistence;
using Wondarr.Core.References;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.References;

/// <summary>
/// The Match queue against a real migrated SQLite database, a real <see cref="SongService"/> and the
/// real album policy engine: substituted is only the resolver, because that is the network.
/// </summary>
public class ReferenceMatchServiceTests : IDisposable
{
    private const string Root = "/reference/music";
    private const string CoverUrl = "https://cover.example/album.jpg";

    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly IIdentityResolver _resolver = Substitute.For<IIdentityResolver>();
    private readonly ICommandQueue _commands = Substitute.For<ICommandQueue>();
    private readonly IMusicBrainzClient _musicBrainz = Substitute.For<IMusicBrainzClient>();
    private readonly ICoverArtResolver _coverArt = Substitute.For<ICoverArtResolver>();

    public ReferenceMatchServiceTests()
    {
        _coverArt.ResolveAsync(Arg.Any<CoverArtRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CoverArt(CoverUrl, "coverartarchive"));
    }

    /// <summary>Deletes this test's temp database.</summary>
    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Resolving by a candidate or an id ------------------------------------------------------

    [Fact]
    public async Task Accepting_a_candidate_rank_makes_the_file_the_songs_own_file()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Ambiguous);
        await AddCandidateAsync(context, file.Id, 1, "m-a", null);

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        var result = await Service(context).ResolveAsync(file.Id, new ReferenceResolveChoice(1, null, null, false), CancellationToken.None);

        result.State.Should().Be(ReferenceFileState.Identified);
        result.SongId.Should().NotBeNull();
        result.Message.Should().BeNull();

        var row = await RowAsync(context, file.Id);
        row.State.Should().Be(ReferenceFileState.Identified);
        row.SongId.Should().Be(result.SongId);
        row.Confidence.Should().Be(1.0);
        row.IdentifiedBy.Should().Be("manual");
        row.Message.Should().BeNull();

        var probe = Probe(248_000);
        var stored = await context.SongFiles.AsNoTracking().SingleAsync();
        stored.SongId.Should().Be(result.SongId!.Value);
        stored.Path.Should().Be(ReferenceOwnershipPath(file.RelativePath));
        stored.SourceType.Should().Be(SourceTypes.Reference);
        stored.Codec.Should().Be(probe.Codec);
        stored.Container.Should().Be(probe.Container);
        stored.BitrateKbps.Should().Be(probe.BitrateKbps);
        stored.SampleRate.Should().Be(probe.SampleRate);
        stored.BitDepth.Should().Be(probe.BitDepth);
        stored.Channels.Should().Be(probe.Channels);
        stored.DurationMs.Should().Be(probe.DurationMs);
        stored.QualityId.Should().Be(MeasuredQuality.FromMediaInfo(probe));
        stored.FingerprintVerified.Should().BeFalse();
        stored.TagsWritten.Should().BeNull();
        stored.ImportedAt.Should().Be(_timeProvider.GetUtcNow().UtcDateTime);

        (await context.MatchCandidates.CountAsync()).Should().Be(0);
        (await context.History.AsNoTracking().SingleAsync()).EventType.Should().Be(HistoryEventType.Imported);
    }

    [Fact]
    public async Task Resolving_by_a_recording_mbid_identifies_the_file()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Unmatched);

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        var result = await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, "m-a", null, false),
            CancellationToken.None);

        result.State.Should().Be(ReferenceFileState.Identified);
        (await RowAsync(context, file.Id)).IdentifiedBy.Should().Be("manual");
    }

    [Fact]
    public async Task Resolving_by_a_deezer_id_identifies_the_file()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Unmatched);

        var deezer = Identity("m-a", "Get Lucky", durationMs: 248_000) with { DeezerId = 4242 };

        _resolver.GetIdentityAsync(null, 4242, Arg.Any<CancellationToken>()).Returns(deezer);

        var result = await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, null, 4242, false),
            CancellationToken.None);

        result.State.Should().Be(ReferenceFileState.Identified);
        (await context.Songs.AsNoTracking().SingleAsync()).DeezerId.Should().Be(4242);
    }

    // --- Resolving by the release the user added on MusicBrainz (DECISIONS build session 14 #33) -----

    [Fact]
    public async Task Resolving_by_the_release_the_user_added_gives_the_file_its_only_track()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "Hype Duo/_ Yeah the Boys.mp3", ReferenceFileState.Unmatched);

        _musicBrainz.GetAddedReleaseAsync("rel-1", Arg.Any<CancellationToken>())
            .Returns(Release("rel-1", ("m-new", "Yeah the Boys")));
        _resolver.GetIdentityAsync("m-new", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-new", "Yeah the Boys", durationMs: 248_000));

        var result = await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, null, null, false, MbReleaseId: "rel-1"),
            CancellationToken.None);

        result.State.Should().Be(ReferenceFileState.Identified);

        var row = await RowAsync(context, file.Id);
        row.IdentifiedBy.Should().Be("manual");
        (await context.Songs.AsNoTracking().SingleAsync(song => song.Id == row.SongId)).MbRecordingId.Should().Be("m-new");
    }

    [Fact]
    public async Task A_release_with_several_tracks_gives_the_file_the_one_titled_as_the_file()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "Daft Punk/Get Lucky.flac", ReferenceFileState.Unmatched);

        _musicBrainz.GetAddedReleaseAsync("rel-2", Arg.Any<CancellationToken>())
            .Returns(Release("rel-2", ("m-intro", "Intro"), ("m-lucky", "Get Lucky"), ("m-outro", "Outro")));
        _resolver.GetIdentityAsync("m-lucky", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-lucky", "Get Lucky", durationMs: 248_000));

        var result = await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, null, null, false, MbReleaseId: "rel-2"),
            CancellationToken.None);

        result.State.Should().Be(ReferenceFileState.Identified);
        (await context.Songs.AsNoTracking().SingleAsync()).MbRecordingId.Should().Be("m-lucky");
    }

    [Fact]
    public async Task A_release_that_cannot_say_which_track_the_file_is_is_refused_and_the_file_waits()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "Daft Punk/Get Lucky.flac", ReferenceFileState.Unmatched);

        _musicBrainz.GetAddedReleaseAsync("rel-3", Arg.Any<CancellationToken>())
            .Returns(Release("rel-3", ("m-one", "One More Time"), ("m-two", "Aerodynamic")));

        var act = () => Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, null, null, false, MbReleaseId: "rel-3"),
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("The release has 2 tracks and none of them is titled \"Get Lucky\". Search for the recording in the drawer instead.");
        (await RowAsync(context, file.Id)).State.Should().Be(ReferenceFileState.Unmatched);
        await _resolver.DidNotReceiveWithAnyArgs().GetIdentityAsync(default, default, default);
    }

    [Theory]
    [InlineData("rel-5", "The release has 2 tracks and more than one is titled \"Get Lucky\". Search for the recording in the drawer instead.")]
    [InlineData("rel-6", "The release lists no recordings. Search for the recording in the drawer instead.")]
    public async Task A_release_with_two_tracks_of_the_files_title_or_none_at_all_is_refused(string releaseId, string message)
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "Daft Punk/Get Lucky.flac", ReferenceFileState.Unmatched);

        _musicBrainz.GetAddedReleaseAsync("rel-5", Arg.Any<CancellationToken>())
            .Returns(Release("rel-5", ("m-one", "Get Lucky"), ("m-two", "Get Lucky")));
        _musicBrainz.GetAddedReleaseAsync("rel-6", Arg.Any<CancellationToken>())
            .Returns(new MbRelease
            {
                Id = "rel-6",
                Title = "Get Lucky",
                Media = [new MbMedium { Position = 1, Tracks = [new MbTrack { Id = "t-1", Position = 1, Title = "Get Lucky" }] }],
            });

        var act = () => Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, null, null, false, MbReleaseId: releaseId),
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage(message);
        (await RowAsync(context, file.Id)).State.Should().Be(ReferenceFileState.Unmatched);
    }

    [Fact]
    public async Task A_release_MusicBrainz_does_not_show_yet_is_not_found_and_the_file_waits()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "Hype Duo/_ Yeah the Boys.mp3", ReferenceFileState.Unmatched);

        _musicBrainz.GetAddedReleaseAsync("rel-4", Arg.Any<CancellationToken>()).Returns((MbRelease?)null);

        var act = () => Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, null, null, false, MbReleaseId: "rel-4"),
            CancellationToken.None);

        (await act.Should().ThrowAsync<SongNotFoundException>()).WithMessage(ReferenceMatchService.ReleaseNotShownMessage);
        (await RowAsync(context, file.Id)).State.Should().Be(ReferenceFileState.Unmatched);
    }

    [Fact]
    public async Task A_release_id_with_another_choice_is_refused()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Unmatched);

        var act = () => Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, "m-a", null, false, MbReleaseId: "rel-1"),
            CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("Exactly one of*mbReleaseId*");
        await _musicBrainz.DidNotReceiveWithAnyArgs().GetAddedReleaseAsync(default!, default);
    }

    [Fact]
    public async Task Skipping_releases_the_reference_file_the_song_held()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/wrong.flac", ReferenceFileState.Identified);

        var songs = await NewSongService(context).AddIdentitiesAsync(
            [Identity("m-a", "Wrong Song", durationMs: 248_000)],
            new SongAddOptions(),
            CancellationToken.None);

        context.SongFiles.Add(new SongFile
        {
            SongId = songs[0].Song.Id,
            Path = ReferenceOwnershipPath(file.RelativePath),
            SourceType = SourceTypes.Reference,
            QualityId = ProbeQualityId,
        });

        file.SongId = songs[0].Song.Id;
        await AddCandidateAsync(context, file.Id, 1, "m-a", null);
        await context.SaveChangesAsync();

        var result = await Service(context).ResolveAsync(file.Id, new ReferenceResolveChoice(null, null, null, true), CancellationToken.None);

        result.State.Should().Be(ReferenceFileState.Skipped);
        result.SongId.Should().BeNull();

        var row = await RowAsync(context, file.Id);
        row.State.Should().Be(ReferenceFileState.Skipped);
        row.SongId.Should().BeNull();

        // The song is wanted again, and the candidates are no longer a question.
        (await context.SongFiles.CountAsync()).Should().Be(0);
        (await context.MatchCandidates.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Re_resolving_an_identified_file_frees_the_song_it_was()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/changed.flac", ReferenceFileState.Identified);

        var songs = await NewSongService(context).AddIdentitiesAsync(
            [Identity("m-a", "Old Song", durationMs: 248_000)],
            new SongAddOptions(),
            CancellationToken.None);

        context.SongFiles.Add(new SongFile
        {
            SongId = songs[0].Song.Id,
            Path = ReferenceOwnershipPath(file.RelativePath),
            SourceType = SourceTypes.Reference,
            QualityId = ProbeQualityId,
        });

        file.SongId = songs[0].Song.Id;
        await context.SaveChangesAsync();

        _resolver.GetIdentityAsync("m-b", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-b", "New Song", durationMs: 248_000));

        var result = await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, "m-b", null, false),
            CancellationToken.None);

        result.SongId.Should().NotBe(songs[0].Song.Id);

        var stored = await context.SongFiles.AsNoTracking().SingleAsync();
        stored.SongId.Should().Be(result.SongId!.Value);

        // The song the file used to be has no file any more, so it is wanted again.
        (await context.SongFiles.AsNoTracking().CountAsync(candidate => candidate.SongId == songs[0].Song.Id))
            .Should().Be(0);
    }

    [Fact]
    public async Task A_song_that_already_has_a_library_file_keeps_it()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Ambiguous);
        await AddCandidateAsync(context, file.Id, 1, "m-a", null);

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        var songs = await NewSongService(context).AddIdentitiesAsync(
            [Identity("m-a", "Get Lucky", durationMs: 248_000)],
            new SongAddOptions(),
            CancellationToken.None);

        var libraryPath = Path.Combine("C:", "library", "Daft Punk", "Get Lucky.flac");

        context.SongFiles.Add(new SongFile
        {
            SongId = songs[0].Song.Id,
            Path = libraryPath,
            SourceType = SourceTypes.Soulseek,
            QualityId = ProbeQualityId,
        });

        await context.SaveChangesAsync();

        var result = await Service(context).ResolveAsync(file.Id, new ReferenceResolveChoice(1, null, null, false), CancellationToken.None);

        result.State.Should().Be(ReferenceFileState.Identified);
        result.Message.Should().Be("duplicate: the song already has a file");

        var stored = await context.SongFiles.AsNoTracking().SingleAsync();
        stored.Path.Should().Be(libraryPath);
        stored.SourceType.Should().Be(SourceTypes.Soulseek);
    }

    [Fact]
    public async Task An_id_the_resolver_does_not_know_leaves_the_row_alone()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Ambiguous);
        await AddCandidateAsync(context, file.Id, 1, "m-a", null);
        await context.SaveChangesAsync();

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>()).Returns((SongIdentity?)null);

        var resolve = async () => await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(1, null, null, false),
            CancellationToken.None);

        await resolve.Should().ThrowAsync<SongNotFoundException>();

        var row = await RowAsync(context, file.Id);
        row.State.Should().Be(ReferenceFileState.Ambiguous);
        row.SongId.Should().BeNull();
        (await context.SongFiles.CountAsync()).Should().Be(0);
        (await context.MatchCandidates.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_file_that_cannot_be_resolved_yet_is_refused()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Pending);
        await context.SaveChangesAsync();

        var resolve = async () => await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(1, null, null, false),
            CancellationToken.None);

        (await resolve.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*cannot be resolved while it is pending*");
    }

    [Fact]
    public async Task A_choice_must_name_exactly_one_thing()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Ambiguous);
        await context.SaveChangesAsync();

        var service = Service(context);

        var none = async () => await service.ResolveAsync(file.Id, new ReferenceResolveChoice(null, null, null, false), CancellationToken.None);
        var both = async () => await service.ResolveAsync(file.Id, new ReferenceResolveChoice(1, null, null, true), CancellationToken.None);

        await none.Should().ThrowAsync<ArgumentException>();
        await both.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task An_unknown_reference_file_is_a_missing_row()
    {
        await using var context = await ContextAsync();
        await ReferenceLibraryAsync(context);

        var resolve = async () => await Service(context).ResolveAsync(
            987654,
            new ReferenceResolveChoice(1, null, null, false),
            CancellationToken.None);

        await resolve.Should().ThrowAsync<KeyNotFoundException>();
    }

    // --- The queue ------------------------------------------------------------------------------

    [Fact]
    public async Task The_queue_lists_the_unsettled_files_of_one_library_best_path_first()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var other = await ReferenceLibraryAsync(context, "Other", "/reference/other");

        await AddFileAsync(context, library.Id, "b/two.flac", ReferenceFileState.Ambiguous);
        await AddFileAsync(context, library.Id, "a/one.flac", ReferenceFileState.Unmatched);
        await AddFileAsync(context, library.Id, "c/three.flac", ReferenceFileState.Identified);
        await AddFileAsync(context, other.Id, "a/other.flac", ReferenceFileState.Ambiguous);
        await context.SaveChangesAsync();

        var page = await Service(context).GetQueueAsync(
            new PagingSpec(1, 20, null, descending: false),
            library.Id,
            likelyYouTubeRipsOnly: false,
            CancellationToken.None);

        page.TotalRecords.Should().Be(2);
        page.Records.Select(row => row.RelativePath).Should().Equal("a/one.flac", "b/two.flac");
    }

    [Fact]
    public async Task The_queue_carries_the_candidates_best_first()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Ambiguous);

        await AddCandidateAsync(context, file.Id, 2, "m-b", null);
        await AddCandidateAsync(context, file.Id, 1, "m-a", null);
        await context.SaveChangesAsync();

        var page = await Service(context).GetQueueAsync(
            new PagingSpec(1, 20, "state", descending: false),
            null,
            likelyYouTubeRipsOnly: false,
            CancellationToken.None);

        page.Records.Single().Candidates.Select(candidate => candidate.Rank).Should().Equal(1, 2);
    }

    // --- Bulk accept ----------------------------------------------------------------------------

    [Fact]
    public async Task Bulk_accept_adds_every_identity_in_one_call()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);

        var ids = new List<long>();

        for (var index = 0; index < 3; index++)
        {
            var id = $"m-{index}";
            var file = await AddFileAsync(context, library.Id, $"a/{index}.flac", ReferenceFileState.Ambiguous);

            await AddCandidateAsync(context, file.Id, 1, id, null);
            ids.Add(file.Id);

            _resolver.GetIdentityAsync(id, null, Arg.Any<CancellationToken>())
                .Returns(Identity(id, "T" + index, durationMs: 248_000));
        }

        await context.SaveChangesAsync();

        var real = NewSongService(context);
        var spy = Substitute.For<ISongService>();

        spy.AddIdentitiesAsync(
                Arg.Any<IReadOnlyList<SongIdentity>>(),
                Arg.Any<SongAddOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call => real.AddIdentitiesAsync(
                call.ArgAt<IReadOnlyList<SongIdentity>>(0),
                call.ArgAt<SongAddOptions>(1),
                call.ArgAt<CancellationToken>(2)));

        var result = await new ReferenceMatchService(
                context,
                _resolver,
                _musicBrainz,
                spy,
                _commands,
                _timeProvider,
                NullLogger<ReferenceMatchService>.Instance)
            .AcceptTopCandidatesAsync(ids, replace: false, CancellationToken.None);

        result.Resolved.Should().Be(3);
        result.Failed.Should().Be(0);
        result.Errors.Should().BeEmpty();

        await spy.Received(1).AddIdentitiesAsync(
            Arg.Is<IReadOnlyList<SongIdentity>>(identities => identities.Count == 3),
            Arg.Any<SongAddOptions>(),
            Arg.Any<CancellationToken>());

        (await context.MatchCandidates.CountAsync()).Should().Be(0);
        (await context.SongFiles.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Bulk_accept_reports_the_files_it_could_not_accept()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);

        var good = await AddFileAsync(context, library.Id, "a/good.flac", ReferenceFileState.Ambiguous);
        await AddCandidateAsync(context, good.Id, 1, "m-a", null);

        var noCandidate = await AddFileAsync(context, library.Id, "b/empty.flac", ReferenceFileState.Ambiguous);

        var unmatched = await AddFileAsync(context, library.Id, "c/nothing.flac", ReferenceFileState.Unmatched);

        await context.SaveChangesAsync();

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        var result = await Service(context).AcceptTopCandidatesAsync(
            [good.Id, noCandidate.Id, unmatched.Id], replace: false, CancellationToken.None);

        result.Resolved.Should().Be(1);
        result.Failed.Should().Be(2);
        result.Errors.Should().HaveCount(2);
        result.Errors.Should().Contain(error => error.StartsWith("b/empty.flac:", StringComparison.Ordinal));
        result.Errors.Should().Contain(error => error.StartsWith("c/nothing.flac:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bulk_accept_refuses_more_than_the_limit()
    {
        await using var context = await ContextAsync();
        await ReferenceLibraryAsync(context);
        await context.SaveChangesAsync();

        var ids = Enumerable.Range(1, ReferenceMatchService.MaxBulkSize + 1).Select(index => (long)index).ToList();

        var accept = async () => await Service(context).AcceptTopCandidatesAsync(ids, replace: false, CancellationToken.None);

        await accept.Should().ThrowAsync<ArgumentException>();
    }

    // --- The user's files -----------------------------------------------------------------------

    [Fact]
    public async Task Resolving_skipping_and_bulk_accepting_never_touch_the_files_on_disk()
    {
        // The library's root is a real folder, so every path the service derives is a real file.
        var root = Path.Combine(Path.GetTempPath(), "wondarr-reference", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a"));

        try
        {
            string[] names = ["a/one.flac", "a/two.flac", "a/three.flac"];
            var before = new Dictionary<string, (string Hash, DateTime Written)>();

            foreach (var name in names)
            {
                var full = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
                await File.WriteAllBytesAsync(full, [1, 2, 3, 4, (byte)name.Length]);
                before[name] = (await HashAsync(full), File.GetLastWriteTimeUtc(full));
            }

            await using var context = await ContextAsync();
            var library = await ReferenceLibraryAsync(context, rootPath: root);
            var resolved = await AddFileAsync(context, library.Id, names[0], ReferenceFileState.Ambiguous);
            var skipped = await AddFileAsync(context, library.Id, names[1], ReferenceFileState.Ambiguous);
            var bulk = await AddFileAsync(context, library.Id, names[2], ReferenceFileState.Ambiguous);
            await AddCandidateAsync(context, resolved.Id, 1, "m-a", null);
            await AddCandidateAsync(context, bulk.Id, 1, "m-b", null);
            await context.SaveChangesAsync();

            _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
                .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));
            _resolver.GetIdentityAsync("m-b", null, Arg.Any<CancellationToken>())
                .Returns(Identity("m-b", "One More Time", durationMs: 248_000));

            var service = Service(context);
            await service.ResolveAsync(resolved.Id, new ReferenceResolveChoice(1, null, null, false), CancellationToken.None);
            await service.ResolveAsync(skipped.Id, new ReferenceResolveChoice(null, null, null, true), CancellationToken.None);
            var result = await service.AcceptTopCandidatesAsync([bulk.Id], replace: false, CancellationToken.None);

            result.Resolved.Should().Be(1);
            (await context.SongFiles.AsNoTracking().CountAsync()).Should().Be(2, "the resolved and the bulk-accepted file");

            foreach (var name in names)
            {
                var full = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
                File.GetLastWriteTimeUtc(full).Should().Be(before[name].Written, name);
                (await HashAsync(full)).Should().Be(before[name].Hash, name);
            }

            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Should().HaveCount(names.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // --- Helpers --------------------------------------------------------------------------------

    /// <summary>The quality the flushed-FLAC probe measures as, which seeded quality rows cover.</summary>
    private static readonly long ProbeQualityId = MeasuredQuality.FromMediaInfo(Probe(248_000));

    // --- Replace and likely YouTube rips (DECISIONS build session 14 #32) -------------------------------

    [Fact]
    public async Task Replace_links_the_file_marks_it_and_searches_for_a_better_copy()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddRipAsync(context, library.Id, "a/Get Lucky.mp3");
        await AddCandidateAsync(context, file.Id, 1, "m-a", null);
        await context.SaveChangesAsync();

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        var result = await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(1, null, null, false, Replace: true),
            CancellationToken.None);

        result.State.Should().Be(ReferenceFileState.Identified);
        result.Replacement.Should().Be(ReferenceMatchService.SearchingMessage);

        var row = await RowAsync(context, file.Id);
        row.Replace.Should().BeTrue();

        var held = await context.SongFiles.AsNoTracking().SingleAsync(candidate => candidate.SongId == result.SongId);
        held.QualityId.Should().Be(SeedData.Opus160QualityId, "a likely rip is rated no better than a YouTube grab");

        await _commands.Received(1).EnqueueAsync(
            "UpgradeSearch",
            Arg.Is<string>(body => SongIdsOf(body).SequenceEqual(new[] { result.SongId!.Value })),
            CommandTrigger.Manual,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Replace_of_a_file_that_meets_its_cutoff_searches_nothing_and_says_so()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Ambiguous);
        await AddCandidateAsync(context, file.Id, 1, "m-a", null);
        await context.SaveChangesAsync();

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        var result = await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(1, null, null, false, Replace: true),
            CancellationToken.None);

        result.Replacement.Should().Be(ReferenceMatchService.MeetsCutoffMessage);
        _commands.ReceivedCalls().Should().BeEmpty("a FLAC meets the Standard profile's cutoff");
    }

    [Fact]
    public async Task Accept_without_replace_queues_nothing_and_leaves_the_file_owned()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddRipAsync(context, library.Id, "a/Get Lucky.mp3");
        await AddCandidateAsync(context, file.Id, 1, "m-a", null);
        await context.SaveChangesAsync();

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        var result = await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(1, null, null, false),
            CancellationToken.None);

        result.Replacement.Should().BeNull();
        (await RowAsync(context, file.Id)).Replace.Should().BeFalse();
        _commands.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Skipping_a_file_settled_with_replace_forgets_the_answer()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddRipAsync(context, library.Id, "a/Get Lucky.mp3");
        file.Replace = true;
        await context.SaveChangesAsync();

        await Service(context).ResolveAsync(file.Id, new ReferenceResolveChoice(null, null, null, Skip: true), CancellationToken.None);

        var row = await RowAsync(context, file.Id);
        row.State.Should().Be(ReferenceFileState.Skipped);
        row.Replace.Should().BeFalse("a skipped file owns no song to replace");
    }

    [Fact]
    public async Task Replace_cannot_be_combined_with_skip()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddRipAsync(context, library.Id, "a/Get Lucky.mp3");

        var resolve = async () => await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, null, null, Skip: true, Replace: true),
            CancellationToken.None);

        await resolve.Should().ThrowAsync<ArgumentException>().WithMessage("*replace*skip*");
        (await RowAsync(context, file.Id)).State.Should().Be(ReferenceFileState.Ambiguous);
    }

    [Fact]
    public async Task Bulk_replace_marks_every_file_and_searches_the_songs_below_their_cutoff()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var rip = await AddRipAsync(context, library.Id, "a/Get Lucky.mp3");
        var flac = await AddFileAsync(context, library.Id, "b/Lose Yourself to Dance.flac", ReferenceFileState.Ambiguous);
        await AddCandidateAsync(context, rip.Id, 1, "m-a", null);
        await AddCandidateAsync(context, flac.Id, 1, "m-b", null);
        await context.SaveChangesAsync();

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));
        _resolver.GetIdentityAsync("m-b", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-b", "Lose Yourself to Dance", durationMs: 248_000));

        var result = await Service(context).AcceptTopCandidatesAsync([rip.Id, flac.Id], replace: true, CancellationToken.None);

        result.Resolved.Should().Be(2);
        result.Searching.Should().Be(1, "the FLAC already meets its cutoff");
        (await RowAsync(context, rip.Id)).Replace.Should().BeTrue();
        (await RowAsync(context, flac.Id)).Replace.Should().BeTrue();

        var ripSong = (await RowAsync(context, rip.Id)).SongId!.Value;

        await _commands.Received(1).EnqueueAsync(
            "UpgradeSearch",
            Arg.Is<string>(body => SongIdsOf(body).SequenceEqual(new[] { ripSong })),
            CommandTrigger.Manual,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_queue_can_be_narrowed_to_likely_youtube_rips()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        await AddRipAsync(context, library.Id, "a/Get Lucky.mp3");
        await AddFileAsync(context, library.Id, "b/Lose Yourself to Dance.flac", ReferenceFileState.Ambiguous);

        var page = await Service(context).GetQueueAsync(
            new PagingSpec(1, 20, null, descending: false),
            null,
            likelyYouTubeRipsOnly: true,
            CancellationToken.None);

        page.TotalRecords.Should().Be(1);
        page.Records.Single().RelativePath.Should().Be("a/Get Lucky.mp3");
    }

    // --- A racing add of the same recording --------------------------------------------------------

    [Fact]
    public async Task A_racing_add_of_the_recording_links_the_file_to_the_song_that_won()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Unmatched);

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        // The racing request saves the recording after the existence check and before this one writes.
        var winnerId = 0L;
        StubCoverArtThen(async () => winnerId = await SaveWinnerAsync("Get Lucky", "m-a"));

        var result = await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, "m-a", null, false),
            CancellationToken.None);

        winnerId.Should().BeGreaterThan(0);
        result.State.Should().Be(ReferenceFileState.Identified);
        result.SongId.Should().Be(winnerId);
        context.Database.CurrentTransaction.Should().BeNull("the transaction the add left open is closed");

        // Read through a second connection: it is committed, not pending in the first one's transaction.
        await using var reader = _database.CreateContext(_timeProvider);

        var row = await reader.ReferenceFiles.AsNoTracking().SingleAsync(candidate => candidate.Id == file.Id);
        row.State.Should().Be(ReferenceFileState.Identified);
        row.SongId.Should().Be(winnerId);
        row.IdentifiedBy.Should().Be("manual");

        (await reader.Songs.AsNoTracking().SingleAsync()).Id.Should().Be(winnerId);

        var owned = await reader.SongFiles.AsNoTracking().SingleAsync();
        owned.SongId.Should().Be(winnerId);
        owned.SourceType.Should().Be(SourceTypes.Reference);
        owned.Path.Should().Be(ReferenceOwnershipPath(file.RelativePath));

        // Nothing of the lost add is left behind: one song, one artist, one album context.
        (await reader.Artists.CountAsync()).Should().Be(1);
        (await reader.AlbumContexts.CountAsync()).Should().Be(1);
        (await reader.History.AsNoTracking().SingleAsync()).EventType.Should().Be(HistoryEventType.Imported);
    }

    [Fact]
    public async Task A_racing_add_of_the_deezer_track_links_the_file_to_the_song_that_won()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Unmatched);

        _resolver.GetIdentityAsync(null, 4242, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000) with { DeezerId = 4242 });

        var winnerId = 0L;
        StubCoverArtThen(async () => winnerId = await SaveWinnerAsync("Get Lucky", null, 4242));

        var result = await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, null, 4242, false),
            CancellationToken.None);

        result.SongId.Should().Be(winnerId);
        context.Database.CurrentTransaction.Should().BeNull();

        await using var reader = _database.CreateContext(_timeProvider);
        (await reader.Songs.AsNoTracking().SingleAsync()).Id.Should().Be(winnerId);
        (await reader.SongFiles.AsNoTracking().SingleAsync()).SongId.Should().Be(winnerId);
    }

    [Fact]
    public async Task When_the_musicbrainz_id_and_the_deezer_id_belong_to_different_songs_the_musicbrainz_one_wins()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Unmatched);

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000) with { DeezerId = 4242 });

        // Two racing requests: one saved the recording by its MBID, another by the Deezer id.
        var mbSongId = 0L;
        var deezerSongId = 0L;
        StubCoverArtThen(async () =>
        {
            deezerSongId = await SaveWinnerAsync("Get Lucky (Deezer)", null, 4242);
            mbSongId = await SaveWinnerAsync("Get Lucky (MusicBrainz)", "m-a");
        });

        var result = await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, "m-a", null, false),
            CancellationToken.None);

        mbSongId.Should().NotBe(deezerSongId);
        result.SongId.Should().Be(mbSongId);
        context.Database.CurrentTransaction.Should().BeNull();

        await using var reader = _database.CreateContext(_timeProvider);
        (await reader.Songs.CountAsync()).Should().Be(2);
        (await reader.SongFiles.AsNoTracking().SingleAsync()).SongId.Should().Be(mbSongId);
    }

    [Fact]
    public async Task A_link_that_fails_after_a_racing_add_leaves_no_transaction_open_and_nothing_written()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Unmatched);

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        StubCoverArtThen(async () => await SaveWinnerAsync("Get Lucky", "m-a"));
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER refuse_file BEFORE INSERT ON song_file "
            + "BEGIN SELECT RAISE(ABORT, 'link refused'); END");

        var act = () => Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, "m-a", null, false),
            CancellationToken.None);

        (await act.Should().ThrowAsync<DbUpdateException>()).Which.GetBaseException().Message.Should().Contain("link refused");
        context.Database.CurrentTransaction.Should().BeNull();

        await using var reader = _database.CreateContext(_timeProvider);
        (await reader.ReferenceFiles.AsNoTracking().SingleAsync(candidate => candidate.Id == file.Id))
            .State.Should().Be(ReferenceFileState.Unmatched);
        (await reader.SongFiles.CountAsync()).Should().Be(0);
        (await reader.Songs.CountAsync()).Should().Be(1, "only the winner's song exists");
    }

    [Fact]
    public async Task A_winner_that_already_holds_a_file_is_reported_as_a_duplicate_like_any_other_match()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Unmatched);

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        var libraryPath = Path.Combine("C:", "library", "Daft Punk", "Get Lucky.flac");

        StubCoverArtThen(async () =>
        {
            var winnerId = await SaveWinnerAsync("Get Lucky", "m-a");

            await using var other = _database.CreateContext(_timeProvider);
            other.SongFiles.Add(new SongFile
            {
                SongId = winnerId,
                Path = libraryPath,
                SourceType = SourceTypes.Soulseek,
                QualityId = ProbeQualityId,
            });
            await other.SaveChangesAsync();
        });

        var result = await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, "m-a", null, false),
            CancellationToken.None);

        result.State.Should().Be(ReferenceFileState.Identified);
        result.Message.Should().Be("duplicate: the song already has a file");
        context.Database.CurrentTransaction.Should().BeNull();

        await using var reader = _database.CreateContext(_timeProvider);
        var stored = await reader.SongFiles.AsNoTracking().SingleAsync();
        stored.Path.Should().Be(libraryPath);
        stored.SourceType.Should().Be(SourceTypes.Soulseek);
    }

    [Fact]
    public async Task Bulk_accept_links_a_file_whose_recording_a_racing_add_saved_and_adds_the_rest()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);

        var ids = new List<long>();

        for (var index = 0; index < 3; index++)
        {
            var id = $"m-{index}";
            var file = await AddFileAsync(context, library.Id, $"a/{index}.flac", ReferenceFileState.Ambiguous);

            await AddCandidateAsync(context, file.Id, 1, id, null);
            ids.Add(file.Id);

            _resolver.GetIdentityAsync(id, null, Arg.Any<CancellationToken>())
                .Returns(Identity(id, "T" + index, durationMs: 248_000));
        }

        // The racing request saves the second recording after the existence check, before the batch writes.
        var winnerId = 0L;
        StubCoverArtThen(async () => winnerId = await SaveWinnerAsync("T1", "m-1"));

        var result = await Service(context).AcceptTopCandidatesAsync(ids, replace: false, CancellationToken.None);

        result.Errors.Should().BeEmpty();
        result.Resolved.Should().Be(3);
        result.Failed.Should().Be(0);
        winnerId.Should().BeGreaterThan(0);
        context.Database.CurrentTransaction.Should().BeNull();

        await using var reader = _database.CreateContext(_timeProvider);

        (await reader.Songs.AsNoTracking().OrderBy(song => song.MbRecordingId).Select(song => song.MbRecordingId).ToListAsync())
            .Should().Equal("m-0", "m-1", "m-2");

        var rows = await reader.ReferenceFiles.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        rows.Should().OnlyContain(row => row.State == ReferenceFileState.Identified && row.IdentifiedBy == "manual");
        rows[1].SongId.Should().Be(winnerId);

        (await reader.SongFiles.CountAsync()).Should().Be(3);
        (await reader.SongFiles.AsNoTracking().SingleAsync(file => file.SongId == winnerId)).SourceType.Should().Be(SourceTypes.Reference);
        (await reader.MatchCandidates.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Bulk_accept_still_reports_a_group_that_fails_for_another_reason()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Poison.flac", ReferenceFileState.Ambiguous);

        await AddCandidateAsync(context, file.Id, 1, "m-b", null);

        _resolver.GetIdentityAsync("m-b", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-b", "Poison", durationMs: 248_000));

        StubCoverArtThen(async () => await SaveWinnerAsync("Other", "m-b"));
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER refuse_poison BEFORE INSERT ON song WHEN NEW.title = 'Poison' "
            + "BEGIN SELECT RAISE(ABORT, 'poison refused'); END");

        var result = await Service(context).AcceptTopCandidatesAsync([file.Id], replace: false, CancellationToken.None);

        result.Resolved.Should().Be(0);
        result.Failed.Should().Be(1);
        result.Errors.Should().ContainSingle().Which.Should().Contain("a/Poison.flac").And.Contain("could not be accepted");
        context.Database.CurrentTransaction.Should().BeNull();

        await using var reader = _database.CreateContext(_timeProvider);
        (await reader.SongFiles.CountAsync()).Should().Be(0);
        (await reader.ReferenceFiles.AsNoTracking().SingleAsync()).State.Should().Be(ReferenceFileState.Ambiguous);
    }

    [Fact]
    public async Task A_save_failure_that_is_not_a_duplicate_recording_still_fails_the_match()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Poison.flac", ReferenceFileState.Unmatched);

        _resolver.GetIdentityAsync("m-b", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-b", "Poison", durationMs: 248_000));

        // A song holding the recording appears after the existence check, so a handler that took any
        // failed save for "somebody else added it" would link the file to it. This save fails for another
        // reason (a trigger aborts it before any unique index is looked at), so the match must fail.
        StubCoverArtThen(async () => await SaveWinnerAsync("Other", "m-b"));
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER refuse_poison BEFORE INSERT ON song WHEN NEW.title = 'Poison' "
            + "BEGIN SELECT RAISE(ABORT, 'poison refused'); END");

        var act = () => Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, "m-b", null, false),
            CancellationToken.None);

        (await act.Should().ThrowAsync<DbUpdateException>()).Which.GetBaseException().Message.Should().Contain("poison refused");
        context.Database.CurrentTransaction.Should().BeNull();

        await using var reader = _database.CreateContext(_timeProvider);
        (await reader.ReferenceFiles.AsNoTracking().SingleAsync(candidate => candidate.Id == file.Id))
            .State.Should().Be(ReferenceFileState.Unmatched);
        (await reader.SongFiles.CountAsync()).Should().Be(0);
        (await reader.Songs.CountAsync()).Should().Be(1, "only the other request's song exists");
    }

    // --- A racing add that creates the same new artist ----------------------------------------------

    [Fact]
    public async Task A_racing_add_of_a_song_by_the_same_new_artist_does_not_stop_a_manual_match()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Unmatched);

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        // A different song by the same new artist is saved after the artist lookup and before this write.
        var artistId = 0L;
        StubCoverArtThen(async () => artistId = await SaveWinnerArtistAsync("Instant Crush"));

        var result = await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, "m-a", null, false),
            CancellationToken.None);

        artistId.Should().BeGreaterThan(0);
        result.State.Should().Be(ReferenceFileState.Identified);
        result.SongId.Should().BeGreaterThan(0);
        context.Database.CurrentTransaction.Should().BeNull("the transaction the add left open is closed");

        // Read through a second connection: it is committed, not pending in the first one's transaction.
        await using var reader = _database.CreateContext(_timeProvider);

        await AssertOneSharedArtistAsync(reader, artistId, songs: 2);

        var row = await reader.ReferenceFiles.AsNoTracking().SingleAsync(candidate => candidate.Id == file.Id);
        row.State.Should().Be(ReferenceFileState.Identified);
        row.SongId.Should().Be(result.SongId);

        var owned = await reader.SongFiles.AsNoTracking().SingleAsync();
        owned.SongId.Should().Be(result.SongId!.Value);
        owned.SourceType.Should().Be(SourceTypes.Reference);
        (await reader.History.AsNoTracking().SingleAsync()).EventType.Should().Be(HistoryEventType.Imported);
    }

    [Fact]
    public async Task Bulk_accept_adds_songs_by_a_new_artist_a_racing_add_created_first()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);

        var ids = new List<long>();

        for (var index = 0; index < 3; index++)
        {
            var id = $"m-{index}";
            var file = await AddFileAsync(context, library.Id, $"a/{index}.flac", ReferenceFileState.Ambiguous);

            await AddCandidateAsync(context, file.Id, 1, id, null);
            ids.Add(file.Id);

            _resolver.GetIdentityAsync(id, null, Arg.Any<CancellationToken>())
                .Returns(Identity(id, "T" + index, durationMs: 248_000));
        }

        var artistId = 0L;
        StubCoverArtThen(async () => artistId = await SaveWinnerArtistAsync("Instant Crush"));

        var result = await Service(context).AcceptTopCandidatesAsync(ids, replace: false, CancellationToken.None);

        result.Errors.Should().BeEmpty();
        result.Resolved.Should().Be(3);
        result.Failed.Should().Be(0);
        artistId.Should().BeGreaterThan(0);
        context.Database.CurrentTransaction.Should().BeNull();

        await using var reader = _database.CreateContext(_timeProvider);

        await AssertOneSharedArtistAsync(reader, artistId, songs: 4);

        (await reader.ReferenceFiles.AsNoTracking().ToListAsync())
            .Should().OnlyContain(row => row.State == ReferenceFileState.Identified && row.IdentifiedBy == "manual");
        (await reader.SongFiles.CountAsync()).Should().Be(3);
        (await reader.MatchCandidates.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_manual_match_whose_new_guest_artist_is_inserted_before_the_raced_one_writes_it_once()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Unmatched);

        // The guest is listed before the main artist, so it is inserted first, in a save the raced main
        // artist then refuses: what the failed first try wrote has to be taken back with its transaction.
        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000) with
            {
                Artists =
                [
                    new IdentityArtist("Todd Edwards", "Edwards, Todd", "g1", null, ArtistRole.Featured, 1),
                    new IdentityArtist("Daft Punk", "Daft Punk", "a1", null, ArtistRole.Main, 0),
                ],
            });

        var artistId = 0L;
        StubCoverArtThen(async () => artistId = await SaveWinnerArtistAsync("Instant Crush"));

        var result = await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, "m-a", null, false),
            CancellationToken.None);

        result.State.Should().Be(ReferenceFileState.Identified);
        context.Database.CurrentTransaction.Should().BeNull();

        await using var reader = _database.CreateContext(_timeProvider);

        (await reader.Artists.AsNoTracking().OrderBy(artist => artist.MbArtistId).Select(artist => artist.MbArtistId).ToListAsync())
            .Should().Equal("a1", "g1");
        (await reader.Artists.AsNoTracking().SingleAsync(artist => artist.MbArtistId == "a1")).Id.Should().Be(artistId);
        (await reader.Songs.CountAsync()).Should().Be(2);
        (await reader.SongArtists.CountAsync()).Should().Be(3, "the winner's one credit and this song's two");
        (await reader.AlbumContexts.CountAsync()).Should().Be(2);

        var credits = await reader.SongArtists.AsNoTracking().Where(credit => credit.SongId == result.SongId).ToListAsync();
        credits.Should().HaveCount(2);
        (await reader.SongFiles.AsNoTracking().SingleAsync()).SongId.Should().Be(result.SongId!.Value);
    }

    [Fact]
    public async Task A_manual_match_of_a_recording_a_racing_add_saved_with_its_new_artist_links_the_file_to_the_winner()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Unmatched);

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        // The double click: a second request adds this very recording and its new artist after this one
        // looked for both and before it writes. The first try meets the artist index; the add made again
        // then finds the song itself.
        var artistId = 0L;
        StubCoverArtThen(async () => artistId = await SaveWinnerArtistAsync("Get Lucky", "m-a"));

        var result = await Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, "m-a", null, false),
            CancellationToken.None);

        result.State.Should().Be(ReferenceFileState.Identified);
        context.Database.CurrentTransaction.Should().BeNull();

        await using var reader = _database.CreateContext(_timeProvider);

        var winner = await reader.Songs.AsNoTracking().SingleAsync();
        result.SongId.Should().Be(winner.Id);

        await AssertOneSharedArtistAsync(reader, artistId, songs: 1);

        var row = await reader.ReferenceFiles.AsNoTracking().SingleAsync(candidate => candidate.Id == file.Id);
        row.State.Should().Be(ReferenceFileState.Identified);
        row.SongId.Should().Be(winner.Id);

        var owned = await reader.SongFiles.AsNoTracking().SingleAsync();
        owned.SongId.Should().Be(winner.Id);
        owned.SourceType.Should().Be(SourceTypes.Reference);
        (await reader.History.AsNoTracking().SingleAsync()).EventType.Should().Be(HistoryEventType.Imported);
    }

    [Fact]
    public async Task A_manual_match_whose_retry_fails_fails_as_before_and_leaves_nothing_open_or_written()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Poison.flac", ReferenceFileState.Unmatched);

        _resolver.GetIdentityAsync("m-b", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-b", "Poison", durationMs: 248_000));

        // The artist index refuses the first try; the retry reuses the winner's artist and reaches the
        // song, which this trigger refuses: the second failure is final.
        StubCoverArtThen(async () => await SaveWinnerArtistAsync("Instant Crush"));
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER refuse_poison BEFORE INSERT ON song WHEN NEW.title = 'Poison' "
            + "BEGIN SELECT RAISE(ABORT, 'poison refused'); END");

        var act = () => Service(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, "m-b", null, false),
            CancellationToken.None);

        (await act.Should().ThrowAsync<DbUpdateException>()).Which.GetBaseException().Message.Should().Contain("poison refused");
        context.Database.CurrentTransaction.Should().BeNull();

        await using var reader = _database.CreateContext(_timeProvider);

        (await reader.ReferenceFiles.AsNoTracking().SingleAsync(candidate => candidate.Id == file.Id))
            .State.Should().Be(ReferenceFileState.Unmatched);
        (await reader.SongFiles.CountAsync()).Should().Be(0);
        (await reader.Songs.CountAsync()).Should().Be(1, "only the winner's song exists");
        (await reader.Artists.CountAsync()).Should().Be(1);
        (await reader.AlbumContexts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Bulk_accept_whose_retry_fails_reports_the_group_and_leaves_nothing_open_or_written()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddFileAsync(context, library.Id, "a/Poison.flac", ReferenceFileState.Ambiguous);

        await AddCandidateAsync(context, file.Id, 1, "m-b", null);

        _resolver.GetIdentityAsync("m-b", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-b", "Poison", durationMs: 248_000));

        StubCoverArtThen(async () => await SaveWinnerArtistAsync("Instant Crush"));
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER refuse_poison BEFORE INSERT ON song WHEN NEW.title = 'Poison' "
            + "BEGIN SELECT RAISE(ABORT, 'poison refused'); END");

        var result = await Service(context).AcceptTopCandidatesAsync([file.Id], replace: false, CancellationToken.None);

        result.Resolved.Should().Be(0);
        result.Failed.Should().Be(1);
        result.Errors.Should().ContainSingle().Which.Should().Contain("a/Poison.flac").And.Contain("could not be accepted");
        context.Database.CurrentTransaction.Should().BeNull();

        await using var reader = _database.CreateContext(_timeProvider);

        (await reader.SongFiles.CountAsync()).Should().Be(0);
        (await reader.ReferenceFiles.AsNoTracking().SingleAsync()).State.Should().Be(ReferenceFileState.Ambiguous);
        (await reader.Songs.CountAsync()).Should().Be(1, "only the winner's song exists");
        (await reader.Artists.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// The shape a lost artist race must leave: the winner's artist row is the only <c>a1</c>, every song
    /// credits it, and no song is missing its credit or its album context.
    /// </summary>
    private static async Task AssertOneSharedArtistAsync(WondarrDbContext reader, long artistId, int songs)
    {
        artistId.Should().BeGreaterThan(0);

        (await reader.Artists.CountAsync()).Should().Be(1);
        (await reader.Artists.AsNoTracking().SingleAsync()).Id
            .Should().Be(artistId, "the winner's row is the artist: the loser's copy was never written");

        (await reader.Songs.CountAsync()).Should().Be(songs);
        (await reader.Songs.CountAsync(song => song.PrimaryArtistId == artistId)).Should().Be(songs);
        (await reader.SongArtists.CountAsync(credit => credit.ArtistId == artistId)).Should().Be(songs);
        (await reader.AlbumContexts.CountAsync()).Should().Be(songs);
    }

    /// <summary>
    /// Stubs the cover archive and, on its first call (which comes after the add checked what the library
    /// holds and before it writes), runs <paramref name="beforeWrite"/>: the seam a racing request slips through.
    /// </summary>
    private void StubCoverArtThen(Func<Task> beforeWrite)
    {
        var fired = false;

        _coverArt.ResolveAsync(Arg.Any<CoverArtRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (!fired)
                {
                    fired = true;
                    await beforeWrite();
                }

                return (CoverArt?)new CoverArt(CoverUrl, "coverartarchive");
            });
    }

    /// <summary>Saves a song through a second context, as a request that won the race would.</summary>
    private async Task<long> SaveWinnerAsync(string title, string? mbRecordingId = null, long? deezerId = null)
    {
        await using var other = _database.CreateContext(_timeProvider);
        var winner = await Wondarr.Core.Tests.Songs.SongSeed.AddAsync(other, title);

        await other.Songs
            .Where(song => song.Id == winner.Id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(song => song.MbRecordingId, mbRecordingId)
                .SetProperty(song => song.DeezerId, deezerId));

        return winner.Id;
    }

    /// <summary>
    /// Commits the artist Daft Punk (MusicBrainz id <c>a1</c>) and a different song by it through a second
    /// context, as a request that won the race would, and returns the artist's id.
    /// </summary>
    private async Task<long> SaveWinnerArtistAsync(string songTitle, string? mbRecordingId = null)
    {
        await using var other = _database.CreateContext(_timeProvider);
        var artist = new Artist { Name = "Daft Punk", SortName = "Daft Punk", MbArtistId = "a1" };

        other.Artists.Add(artist);
        await other.SaveChangesAsync();

        var winner = await Wondarr.Core.Tests.Songs.SongSeed.AddAsync(other, songTitle);

        if (mbRecordingId is not null)
        {
            await other.Songs
                .Where(song => song.Id == winner.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(song => song.MbRecordingId, mbRecordingId));
        }

        return artist.Id;
    }

    private static MediaInfo Probe(int durationMs) =>
        new("flac", "flac", 900, 44100, 16, 2, durationMs, true, 25_000_000);

    private static string ReferenceOwnershipPath(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string ProbeJson(int durationMs) => JsonSerializer.Serialize(Probe(durationMs), StoredJson);

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);

        return Convert.ToHexString(hash);
    }

    private static SongIdentity Identity(string mbRecordingId, string title, int? durationMs) => new()
    {
        Source = "musicbrainz",
        MbRecordingId = mbRecordingId,
        Title = title,
        ArtistCredit = "Daft Punk",
        Artists = [new IdentityArtist("Daft Punk", "Daft Punk", "a1", null, ArtistRole.Main, 0)],
        DurationMs = durationMs,
        ReleaseOptions =
        [
            new ReleaseOption
            {
                Key = "r-" + mbRecordingId,
                MbReleaseId = "r-" + mbRecordingId,
                Title = "Random Access Memories",
                AlbumArtist = "Daft Punk",
            },
        ],
    };

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_timeProvider);

        return _database.CreateContext(_timeProvider);
    }

    private static async Task<ReferenceLibrary> ReferenceLibraryAsync(
        WondarrDbContext context,
        string name = "Music",
        string rootPath = Root)
    {
        var library = new ReferenceLibrary { Name = name, RootPath = rootPath };

        context.ReferenceLibraries.Add(library);
        await context.SaveChangesAsync();

        return library;
    }

    /// <summary>A reference file row, saved at once so that a candidate can name its id.</summary>
    /// <summary>A release as MusicBrainz' lookup returns it: one digital medium with these tracks, in order.</summary>
    private static MbRelease Release(string id, params (string RecordingId, string Title)[] tracks) => new()
    {
        Id = id,
        Title = tracks[0].Title,
        Media =
        [
            new MbMedium
            {
                Position = 1,
                Format = "Digital Media",
                TrackCount = tracks.Length,
                Tracks = [.. tracks.Select((track, index) => new MbTrack
                {
                    Id = string.Concat("track-", track.RecordingId),
                    Position = index + 1,
                    Title = track.Title,
                    Recording = new MbRecording { Id = track.RecordingId, Title = track.Title },
                })],
            },
        ],
    };

    private static async Task<ReferenceFile> AddFileAsync(
        WondarrDbContext context,
        long libraryId,
        string relativePath,
        ReferenceFileState state)
    {
        var file = new ReferenceFile
        {
            ReferenceLibraryId = libraryId,
            RelativePath = relativePath,
            Size = 25_000_000,
            ModifiedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastSeenAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Probe = ProbeJson(248_000),
            State = state,
        };

        context.ReferenceFiles.Add(file);
        await context.SaveChangesAsync();

        return file;
    }

    /// <summary>
    /// An ambiguous file the scan flagged a likely YouTube rip: an MP3 at 48 kHz that measures as V0
    /// (Hip to Be Square on the owner's library).
    /// </summary>
    private static async Task<ReferenceFile> AddRipAsync(WondarrDbContext context, long libraryId, string relativePath)
    {
        var file = await AddFileAsync(context, libraryId, relativePath, ReferenceFileState.Ambiguous);

        file.Probe = JsonSerializer.Serialize(
            new MediaInfo("mp3", "mp3", 268, 48_000, null, 2, 248_000, false, 7_857_630),
            StoredJson);
        file.LikelyYouTubeRip = true;
        await context.SaveChangesAsync();

        return file;
    }

    /// <summary>The songs an <c>UpgradeSearch</c> body names.</summary>
    private static long[] SongIdsOf(string body) =>
        [.. JsonDocument.Parse(body).RootElement.GetProperty("songIds").EnumerateArray().Select(id => id.GetInt64())];

    /// <summary>A ranked candidate for a stored reference file.</summary>
    private static async Task AddCandidateAsync(
        WondarrDbContext context,
        long referenceFileId,
        int rank,
        string? mbRecordingId,
        long? deezerId)
    {
        context.MatchCandidates.Add(new MatchCandidate
        {
            ReferenceFileId = referenceFileId,
            Rank = rank,
            Identity = new MatchIdentity(
                mbRecordingId is null ? "deezer" : "musicbrainz",
                mbRecordingId,
                deezerId,
                "Get Lucky",
                "Daft Punk",
                248_000,
                "Random Access Memories").ToJson(),
            Score = 0.8,
            Reason = "search 80",
        });

        await context.SaveChangesAsync();
    }

    private static Task<ReferenceFile> RowAsync(WondarrDbContext context, long id) =>
        context.ReferenceFiles.AsNoTracking().SingleAsync(row => row.Id == id);

    private ReferenceMatchService Service(WondarrDbContext context) =>
        new(
            context,
            _resolver,
            _musicBrainz,
            NewSongService(context),
            _commands,
            _timeProvider,
            NullLogger<ReferenceMatchService>.Instance);

    private SongService NewSongService(WondarrDbContext context)
    {
        var counter = 0;

        return new SongService(
            context,
            _resolver,
            new AlbumPolicyEngine(() => new Guid(++counter, 0, 0, new byte[8])),
            _musicBrainz,
            _coverArt,
            Wondarr.Core.Tests.Songs.SearchOnAddOff.Commands,
            Wondarr.Core.Tests.Songs.SearchOnAddOff.Options,
            NullLogger<SongService>.Instance);
    }
}
