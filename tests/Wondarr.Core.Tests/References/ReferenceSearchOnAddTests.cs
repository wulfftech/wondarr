using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Jobs;
using Wondarr.Core.Media;
using Wondarr.Core.Metadata.AcoustId;
using Wondarr.Core.Metadata.CoverArt;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.References;
using Wondarr.Core.Searching;
using Wondarr.Core.Songs;
using Wondarr.Core.Tagging;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.References;

/// <summary>
/// A song found in a reference library is owned already: with <c>search.search_on_add</c> <b>on</b>, none of
/// the three ways a reference file becomes a song (a scan's identification, a Match-queue resolve, a bulk
/// accept) may enqueue a search, and the song reaches other connections only together with its file.
/// Everything is real except the network (resolver, fingerprinter, AcoustID, cover art) and the command
/// queue, which records what it was asked.
/// </summary>
public sealed class ReferenceSearchOnAddTests : IDisposable
{
    private const string Root = "/reference/music";

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
    private readonly ICommandQueue _commands = Substitute.For<ICommandQueue>();

    public ReferenceSearchOnAddTests()
    {
        _coverArt.ResolveAsync(Arg.Any<CoverArtRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CoverArt("https://cover.example/album.jpg", "coverartarchive"));
        _fingerprinter.FingerprintAsync(
                Arg.Any<string>(),
                Arg.Any<FingerprintWindow>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new FingerprintResult(false, null, 0, FingerprintWindow.Start, "no fingerprinter here"));
    }

    /// <summary>Deletes this test's temp database.</summary>
    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Nothing is searched for ----------------------------------------------------------------

    [Fact]
    public async Task Control_a_plain_add_with_search_on_add_on_does_enqueue_a_search()
    {
        await using var context = await ContextAsync();

        await NewSongService(context)
            .AddIdentitiesAsync([Identity("m-a", "Get Lucky")], new SongAddOptions(), CancellationToken.None);

        await _commands.Received(1).EnqueueAsync(
            SongSearchCommandHandler.CommandName,
            Arg.Any<string?>(),
            Arg.Any<CommandTrigger>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_add_with_search_on_add_off_enqueues_nothing_whatever_the_global_setting()
    {
        await using var context = await ContextAsync();

        await NewSongService(context)
            .AddIdentitiesAsync([Identity("m-a", "Get Lucky")], new SongAddOptions { SearchOnAdd = false }, CancellationToken.None);

        _commands.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Identifying_reference_files_adds_songs_and_enqueues_no_search(int files)
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);

        for (var index = 0; index < files; index++)
        {
            var mbid = "m-" + index;

            AddPendingFile(context, library.Id, $"a/{index}.flac", mbid);
            _resolver.GetIdentityAsync(mbid, null, Arg.Any<CancellationToken>())
                .Returns(Identity(mbid, "Song " + index));
        }

        await context.SaveChangesAsync();

        var result = await Identifier(context, NewSongService(context)).IdentifyPendingAsync(library.Id, null, CancellationToken.None);

        result.Identified.Should().Be(files);
        (await context.Songs.CountAsync()).Should().Be(files);
        (await context.SongFiles.CountAsync()).Should().Be(files);
        _commands.ReceivedCalls().Should().BeEmpty("a song the user owns in a reference library is never searched for");
    }

    [Fact]
    public async Task Resolving_a_match_queue_file_adds_the_song_and_enqueues_no_search()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var file = await AddStoredFileAsync(context, library.Id, "a/Get Lucky.flac", ReferenceFileState.Unmatched);

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>()).Returns(Identity("m-a", "Get Lucky"));

        var result = await MatchService(context).ResolveAsync(
            file.Id,
            new ReferenceResolveChoice(null, "m-a", null, false),
            CancellationToken.None);

