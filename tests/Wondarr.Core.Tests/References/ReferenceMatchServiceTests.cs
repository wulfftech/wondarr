using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
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
                spy,
                _timeProvider,
                NullLogger<ReferenceMatchService>.Instance)
            .AcceptTopCandidatesAsync(ids, CancellationToken.None);

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
            [good.Id, noCandidate.Id, unmatched.Id],
            CancellationToken.None);

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

        var accept = async () => await Service(context).AcceptTopCandidatesAsync(ids, CancellationToken.None);

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
            var result = await service.AcceptTopCandidatesAsync([bulk.Id], CancellationToken.None);

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
            NewSongService(context),
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
            NullLogger<SongService>.Instance);
    }
}
