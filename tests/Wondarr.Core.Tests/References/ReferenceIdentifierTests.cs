using System.Text.Json;
using System.Text.Json.Serialization;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Media;
using Wondarr.Core.Metadata.AcoustId;
using Wondarr.Core.Metadata.CoverArt;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.References;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Wondarr.Core.Tagging;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.References;

/// <summary>
/// The identification pipeline against a real migrated SQLite database, a real <see cref="SongService"/>
/// and the real album policy engine: substituted are only the resolver, the fingerprinter and the
/// AcoustID client, because those are the network.
/// </summary>
public class ReferenceIdentifierTests : IDisposable
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
    private readonly IFingerprinter _fingerprinter = Substitute.For<IFingerprinter>();
    private readonly IAcoustIdClient _acoustId = Substitute.For<IAcoustIdClient>();
    private readonly IMusicBrainzClient _musicBrainz = Substitute.For<IMusicBrainzClient>();
    private readonly ICoverArtResolver _coverArt = Substitute.For<ICoverArtResolver>();
    private readonly ReferenceOptions _options = new();

    public ReferenceIdentifierTests()
    {
        // Anything not stubbed by a test finds nothing, which is what most tiers should do.
        _resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolveResult { Status = ResolveStatus.Unresolved });

        _acoustId.LookupAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new AcoustIdLookupResult(AcoustIdStatus.NotConfigured, [], null));

        _coverArt.ResolveAsync(Arg.Any<CoverArtRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CoverArt(CoverUrl, "coverartarchive"));

        // Not every tier reaches the fingerprinter; a test that needs it stubs its own answer.
        _fingerprinter.FingerprintAsync(
                Arg.Any<string>(),
                Arg.Any<FingerprintWindow>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new FingerprintResult(false, null, 0, FingerprintWindow.Start, "no fingerprinter here"));
    }

    // --- Tiers ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_tagged_recording_mbid_becomes_an_owned_song_filed_at_the_file()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "Daft Punk/Random Access Memories/08 - Get Lucky.flac", 248_000,
            Tags(title: "Get Lucky", artist: "Daft Punk", mbid: "m-a"));

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_500));

        var result = await RunAsync(context, library.Id);

        result.Should().Be(new ReferenceIdentifyResult(1, 0, 0, 0));

        var row = await RowAsync(context, file.Id);
        row.State.Should().Be(ReferenceFileState.Identified);
        row.Confidence.Should().Be(1.0);
        row.IdentifiedBy.Should().Be("tag_mbid");
        row.Message.Should().BeNull();
        row.SongId.Should().NotBeNull();

        var stored = await context.SongFiles.AsNoTracking().SingleAsync(candidate => candidate.SongId == row.SongId);
        var probe = Probe(248_000);

        stored.Path.Should().Be(ReferenceOwnershipPath(file.RelativePath));
        stored.Size.Should().Be(file.Size);
        stored.Codec.Should().Be(probe.Codec);
        stored.Container.Should().Be(probe.Container);
        stored.BitrateKbps.Should().Be(probe.BitrateKbps);
        stored.SampleRate.Should().Be(probe.SampleRate);
        stored.BitDepth.Should().Be(probe.BitDepth);
        stored.Channels.Should().Be(probe.Channels);
        stored.DurationMs.Should().Be(probe.DurationMs);
        stored.QualityId.Should().Be(MeasuredQuality.FromMediaInfo(probe));
        stored.SourceType.Should().Be(SourceTypes.Reference);
        stored.FingerprintVerified.Should().BeFalse();
        stored.TagsWritten.Should().BeNull();
        stored.ImportedAt.Should().Be(_timeProvider.GetUtcNow().UtcDateTime);
        stored.SourceRef.Should().Contain("\"referenceLibraryId\"").And.Contain($"\"referenceFileId\":{file.Id}");

        var history = await context.History.AsNoTracking().SingleAsync();
        history.EventType.Should().Be(HistoryEventType.Imported);
        history.SongId.Should().Be(row.SongId!.Value);
        history.Data.Should().Contain($"\"referenceFileId\":{file.Id}");

        // The user's file is untouched: no tag snapshot, and nothing was written to it.
        (await context.MatchCandidates.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_tagged_mbid_of_the_wrong_length_is_kept_as_a_weak_candidate()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "a/Get Lucky.flac", 200_000,
            Tags(title: "Get Lucky", artist: "Daft Punk", mbid: "m-a"));

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 300_000));

        var result = await RunAsync(context, library.Id);

        result.Should().Be(new ReferenceIdentifyResult(0, 1, 0, 0));

        var row = await RowAsync(context, file.Id);
        row.State.Should().Be(ReferenceFileState.Ambiguous);
        row.SongId.Should().BeNull();

        var candidate = await context.MatchCandidates.AsNoTracking().SingleAsync();
        candidate.Rank.Should().Be(1);
        candidate.Score.Should().Be(0.5);
        candidate.Reason.Should().Be("tagged MBID, length differs by 100 s");
        candidate.Identity.Should().Contain("\"mbRecordingId\":\"m-a\"").And.NotContain("null");
    }

    [Fact]
    public async Task An_isrc_tag_that_bridges_to_one_recording_is_accepted_at_0_95()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "a/b/Get Lucky.flac", 248_000,
            Tags(title: "Get Lucky", artist: "Daft Punk", isrc: "GBDUW0000059"));

        _resolver.ResolveAsync("GBDUW0000059", Arg.Any<CancellationToken>())
            .Returns(new ResolveResult
            {
                Status = ResolveStatus.Resolved,
                Identity = Identity("m-a", "Get Lucky", durationMs: 249_000),
            });

        var result = await RunAsync(context, library.Id);

        result.Identified.Should().Be(1);

        var row = await RowAsync(context, file.Id);
        row.IdentifiedBy.Should().Be("isrc");
        row.Confidence.Should().Be(0.95);
    }

    [Fact]
    public async Task A_fingerprint_the_tags_do_not_confirm_is_capped_at_0_89_and_goes_to_the_queue()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "a/whatever.flac", 248_000,
            Tags(title: "Something Else", artist: "Another Band"));

        StubFingerprint();
        StubLookup(new AcoustIdResult("ac-1", 0.97, [Recording("m-a", "Get Lucky", 248, "Daft Punk")]));

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        var result = await RunAsync(context, library.Id);

        result.Should().Be(new ReferenceIdentifyResult(0, 1, 0, 0));

        var row = await RowAsync(context, file.Id);
        row.State.Should().Be(ReferenceFileState.Ambiguous);
        row.Confidence.Should().Be(0.89);
        row.Fingerprint.Should().Be("FP");
        row.AcoustId.Should().Be("ac-1");

        var candidate = await context.MatchCandidates.AsNoTracking().SingleAsync();
        candidate.Score.Should().Be(0.89);
        candidate.Reason.Should().Be("AcoustID 0.89");
    }

    [Fact]
    public async Task A_fingerprint_the_tags_confirm_keeps_its_own_score()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "a/Get Lucky.flac", 248_000,
            Tags(title: "Get Lucky", artist: "Daft Punk feat. Pharrell Williams"));

        StubFingerprint();
        StubLookup(new AcoustIdResult(
            "ac-1",
            0.93,
            [Recording("m-a", "Get Lucky", 248, "Daft Punk", "Pharrell Williams")]));

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        var result = await RunAsync(context, library.Id);

        result.Identified.Should().Be(1);

        var row = await RowAsync(context, file.Id);
        row.IdentifiedBy.Should().Be("acoustid");
        row.Confidence.Should().Be(0.93);
        row.AcoustId.Should().Be("ac-1");

        var stored = await context.SongFiles.AsNoTracking().SingleAsync();
        stored.FingerprintVerified.Should().BeTrue();
    }

    [Fact]
    public async Task An_untagged_file_whose_recordings_all_agree_keeps_the_score()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "01 Get Lucky.flac", 248_000, tags: null);

        StubFingerprint();
        StubLookup(new AcoustIdResult(
            "ac-1",
            0.91,
            [
                Recording("m-a", "Get Lucky", 248, "Daft Punk"),
                Recording("m-b", "Get Lucky", 249, "Daft Punk"),
            ]));

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));
        _resolver.GetIdentityAsync("m-b", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-b", "Get Lucky", durationMs: 249_000));

        var result = await RunAsync(context, library.Id);

        result.Identified.Should().Be(1);

        // The closer length wins between two recordings the file cannot be told apart by.
        (await RowAsync(context, file.Id)).SongId.Should().Be(
            await context.Songs.AsNoTracking().Where(song => song.MbRecordingId == "m-a").Select(song => song.Id).SingleAsync());
    }

    [Fact]
    public async Task A_rate_limited_acoustid_leaves_the_file_pending_for_the_next_scan()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "a/whatever.flac", 248_000, Tags(title: "x", artist: "y"));

        StubFingerprint();
        _acoustId.LookupAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new AcoustIdLookupResult(AcoustIdStatus.RateLimited, [], "throttled"));

        var result = await RunAsync(context, library.Id);

        result.Should().Be(new ReferenceIdentifyResult(0, 0, 0, 1));

        var row = await RowAsync(context, file.Id);
        row.State.Should().Be(ReferenceFileState.Pending);
        row.Message.Should().Be("AcoustID unavailable; the next scan retries this file");
        row.Fingerprint.Should().Be("FP");

        // Tier 4 is not reached: the run is deferred, not judged.
        await _resolver.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_search_from_the_tags_is_accepted_at_0_90()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "a/whatever.flac", 248_000,
            Tags(title: "Get Lucky", artist: "Daft Punk"));

        _resolver.ResolveAsync("Daft Punk - Get Lucky", Arg.Any<CancellationToken>())
            .Returns(new ResolveResult
            {
                Status = ResolveStatus.Resolved,
                Identity = Identity("m-a", "Get Lucky", durationMs: 248_000),
            });

        var result = await RunAsync(context, library.Id);

        result.Identified.Should().Be(1);

        var row = await RowAsync(context, file.Id);
        row.IdentifiedBy.Should().Be("search");
        row.Confidence.Should().Be(0.90);
    }

    [Fact]
    public async Task A_search_from_the_file_name_finds_the_song_of_an_untagged_file()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "Daft Punk - Get Lucky.flac", 248_000, tags: null);

        _resolver.ResolveAsync("Daft Punk - Get Lucky", Arg.Any<CancellationToken>())
            .Returns(new ResolveResult
            {
                Status = ResolveStatus.Resolved,
                Identity = Identity("m-a", "Get Lucky", durationMs: 248_000),
            });

        var result = await RunAsync(context, library.Id);

        result.Identified.Should().Be(1);
        (await RowAsync(context, file.Id)).IdentifiedBy.Should().Be("search");
    }

    [Fact]
    public async Task An_unresolved_search_keeps_its_ranked_candidates_scaled_to_the_confidence_scale()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "a/whatever.flac", 248_000,
            Tags(title: "Get Lucky", artist: "Daft Punk"));

        _resolver.ResolveAsync("Daft Punk - Get Lucky", Arg.Any<CancellationToken>())
            .Returns(new ResolveResult
            {
                Status = ResolveStatus.Unresolved,
                Candidates =
                [
                    Candidate("m-a", "Get Lucky", 71),
                    Candidate("m-b", "Get Lucky (live)", 40),
                ],
            });

        var result = await RunAsync(context, library.Id);

        result.Should().Be(new ReferenceIdentifyResult(0, 1, 0, 0));

        var candidates = await context.MatchCandidates.AsNoTracking().OrderBy(entry => entry.Rank).ToListAsync();

        candidates.Should().HaveCount(2);
        candidates[0].Rank.Should().Be(1);
        candidates[0].Score.Should().BeApproximately(71 / 100.0 * 0.89, 0.0001);
        candidates[0].Reason.Should().Be("search 71");
        candidates[1].Score.Should().BeApproximately(40 / 100.0 * 0.89, 0.0001);
    }

    [Fact]
    public async Task Nothing_found_marks_the_file_unmatched()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "a/whatever.flac", 248_000,
            Tags(title: "Nobody Knows", artist: "No One"));

        var result = await RunAsync(context, library.Id);

        result.Should().Be(new ReferenceIdentifyResult(0, 0, 1, 0));

        var row = await RowAsync(context, file.Id);
        row.State.Should().Be(ReferenceFileState.Unmatched);
        row.SongId.Should().BeNull();
        (await context.MatchCandidates.CountAsync()).Should().Be(0);
    }

    // --- Ownership -----------------------------------------------------------------------------

    [Fact]
    public async Task A_chunk_adds_every_accepted_identity_in_one_call()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);

        for (var index = 0; index < 3; index++)
        {
            var id = $"m-{index}";
            AddFile(context, library.Id, $"a/{index}.flac", 248_000, Tags(title: "T" + index, artist: "A", mbid: id));

            _resolver.GetIdentityAsync(id, null, Arg.Any<CancellationToken>())
                .Returns(Identity(id, "T" + index, durationMs: 248_000));
        }

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

        await context.SaveChangesAsync();

        var result = await new ReferenceIdentifier(
                context,
                _resolver,
                spy,
                _fingerprinter,
                _acoustId,
                Options(),
                _timeProvider,
                NullLogger<ReferenceIdentifier>.Instance)
            .IdentifyPendingAsync(library.Id, null, CancellationToken.None);

        result.Identified.Should().Be(3);

        await spy.Received(1).AddIdentitiesAsync(
            Arg.Is<IReadOnlyList<SongIdentity>>(identities => identities.Count == 3),
            Arg.Any<SongAddOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Two_files_of_one_recording_share_the_song_and_the_second_is_a_duplicate()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);

        var first = AddFile(context, library.Id, "a/one.flac", 248_000, Tags(mbid: "m-a"));
        var second = AddFile(context, library.Id, "b/two.flac", 248_000, Tags(mbid: "m-a"));

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        var result = await RunAsync(context, library.Id);

        result.Identified.Should().Be(2);

        var firstRow = await RowAsync(context, first.Id);
        var secondRow = await RowAsync(context, second.Id);

        secondRow.SongId.Should().Be(firstRow.SongId);
        secondRow.Message.Should().Be("duplicate: the song already has a file");
        firstRow.Message.Should().BeNull();

        (await context.Songs.CountAsync()).Should().Be(1);
        (await context.SongFiles.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_song_that_already_has_a_library_file_keeps_it()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "a/Get Lucky.flac", 248_000, Tags(mbid: "m-a"));

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", durationMs: 248_000));

        // The song is already owned as a downloaded file Wondarr manages.
        var added = await NewSongService(context)
            .AddIdentitiesAsync([Identity("m-a", "Get Lucky", durationMs: 248_000)], new SongAddOptions(), CancellationToken.None);

        var libraryPath = Path.Combine("C:", "library", "Daft Punk", "Get Lucky.flac");

        context.SongFiles.Add(new SongFile
        {
            SongId = added[0].Song.Id,
            Path = libraryPath,
            SourceType = SourceTypes.Soulseek,
            QualityId = ProbeQualityId,
        });

        await context.SaveChangesAsync();

        var result = await RunAsync(context, library.Id);

        result.Identified.Should().Be(1);

        var row = await RowAsync(context, file.Id);
        row.SongId.Should().Be(added[0].Song.Id);
        row.Message.Should().Be("duplicate: the song already has a file");

        var stored = await context.SongFiles.AsNoTracking().SingleAsync();
        stored.Path.Should().Be(libraryPath);
        stored.SourceType.Should().Be(SourceTypes.Soulseek);
    }

    [Fact]
    public async Task A_re_identified_file_that_became_another_song_frees_the_old_one()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "a/changed.flac", 248_000, Tags(mbid: "m-b"));
        await context.SaveChangesAsync();

        var songs = NewSongService(context);
        var old = await songs.AddIdentitiesAsync(
            [Identity("m-a", "Old Song", durationMs: 248_000)],
            new SongAddOptions(),
            CancellationToken.None);

        context.SongFiles.Add(new SongFile
        {
            SongId = old[0].Song.Id,
            Path = ReferenceOwnershipPath(file.RelativePath),
            SourceType = SourceTypes.Reference,
            QualityId = ProbeQualityId,
        });

        // The scanner kept the song on the row: the file is the same file, only its bytes moved.
        file.SongId = old[0].Song.Id;
        await context.SaveChangesAsync();

        _resolver.GetIdentityAsync("m-b", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-b", "New Song", durationMs: 248_000));

        var result = await RunAsync(context, library.Id);

        result.Identified.Should().Be(1);

        var row = await RowAsync(context, file.Id);
        row.SongId.Should().NotBe(old[0].Song.Id);

        var stored = await context.SongFiles.AsNoTracking().SingleAsync();
        stored.SongId.Should().Be(row.SongId!.Value);

        // The old song is wanted again: it has no file row at all.
        (await context.SongFiles.AsNoTracking().CountAsync(candidate => candidate.SongId == old[0].Song.Id))
            .Should().Be(0);
    }

    [Fact]
    public async Task A_file_that_is_no_longer_identified_frees_the_reference_song()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "a/changed.flac", 248_000, Tags(title: "x", artist: "y"));
        await context.SaveChangesAsync();

        var songs = NewSongService(context);
        var old = await songs.AddIdentitiesAsync(
            [Identity("m-a", "Old Song", durationMs: 248_000)],
            new SongAddOptions(),
            CancellationToken.None);

        context.SongFiles.Add(new SongFile
        {
            SongId = old[0].Song.Id,
            Path = ReferenceOwnershipPath(file.RelativePath),
            SourceType = SourceTypes.Reference,
            QualityId = ProbeQualityId,
        });

        file.SongId = old[0].Song.Id;
        await context.SaveChangesAsync();

        var result = await RunAsync(context, library.Id);

        result.Unmatched.Should().Be(1);

        var row = await RowAsync(context, file.Id);
        row.State.Should().Be(ReferenceFileState.Unmatched);
        row.SongId.Should().BeNull();
        (await context.SongFiles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_fingerprint_the_tags_confirm_but_whose_length_differs_is_capped_and_queued()
    {
        // An extended mix shares the start of the album version: the tags agree, the length does not.
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "a/Get Lucky.flac", 248_000,
            Tags(title: "Get Lucky", artist: "Daft Punk"));

        StubFingerprint();
        StubLookup(new AcoustIdResult("ac-1", 0.97, [Recording("m-long", "Get Lucky", 369, "Daft Punk")]));

        _resolver.GetIdentityAsync("m-long", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-long", "Get Lucky", durationMs: 369_000));

        var result = await RunAsync(context, library.Id);

        result.Identified.Should().Be(0);
        result.Ambiguous.Should().Be(1);

        var row = await RowAsync(context, file.Id);
        row.State.Should().Be(ReferenceFileState.Ambiguous);
        (await context.SongFiles.CountAsync()).Should().Be(0);

        var candidate = await context.MatchCandidates.AsNoTracking().SingleAsync();
        candidate.Score.Should().Be(0.89);
        candidate.Reason.Should().Contain("length differs");
    }

    [Fact]
    public async Task A_deferred_file_keeps_the_song_it_was_linked_to()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "a/changed.flac", 248_000, Tags(title: "x", artist: "y"));
        await context.SaveChangesAsync();

        var old = await NewSongService(context).AddIdentitiesAsync(
            [Identity("m-a", "Old Song", durationMs: 248_000)],
            new SongAddOptions(),
            CancellationToken.None);

        context.SongFiles.Add(new SongFile
        {
            SongId = old[0].Song.Id,
            Path = ReferenceOwnershipPath(file.RelativePath),
            SourceType = SourceTypes.Reference,
            QualityId = ProbeQualityId,
        });

        file.SongId = old[0].Song.Id;
        await context.SaveChangesAsync();

        StubFingerprint();
        _acoustId.LookupAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new AcoustIdLookupResult(AcoustIdStatus.Unavailable, [], "down"));

        var result = await RunAsync(context, library.Id);

        result.Deferred.Should().Be(1);

        var row = await RowAsync(context, file.Id);
        row.State.Should().Be(ReferenceFileState.Pending);
        row.SongId.Should().Be(old[0].Song.Id);
        (await context.SongFiles.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Once_acoustid_refuses_the_rest_of_the_run_waits_without_asking_again()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        AddFile(context, library.Id, "a/1.flac", 248_000, Tags(title: "x", artist: "y"));
        AddFile(context, library.Id, "a/2.flac", 248_000, Tags(title: "x", artist: "y"));
        AddFile(context, library.Id, "a/3.flac", 248_000, Tags(title: "x", artist: "y"));
        await context.SaveChangesAsync();

        StubFingerprint();
        _acoustId.LookupAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new AcoustIdLookupResult(AcoustIdStatus.RateLimited, [], "throttled"));

        var result = await RunAsync(context, library.Id);

        result.Deferred.Should().Be(3);
        await _acoustId.Received(1).LookupAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _fingerprinter.Received(1).FingerprintAsync(
            Arg.Any<string>(), Arg.Any<FingerprintWindow>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_lookup_that_throws_defers_that_file_and_the_next_ones_are_still_identified()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var broken = AddFile(context, library.Id, "a/1.flac", 248_000, Tags(title: "Broken", artist: "Daft Punk"));
        var fine = AddFile(context, library.Id, "a/2.flac", 248_000, Tags(title: "Get Lucky", artist: "Daft Punk"));
        await context.SaveChangesAsync();

        _resolver.ResolveAsync("Daft Punk - Broken", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ResolveResult>(new HttpRequestException("MusicBrainz is down")));
        _resolver.ResolveAsync("Daft Punk - Get Lucky", Arg.Any<CancellationToken>())
            .Returns(new ResolveResult
            {
                Status = ResolveStatus.Resolved,
                Identity = Identity("m-a", "Get Lucky", durationMs: 248_000),
            });

        var result = await RunAsync(context, library.Id);

        result.Identified.Should().Be(1);
        result.Deferred.Should().Be(1);
        (await RowAsync(context, broken.Id)).State.Should().Be(ReferenceFileState.Pending);
        (await RowAsync(context, fine.Id)).State.Should().Be(ReferenceFileState.Identified);
    }

    [Fact]
    public async Task A_search_hit_whose_length_nobody_knows_is_only_a_candidate()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = AddFile(context, library.Id, "a/whatever.flac", 248_000,
            Tags(title: "Get Lucky", artist: "Daft Punk"));

        _resolver.ResolveAsync("Daft Punk - Get Lucky", Arg.Any<CancellationToken>())
            .Returns(new ResolveResult
            {
                Status = ResolveStatus.Resolved,
                Identity = Identity("m-a", "Get Lucky", durationMs: null),
            });

        var result = await RunAsync(context, library.Id);

        result.Ambiguous.Should().Be(1);
        (await RowAsync(context, file.Id)).State.Should().Be(ReferenceFileState.Ambiguous);
        (await context.MatchCandidates.AsNoTracking().SingleAsync()).Score.Should().Be(0.7);
    }

    /// <summary>Deletes this test's temp database.</summary>
    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Helpers -------------------------------------------------------------------------------

    /// <summary>The quality the flushed-FLAC probe measures as, which seeded quality rows cover.</summary>
    private static readonly long ProbeQualityId = MeasuredQuality.FromMediaInfo(Probe(248_000));

    private static MediaInfo Probe(int durationMs) =>
        new("flac", "flac", 900, 44100, 16, 2, durationMs, true, 25_000_000);

    private static string ReferenceOwnershipPath(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string ProbeJson(int durationMs) => JsonSerializer.Serialize(Probe(durationMs), StoredJson);

    private static string TagsJson(
        string? title = null,
        string? artist = null,
        string? mbid = null,
        string? isrc = null) =>
        JsonSerializer.Serialize(
            new FileTags(
                title,
                artist,
                null,
                null,
                null,
                isrc,
                mbid,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null),
            StoredJson);

    private static string Tags(
        string? title = null,
        string? artist = null,
        string? mbid = null,
        string? isrc = null) =>
        TagsJson(title, artist, mbid, isrc);

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

    private static SongCandidate Candidate(string mbRecordingId, string title, double score) => new()
    {
        Source = "musicbrainz",
        MbRecordingId = mbRecordingId,
        Title = title,
        ArtistCredit = "Daft Punk",
        Score = score,
        AlbumTitle = "Random Access Memories",
    };

    private static AcoustIdRecording Recording(
        string id,
        string title,
        double seconds,
        params string[] artists) =>
        new(id, title, seconds, artists);

    private void StubFingerprint() =>
        _fingerprinter.FingerprintAsync(Arg.Any<string>(), Arg.Any<FingerprintWindow>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new FingerprintResult(true, "FP", 248, FingerprintWindow.Start, null));

    private void StubLookup(params AcoustIdResult[] results) =>
        _acoustId.LookupAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new AcoustIdLookupResult(AcoustIdStatus.Ok, results, null));

    private FixedOptions<ReferenceOptions> Options() => new(_options);

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_timeProvider);

        return _database.CreateContext(_timeProvider);
    }

    private static async Task<ReferenceLibrary> ReferenceLibraryAsync(WondarrDbContext context)
    {
        var library = new ReferenceLibrary { Name = "Music", RootPath = Root };

        context.ReferenceLibraries.Add(library);
        await context.SaveChangesAsync();

        return library;
    }

    private static ReferenceFile AddFile(
        WondarrDbContext context,
        long libraryId,
        string relativePath,
        int durationMs,
        string? tags)
    {
        var file = new ReferenceFile
        {
            ReferenceLibraryId = libraryId,
            RelativePath = relativePath,
            Size = 25_000_000,
            ModifiedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastSeenAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Probe = ProbeJson(durationMs),
            Tags = tags,
            State = ReferenceFileState.Pending,
        };

        context.ReferenceFiles.Add(file);

        return file;
    }

    private static Task<ReferenceFile> RowAsync(WondarrDbContext context, long id) =>
        context.ReferenceFiles.AsNoTracking().SingleAsync(row => row.Id == id);

    private SongService NewSongService(WondarrDbContext context)
    {
        var counter = 0;

        return new SongService(
            context,
            _resolver,
            new AlbumPolicyEngine(() => new Guid(++counter, 0, 0, new byte[8])),
            _musicBrainz,
            _coverArt,
            NullLogger<SongService>.Instance);
    }

    private async Task<ReferenceIdentifyResult> RunAsync(WondarrDbContext context, long libraryId)
    {
        // The identifier reads the scanned rows back, so the setup's rows have to be in the database.
        await context.SaveChangesAsync();

        return await new ReferenceIdentifier(
                context,
                _resolver,
                NewSongService(context),
                _fingerprinter,
                _acoustId,
                Options(),
                _timeProvider,
                NullLogger<ReferenceIdentifier>.Instance)
            .IdentifyPendingAsync(libraryId, null, CancellationToken.None);
    }

    /// <summary>An options monitor that never changes, which is what these tests need.</summary>
    private sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
    {
        /// <inheritdoc />
        public T CurrentValue { get; } = value;

        /// <inheritdoc />
        public T Get(string? name) => CurrentValue;

        /// <inheritdoc />
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