        result.State.Should().Be(ReferenceFileState.Identified);
        (await context.SongFiles.CountAsync()).Should().Be(1);
        _commands.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Bulk_accepting_match_queue_files_adds_the_songs_and_enqueues_no_search()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);
        var ids = new List<long>();

        for (var index = 0; index < 3; index++)
        {
            var mbid = "m-" + index;
            var file = await AddStoredFileAsync(context, library.Id, $"a/{index}.flac", ReferenceFileState.Ambiguous);

            context.MatchCandidates.Add(new MatchCandidate
            {
                ReferenceFileId = file.Id,
                Rank = 1,
                Identity = new MatchIdentity("musicbrainz", mbid, null, "Song " + index, "Daft Punk", 248_000, "Random Access Memories").ToJson(),
                Score = 0.8,
                Reason = "search 80",
            });
            _resolver.GetIdentityAsync(mbid, null, Arg.Any<CancellationToken>()).Returns(Identity(mbid, "Song " + index));
            ids.Add(file.Id);
        }

        await context.SaveChangesAsync();

        var result = await MatchService(context).AcceptTopCandidatesAsync(ids, CancellationToken.None);

        result.Resolved.Should().Be(3);
        (await context.SongFiles.CountAsync()).Should().Be(3);
        _commands.ReceivedCalls().Should().BeEmpty();
    }

    // --- The song and its file arrive together --------------------------------------------------

    [Fact]
    public async Task Another_connection_never_sees_an_identified_song_without_its_file()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);

        for (var index = 0; index < 2; index++)
        {
            var mbid = "m-" + index;

            AddPendingFile(context, library.Id, $"a/{index}.flac", mbid);
            _resolver.GetIdentityAsync(mbid, null, Arg.Any<CancellationToken>()).Returns(Identity(mbid, "Song " + index));
        }

        await context.SaveChangesAsync();

        // Right after the add (the songs are saved in the context, the files are not linked yet) a
        // reader on its own connection must see no song at all.
        var real = NewSongService(context);
        var seenAfterAdd = new List<(long Songs, long Files)>();
        var spy = Substitute.For<ISongService>();

        spy.AddIdentitiesAsync(Arg.Any<IReadOnlyList<SongIdentity>>(), Arg.Any<SongAddOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var added = await real.AddIdentitiesAsync(
                    call.Arg<IReadOnlyList<SongIdentity>>(),
                    call.Arg<SongAddOptions>(),
                    call.Arg<CancellationToken>());

                seenAfterAdd.Add((await CountOnOtherConnectionAsync("song"), await CountOnOtherConnectionAsync("song_file")));

                return added;
            });

        await Identifier(context, spy).IdentifyPendingAsync(library.Id, null, CancellationToken.None);

        seenAfterAdd.Should().ContainSingle().Which.Should().Be((0L, 0L), "the songs are committed together with their files, not before");
        (await CountOnOtherConnectionAsync("song")).Should().Be(2);
        (await CountOnOtherConnectionAsync("song_file")).Should().Be(2);
        context.Database.CurrentTransaction.Should().BeNull("the chunk's transaction is committed");
    }

    [Fact]
    public async Task A_failure_after_the_add_rolls_the_songs_back()
    {
        await using var context = await ContextAsync();
        var library = await ReferenceLibraryAsync(context);

        AddPendingFile(context, library.Id, "a/0.flac", "m-0");
        _resolver.GetIdentityAsync("m-0", null, Arg.Any<CancellationToken>()).Returns(Identity("m-0", "Song 0"));
        await context.SaveChangesAsync();

        // The song service fails after it added (and saved, inside the open transaction) the songs.
        var real = NewSongService(context);
        var spy = Substitute.For<ISongService>();

        spy.AddIdentitiesAsync(Arg.Any<IReadOnlyList<SongIdentity>>(), Arg.Any<SongAddOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<SongAddResult>>>(async call =>
            {
                await real.AddIdentitiesAsync(call.Arg<IReadOnlyList<SongIdentity>>(), call.Arg<SongAddOptions>(), call.Arg<CancellationToken>());

                throw new InvalidOperationException("boom");
            });

        var act = async () => await Identifier(context, spy).IdentifyPendingAsync(library.Id, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        context.Database.CurrentTransaction.Should().BeNull();
        (await CountOnOtherConnectionAsync("song")).Should().Be(0);
    }

    // --- Helpers --------------------------------------------------------------------------------

    private static string ProbeJson() =>
        JsonSerializer.Serialize(new MediaInfo("flac", "flac", 900, 44100, 16, 2, 248_000, true, 25_000_000), StoredJson);

    private static string TagsJson(string mbid) =>
        JsonSerializer.Serialize(
            new FileTags(null, null, null, null, null, null, mbid, null, null, null, null, null, null, null, null),
            StoredJson);

    private static SongIdentity Identity(string mbRecordingId, string title) => new()
    {
        Source = "musicbrainz",
        MbRecordingId = mbRecordingId,
        Title = title,
        ArtistCredit = "Daft Punk",
        Artists = [new IdentityArtist("Daft Punk", "Daft Punk", "a1", null, ArtistRole.Main, 0)],
        DurationMs = 248_000,
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

    private static async Task<ReferenceLibrary> ReferenceLibraryAsync(WondarrDbContext context)
    {
        var library = new ReferenceLibrary { Name = "Music", RootPath = Root };

        context.ReferenceLibraries.Add(library);
        await context.SaveChangesAsync();

        return library;
    }

    private static void AddPendingFile(WondarrDbContext context, long libraryId, string relativePath, string mbid) =>
        context.ReferenceFiles.Add(new ReferenceFile
        {
            ReferenceLibraryId = libraryId,
            RelativePath = relativePath,
            Size = 25_000_000,
            ModifiedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastSeenAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Probe = ProbeJson(),
            Tags = TagsJson(mbid),
            State = ReferenceFileState.Pending,
        });

    private static async Task<ReferenceFile> AddStoredFileAsync(
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
            Probe = ProbeJson(),
            State = state,
        };

        context.ReferenceFiles.Add(file);
        await context.SaveChangesAsync();

        return file;
    }

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_timeProvider);

        return _database.CreateContext(_timeProvider);
    }

    /// <summary>A song service with <c>search.search_on_add</c> <b>on</b>, which is the setting that caused the defect.</summary>
    private SongService NewSongService(WondarrDbContext context)
    {
        var counter = 0;
        var options = Substitute.For<IOptionsMonitor<SearchOptions>>();

        options.CurrentValue.Returns(new SearchOptions { SearchOnAdd = true });

        return new SongService(
            context,
            _resolver,
            new AlbumPolicyEngine(() => new Guid(++counter, 0, 0, new byte[8])),
            _musicBrainz,
            _coverArt,
            _commands,
            options,
            NullLogger<SongService>.Instance);
    }

    private ReferenceIdentifier Identifier(WondarrDbContext context, ISongService songs) =>
        new(
            context,
            _resolver,
            songs,
            _fingerprinter,
            _acoustId,
            new FixedOptions<ReferenceOptions>(new ReferenceOptions()),
            _timeProvider,
            NullLogger<ReferenceIdentifier>.Instance);

    private ReferenceMatchService MatchService(WondarrDbContext context) =>
        new(context, _resolver, NewSongService(context), _timeProvider, NullLogger<ReferenceMatchService>.Instance);

    /// <summary>Counts a table's rows the way another connection (a command handler's scope) would see it.</summary>
    private async Task<long> CountOnOtherConnectionAsync(string table)
    {
        await using var connection = new SqliteConnection($"Data Source={_database.FilePath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
