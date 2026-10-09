using System.Text.Json;
using Wondarr.Core.Compaction;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;
using Wondarr.Core.Profiles;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Media;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Profiles;

/// <summary>
/// The library mover against a real SQLite database and real temp folders as the two libraries' roots:
/// a placed file is re-filed under the target library's root with its sidecar, the emptied folder
/// goes, the rows and the history say where the song is now, and Plex is asked for both folders. The
/// organizer is the same stand-in <see cref="CompactExecutorTests"/> uses — it moves the source to the
/// folder the album names and reports success, which is exactly what the real one does for the files
/// these tests seed.
/// </summary>
public class SongLibraryMoverTests : IDisposable
{
    /// <summary>The seeded FLAC quality, which the mover reads off the file row.</summary>
    private const long FlacQualityId = 36;

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _timeProvider = new() { AutoAdvanceAmount = TimeSpan.FromSeconds(2) };
    private readonly IDiskOperations _disk = new DiskOperations();
    private readonly IPlexLibraryUpdater _updater = Substitute.For<IPlexLibraryUpdater>();
    private readonly MovingOrganizer _organizer;
    private readonly string _root;
    private readonly string _root2;
    private readonly string _bin;
    private readonly string _config;
    private readonly SongLibraryMover _mover;

    public SongLibraryMoverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wondarr-move-tests", Guid.NewGuid().ToString("N"), "music");
        _root2 = Path.Combine(Path.GetTempPath(), "wondarr-move-tests", Guid.NewGuid().ToString("N"), "archive");
        _bin = Path.Combine(Path.GetTempPath(), "wondarr-move-tests", Guid.NewGuid().ToString("N"), "recycle");
        _config = Path.Combine(Path.GetTempPath(), "wondarr-move-tests", Guid.NewGuid().ToString("N"), "config");

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_root2);
        Directory.CreateDirectory(_bin);
        Directory.CreateDirectory(_config);

        _organizer = new MovingOrganizer(_disk);

        var recycleBin = new RecycleBin(
            _disk,
            new TestOptionsMonitor<ImportOptions>(new ImportOptions { RecycleBinPath = _bin }),
            new WondarrPaths(_config),
            _timeProvider,
            NullLogger<RecycleBin>.Instance);

        _mover = new SongLibraryMover(
            _database.CreateContext(_timeProvider),
            _organizer,
            _disk,
            recycleBin,
            _updater,
            new SongFileLock(),
            NullLogger<SongLibraryMover>.Instance);
    }

    [Fact]
    public async Task A_song_with_a_placed_file_moves_to_the_other_librarys_root()
    {
        await using var context = await ContextAsync();
        var songId = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac", lyrics: "the words");
        await SeedCoverAsync("Daft Punk/Single 1");

        var result = await _mover.MoveAsync(songId, 2, CancellationToken.None);

        result.Should().Be(SongMoveResult.Moved);

        // The file and its lyrics are under the second library's root, and the old folder went.
        var newFolder = Path.Combine(_root2, "Daft Punk", "Single 1");
        File.Exists(Path.Combine(newFolder, "01 - Track 1.flac")).Should().BeTrue();
        File.ReadAllText(Path.Combine(newFolder, "01 - Track 1.lrc")).Should().Be("the words");

        // The old album folder went with its cover, and the artist folder it left empty went too.
        Directory.Exists(Path.Combine(_root, "Daft Punk", "Single 1")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "Daft Punk")).Should().BeFalse();
        Directory.EnumerateFiles(_bin, "cover.jpg", SearchOption.AllDirectories).Should().HaveCount(1);

        // The rows say where the song is now, and the history says how it got there.
        await using var fresh = _database.CreateContext(_timeProvider);
        var song = await fresh.Songs.AsNoTracking().SingleAsync(candidate => candidate.Id == songId);
        song.LibraryId.Should().Be(2);

        var file = await fresh.SongFiles.AsNoTracking().SingleAsync(candidate => candidate.SongId == songId);
        file.Path.Should().Be(Path.Combine(newFolder, "01 - Track 1.flac"));

        var history = await fresh.History.AsNoTracking().SingleAsync(row => row.SongId == songId);
        history.EventType.Should().Be(HistoryEventType.Renamed);
        var payload = JsonDocument.Parse(history.Data).RootElement;
        payload.GetProperty("reason").GetString().Should().Be("library");
        payload.GetProperty("fromLibraryId").GetInt64().Should().Be(1);
        payload.GetProperty("toLibraryId").GetInt64().Should().Be(2);
        payload.GetProperty("from").GetString().Should().Contain("Daft Punk");
        payload.GetProperty("to").GetString().Should().Be(file.Path);

        // The organizer filed the song under the target library, and never asked for lyrics.
        _organizer.Requests.Should().ContainSingle();
        _organizer.Requests[0].Library.Id.Should().Be(2);
        _organizer.Requests[0].KeepSource.Should().BeFalse();
        _organizer.Requests[0].LookUpLyrics.Should().BeFalse();

        // Plex is asked for the folder the file left and the one it arrived in.
        _updater.Received().RequestFolder(1, Path.Combine(_root, "Daft Punk", "Single 1"));
        _updater.Received().RequestFolder(2, newFolder);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public async Task ReplayGain_values_on_the_row_follow_the_target_librarys_switch(bool fromOn, bool toOn, bool kept)
    {
        await using var context = await ContextAsync();
        var songId = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");
        await context.Libraries.Where(library => library.Id == 1).ExecuteUpdateAsync(update => update.SetProperty(library => library.ReplayGain, fromOn));
        await context.Libraries.Where(library => library.Id == 2).ExecuteUpdateAsync(update => update.SetProperty(library => library.ReplayGain, toOn));

        // A file in a library without the switch carries no values.
        if (fromOn)
        {
            await context.SongFiles.ExecuteUpdateAsync(update => update
                .SetProperty(file => file.ReplayGainDb, -8.52)
                .SetProperty(file => file.ReplayGainPeak, 1.047129));
        }

        (await _mover.MoveAsync(songId, 2, CancellationToken.None)).Should().Be(SongMoveResult.Moved);

        await using var fresh = _database.CreateContext(_timeProvider);
        var file = await fresh.SongFiles.AsNoTracking().SingleAsync();
        file.ReplayGainDb.Should().Be(kept ? -8.52 : null);
        file.ReplayGainPeak.Should().Be(kept ? 1.047129 : null);
        _organizer.Requests.Single().ReplayGainDb.Should().Be(fromOn ? -8.52 : null);
    }

    [Fact]
    public async Task A_song_with_a_reference_file_only_changes_library()
    {
        await using var context = await ContextAsync();
        var songId = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac", reference: true);

        var result = await _mover.MoveAsync(songId, 2, CancellationToken.None);

        result.Should().Be(SongMoveResult.Moved);

        // The user's own file is never moved, and the organizer is never asked.
        _organizer.Requests.Should().BeEmpty();
        File.Exists(Path.Combine(_root, "Daft Punk", "Single 1", "01 - Track 1.flac")).Should().BeTrue();

        await using var fresh = _database.CreateContext(_timeProvider);
        var song = await fresh.Songs.AsNoTracking().SingleAsync(candidate => candidate.Id == songId);
        song.LibraryId.Should().Be(2);

        var file = await fresh.SongFiles.AsNoTracking().SingleAsync(candidate => candidate.SongId == songId);
        file.Path.Should().Be(Path.Combine(_root, "Daft Punk", "Single 1", "01 - Track 1.flac"));

        var history = await fresh.History.AsNoTracking().SingleAsync(row => row.SongId == songId);
        history.EventType.Should().Be(HistoryEventType.Renamed);
    }

    [Fact]
    public async Task A_song_already_in_the_target_library_is_reported_and_left_alone()
    {
        await using var context = await ContextAsync();
        var songId = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");

        var result = await _mover.MoveAsync(songId, SeedData.DefaultLibraryId, CancellationToken.None);

        result.Should().Be(SongMoveResult.AlreadyThere);
        _organizer.Requests.Should().BeEmpty();
        _updater.DidNotReceive().RequestFolder(Arg.Any<long>(), Arg.Any<string>());
    }

    [Fact]
    public async Task A_song_with_an_unfinished_compaction_move_is_refused_and_nothing_changes()
    {
        await using var context = await ContextAsync();
        var songId = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac");

        context.CompactMoves.Add(new CompactMoveRecord
        {
            LibraryId = SeedData.DefaultLibraryId,
            SongId = songId,
            FromPath = Path.Combine(_root, "Daft Punk", "Single 1", "01 - Track 1.flac"),
            ToPath = Path.Combine(_root, "Daft Punk", "Random Access Memories", "01 - Track 1.flac"),
            Proposed = "{}",
            State = CompactMoveState.Planned,
        });
        await context.SaveChangesAsync();

        var result = await _mover.MoveAsync(songId, 2, CancellationToken.None);

        result.Outcome.Should().Be(SongMoveOutcome.Refused);
        result.Reason.Should().Contain("compaction");

        _organizer.Requests.Should().BeEmpty();
        File.Exists(Path.Combine(_root, "Daft Punk", "Single 1", "01 - Track 1.flac")).Should().BeTrue();

        await using var fresh = _database.CreateContext(_timeProvider);
        (await fresh.Songs.AsNoTracking().SingleAsync(candidate => candidate.Id == songId)).LibraryId
            .Should().Be(SeedData.DefaultLibraryId);
        (await fresh.History.AsNoTracking().CountAsync(row => row.SongId == songId)).Should().Be(0);
    }

    [Fact]
    public async Task An_organizer_failure_leaves_the_file_and_the_rows_as_they_were()
    {
        await using var context = await ContextAsync();
        var songId = await SeedSongAsync(context, "Track 1", "01 - Track 1.flac", lyrics: "the words");
        _organizer.FailFor = "Track 1";

        var result = await _mover.MoveAsync(songId, 2, CancellationToken.None);

        result.Outcome.Should().Be(SongMoveOutcome.Failed);
        result.Reason.Should().NotBeNullOrEmpty();

        // The file, its sidecar and the rows are exactly where they were.
        var oldFolder = Path.Combine(_root, "Daft Punk", "Single 1");
        File.Exists(Path.Combine(oldFolder, "01 - Track 1.flac")).Should().BeTrue();
        File.Exists(Path.Combine(oldFolder, "01 - Track 1.lrc")).Should().BeTrue();
        Directory.Exists(oldFolder).Should().BeTrue();

        await using var fresh = _database.CreateContext(_timeProvider);
        var song = await fresh.Songs.AsNoTracking().SingleAsync(candidate => candidate.Id == songId);
        song.LibraryId.Should().Be(SeedData.DefaultLibraryId);

        var file = await fresh.SongFiles.AsNoTracking().SingleAsync(candidate => candidate.SongId == songId);
        file.Path.Should().Be(Path.Combine(oldFolder, "01 - Track 1.flac"));

        (await fresh.History.AsNoTracking().CountAsync(row => row.SongId == songId)).Should().Be(0);
        _updater.DidNotReceive().RequestFolder(Arg.Any<long>(), Arg.Any<string>());
    }

    [Fact]
    public async Task A_song_without_a_file_moves_without_touching_anything_on_disk()
    {
        await using var context = await ContextAsync();
        var songId = await SeedSongAsync(context, "Track 1", file: false);

        var result = await _mover.MoveAsync(songId, 2, CancellationToken.None);

        result.Should().Be(SongMoveResult.Moved);
        _organizer.Requests.Should().BeEmpty();

        await using var fresh = _database.CreateContext(_timeProvider);
        (await fresh.Songs.AsNoTracking().SingleAsync(candidate => candidate.Id == songId)).LibraryId
            .Should().Be(2);
    }

    public void Dispose()
    {
        _database.Dispose();

        foreach (var directory in new[]
        {
            Path.GetDirectoryName(_root),
            Path.GetDirectoryName(_root2),
            Path.GetDirectoryName(_bin),
            Path.GetDirectoryName(_config),
        })
        {
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A database with the seeded library pointed at one temp root and a second library at another.</summary>
    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_timeProvider);

        var context = _database.CreateContext(_timeProvider);
        var first = await context.Libraries.SingleAsync(candidate => candidate.Id == SeedData.DefaultLibraryId);

        first.RootPath = _root;
        first.NamingTemplate = "{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}";

        context.Libraries.Add(new Library
        {
            Name = "Archive",
            RootPath = _root2,
            NamingTemplate = "{Album Artist Name}/{Album Title}/{track:00} - {Track Title}",
            Layout = LibraryLayout.ArtistAlbum,
            AlbumPolicy = AlbumPolicy.OriginalAlbum,
        });

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return context;
    }

    /// <summary>One song of one album folder, with a real file (and its lyrics sidecar) where the row says it is.</summary>
    private async Task<long> SeedSongAsync(
        WondarrDbContext context,
        string title,
        string? fileName = null,
        bool file = true,
        string? lyrics = null,
        bool reference = false)
    {
        var index = await context.Songs.CountAsync() + 1;
        var artist = await context.Artists.FirstOrDefaultAsync(candidate => candidate.Name == "Daft Punk")
            ?? context.Artists.Add(new Artist { Name = "Daft Punk", SortName = "Daft Punk", MbArtistId = "a1" }).Entity;

        await context.SaveChangesAsync();

        var song = new Song
        {
            Title = title,
            ArtistCredit = "Daft Punk",
            PrimaryArtistId = artist.Id,
            MbRecordingId = "m-" + index,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };

        context.Songs.Add(song);
        await context.SaveChangesAsync();

        context.SongArtists.Add(new SongArtist
        {
            SongId = song.Id,
            ArtistId = artist.Id,
            Role = ArtistRole.Main,
            Position = 1,
        });

        context.AlbumContexts.Add(new AlbumContext
        {
            SongId = song.Id,
            Kind = AlbumContextKind.Single,
            AlbumTitle = "Single 1",
            AlbumArtist = "Daft Punk",
            AlbumKey = "s-1",
            MbReleaseId = "s-1",
            MbReleaseGroupId = "s-1-group",
            TrackNo = 1,
            DiscNo = 1,
            TotalTracks = 1,
            Date = "2013-05-17",
        });

        if (file)
        {
            var path = Path.Combine(_root, "Daft Punk", "Single 1", fileName ?? $"0{index} - {title}.flac");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "the audio bytes");

            if (lyrics is not null)
            {
                await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), lyrics);
            }

            context.SongFiles.Add(new SongFile
            {
                SongId = song.Id,
                Path = path,
                Size = 30_000_000,
                Codec = "flac",
                Container = "flac",
                BitrateKbps = 1000,
                SampleRate = 44_100,
                BitDepth = 16,
                Channels = 2,
                DurationMs = 369_000,
                QualityId = FlacQualityId,
                SourceType = reference ? SourceTypes.Reference : SourceTypes.Soulseek,
                ImportedAt = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc),
            });
        }

        await context.SaveChangesAsync();

        return song.Id;
    }

    /// <summary>Puts an album folder's <c>cover.jpg</c> where a move-out would leave it behind.</summary>
    private async Task SeedCoverAsync(string relativeFolder)
    {
        var folder = Path.Combine(_root, relativeFolder);
        Directory.CreateDirectory(folder);

        await File.WriteAllTextAsync(Path.Combine(folder, "cover.jpg"), "jpeg bytes");
    }

    /// <summary>
    /// The organizer an import uses, without the tag writer and the placer: it moves the source to the
    /// folder the album names, under the library the request names, and reports success — or refuses
    /// the song it was told to refuse, leaving the source exactly where it was.
    /// </summary>
    private sealed class MovingOrganizer(IDiskOperations disk) : ILibraryOrganizer
    {
        /// <summary>The song title this organizer refuses, or <see langword="null"/> to refuse nothing.</summary>
        public string? FailFor { get; set; }

        /// <summary>Every request the mover made, in order.</summary>
        public List<OrganizeRequest> Requests { get; } = [];

        /// <inheritdoc />
        public Task<OrganizeResult> OrganizeAsync(OrganizeRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            Requests.Add(request);

            if (FailFor is not null && request.Song.Title == FailFor)
            {
                // The real organizer leaves the source exactly where it was when tagging fails.
                return Task.FromResult(
                    new OrganizeResult(OrganizeFailure.Tagging, "the tag writer refused", null, null, new Dictionary<string, string>()));
            }

            var directory = Path.Combine(request.Library.RootPath, request.Album.AlbumArtist, request.Album.AlbumTitle);
            var target = Path.Combine(directory, Path.GetFileName(request.SourcePath));

            disk.CreateDirectory(directory);
            disk.MoveFile(request.SourcePath, target);

            return Task.FromResult(
                new OrganizeResult(
                    OrganizeFailure.None,
                    null,
                    target,
                    null,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["TITLE"] = request.Song.Title }));
        }
    }
}
