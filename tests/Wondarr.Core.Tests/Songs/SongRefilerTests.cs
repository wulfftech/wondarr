using System.Text.Json;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Jobs;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Organizer;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Songs;

/// <summary>
/// Re-filing a song after its album is chosen: a managed file moves to the folder the new album names
/// and is retagged through the organizer, with its sidecars and a partial Plex scan of both folders; a
/// reference file is never touched.
/// </summary>
public sealed class SongRefilerTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();
    private readonly IDiskOperations _disk = new DiskOperations();
    private readonly IPlexLibraryUpdater _updater = Substitute.For<IPlexLibraryUpdater>();
    private readonly List<string> _scans = [];
    private readonly MovingOrganizer _organizer;
    private readonly string _root;
    private readonly string _bin;
    private readonly string _config;

    public SongRefilerTests()
    {
        var parent = Path.Combine(Path.GetTempPath(), "wondarr-refile-tests", Guid.NewGuid().ToString("N"));

        _root = Path.Combine(parent, "music");
        _bin = Path.Combine(parent, "recycle");
        _config = Path.Combine(parent, "config");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_bin);
        Directory.CreateDirectory(_config);

        _organizer = new MovingOrganizer(_root, _disk);

        _updater
            .When(updater => updater.RequestFolder(Arg.Any<long>(), Arg.Any<string>()))
            .Do(call => _scans.Add(call.ArgAt<string>(1)));
    }

    [Fact]
    public async Task The_file_moves_to_the_new_albums_folder_with_its_lyrics_and_is_retagged_and_both_folders_are_scanned()
    {
        await using var context = await ContextAsync();
        var (song, from) = await SeedAsync(context, SourceTypes.Soulseek, lyrics: "the words");
        await WriteCoverAsync(Path.GetDirectoryName(from)!);
        await ChooseAlbumAsync(context, song, "r1", "Random Access Memories");

        var result = await Refiler(context).RefileAsync(song, CancellationToken.None);

        result.Outcome.Should().Be(SongRefileOutcome.Refiled);

        var target = Path.Combine(_root, "Daft Punk", "Random Access Memories", "01 - Track 1.flac");
        File.Exists(target).Should().BeTrue();
        File.Exists(from).Should().BeFalse();
        File.ReadAllText(Path.ChangeExtension(target, ".lrc")).Should().Be("the words");

        // The emptied old folder goes with its cover.
        Directory.Exists(Path.GetDirectoryName(from)!).Should().BeFalse();
        Directory.EnumerateFiles(_bin, "cover.jpg", SearchOption.AllDirectories).Should().HaveCount(1);

        // The organizer retagged for the chosen album.
        _organizer.Requests.Single().Album.AlbumTitle.Should().Be("Random Access Memories");

        await using var fresh = _database.CreateContext(_time);
        var file = await fresh.SongFiles.AsNoTracking().SingleAsync();
        file.Path.Should().Be(target);
        file.TagsWritten.Should().Contain("Random Access Memories");
        (await fresh.History.AsNoTracking().SingleAsync()).EventType.Should().Be(HistoryEventType.Renamed);

        _scans.Should().BeEquivalentTo(Path.GetDirectoryName(from), Path.GetDirectoryName(target));
    }

    [Fact]
    public async Task A_reference_file_is_never_moved()
    {
        await using var context = await ContextAsync();
        var (song, from) = await SeedAsync(context, SourceTypes.Reference);
        await ChooseAlbumAsync(context, song, "r1", "Random Access Memories");

        var result = await Refiler(context).RefileAsync(song, CancellationToken.None);

        result.Outcome.Should().Be(SongRefileOutcome.NothingToMove);
        File.Exists(from).Should().BeTrue();
        _organizer.Requests.Should().BeEmpty();
        _scans.Should().BeEmpty();
    }

    [Fact]
    public async Task A_song_without_a_file_has_nothing_to_move()
    {
        await using var context = await ContextAsync();
        var (song, _) = await SeedAsync(context, sourceType: null);

        var result = await Refiler(context).RefileAsync(song, CancellationToken.None);

        result.Outcome.Should().Be(SongRefileOutcome.NothingToMove);
        _organizer.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_song_with_a_move_in_flight_is_refused_and_the_file_stays()
    {
        await using var context = await ContextAsync();
        var (song, from) = await SeedAsync(context, SourceTypes.Soulseek);
        await ChooseAlbumAsync(context, song, "r1", "Random Access Memories");
        context.CompactMoves.Add(new CompactMoveRecord
        {
            LibraryId = SeedData.DefaultLibraryId,
            SongId = song,
            FromPath = from,
            State = CompactMoveState.Planned,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await Refiler(context).RefileAsync(song, CancellationToken.None);

        result.Outcome.Should().Be(SongRefileOutcome.Refused);
        result.Reason.Should().Contain("compaction");
        File.Exists(from).Should().BeTrue();
        _organizer.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_placement_that_reports_an_error_after_the_move_is_still_recorded()
    {
        await using var context = await ContextAsync();
        var (song, _) = await SeedAsync(context, SourceTypes.Soulseek);
        await ChooseAlbumAsync(context, song, "r1", "Random Access Memories");
        _organizer.ErrorAfterMove = "chmod failed";

        var result = await Refiler(context).RefileAsync(song, CancellationToken.None);

        var target = Path.Combine(_root, "Daft Punk", "Random Access Memories", "01 - Track 1.flac");

        result.Outcome.Should().Be(SongRefileOutcome.Refiled);
        await using var fresh = _database.CreateContext(_time);
        (await fresh.SongFiles.AsNoTracking().SingleAsync()).Path.Should().Be(target);
    }

    [Fact]
    public async Task A_failing_organizer_leaves_the_file_and_the_row_as_they_were()
    {
        await using var context = await ContextAsync();
        var (song, from) = await SeedAsync(context, SourceTypes.Soulseek);
        await ChooseAlbumAsync(context, song, "r1", "Random Access Memories");
        _organizer.Fail = true;

        var result = await Refiler(context).RefileAsync(song, CancellationToken.None);

        result.Outcome.Should().Be(SongRefileOutcome.Failed);
        File.Exists(from).Should().BeTrue();
        await using var fresh = _database.CreateContext(_time);
        (await fresh.SongFiles.AsNoTracking().SingleAsync()).Path.Should().Be(from);
    }

    [Fact]
    public async Task The_song_is_read_only_after_its_file_lock_is_held()
    {
        await using var context = await ContextAsync();
        var (song, from) = await SeedAsync(context, SourceTypes.Soulseek);
        await ChooseAlbumAsync(context, song, "r1", "Random Access Memories");

        var songFileLock = new SongFileLock();
        var held = await songFileLock.AcquireAsync(song, CancellationToken.None);
        var refiler = new SongRefiler(
            context,
            _organizer,
            _disk,
            Substitute.For<IRecycleBin>(),
            _updater,
            songFileLock,
            NullLogger<SongRefiler>.Instance);

        var refiling = refiler.RefileAsync(song, CancellationToken.None);

        // While another task holds the song, the refiler has not read it or touched the file.
        var waiting = async () => await refiling.WaitAsync(TimeSpan.FromMilliseconds(300));
        await waiting.Should().ThrowAsync<TimeoutException>();
        _organizer.Requests.Should().BeEmpty();
        File.Exists(from).Should().BeTrue();

        await held.DisposeAsync();

        (await refiling.WaitAsync(TimeSpan.FromSeconds(30))).Outcome.Should().Be(SongRefileOutcome.Refiled);
    }

    [Fact]
    public async Task An_album_chosen_while_the_file_moves_is_followed_by_one_more_re_file()
    {
        await using var context = await ContextAsync();
        var (song, _) = await SeedAsync(context, SourceTypes.Soulseek);
        await ChooseAlbumAsync(context, song, "r1", "Random Access Memories");

        // While the first move runs, the user picks another album.
        _organizer.OnOrganize = () =>
        {
            using var other = _database.CreateContext(_time);
            var album = other.AlbumContexts.Single(candidate => candidate.SongId == song);
            album.AlbumKey = "r2";
            album.AlbumTitle = "Discovery";
            other.SaveChanges();
        };

        var result = await Refiler(context).RefileAsync(song, CancellationToken.None);

        result.Outcome.Should().Be(SongRefileOutcome.Refiled);
        _organizer.Requests.Should().HaveCount(2);
        _organizer.Requests[1].Album.AlbumTitle.Should().Be("Discovery");

        await using var fresh = _database.CreateContext(_time);
        (await fresh.SongFiles.AsNoTracking().SingleAsync()).Path
            .Should().Be(Path.Combine(_root, "Daft Punk", "Discovery", "01 - Track 1.flac"));
    }

    [Fact]
    public async Task The_command_handler_refiles_the_song_its_body_names_and_fails_the_command_on_a_refusal()
    {
        await using var context = await ContextAsync();
        var (song, _) = await SeedAsync(context, SourceTypes.Soulseek);
        await ChooseAlbumAsync(context, song, "r1", "Random Access Memories");

        var services = new ServiceCollection();
        services.AddScoped(_ => _database.CreateContext(_time));
        services.AddScoped(provider => Refiler(provider.GetRequiredService<WondarrDbContext>()));
        services.AddScoped<ISongRefiler>(provider => provider.GetRequiredService<SongRefiler>());
        var handler = new RefileSongCommandHandler(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

        var command = new CommandContext(1, RefileSongCommandHandler.BodyFor(song), CommandTrigger.Manual, _ => Task.CompletedTask);

        var message = await handler.ExecuteAsync(command, CancellationToken.None);

        message.Should().Contain("new album's folder");
        File.Exists(Path.Combine(_root, "Daft Punk", "Random Access Memories", "01 - Track 1.flac")).Should().BeTrue();

        var missing = new CommandContext(2, RefileSongCommandHandler.BodyFor(98765), CommandTrigger.Manual, _ => Task.CompletedTask);
        var failing = async () => await handler.ExecuteAsync(missing, CancellationToken.None);

        await failing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*98765*");

        var noBody = new CommandContext(3, null, CommandTrigger.Manual, _ => Task.CompletedTask);
        var invalid = async () => await handler.ExecuteAsync(noBody, CancellationToken.None);

        await invalid.Should().ThrowAsync<ArgumentException>();
    }

    public void Dispose()
    {
        _database.Dispose();

        var parent = Path.GetDirectoryName(_root);

        if (parent is not null && Directory.Exists(parent))
        {
            Directory.Delete(parent, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private SongRefiler Refiler(WondarrDbContext context) =>
        new(
            context,
            _organizer,
            _disk,
            new RecycleBin(
                _disk,
                new TestOptionsMonitor<ImportOptions>(new ImportOptions { RecycleBinPath = _bin }),
                new WondarrPaths(_config),
                _time,
                NullLogger<RecycleBin>.Instance),
            _updater,
            new SongFileLock(),
            NullLogger<SongRefiler>.Instance);

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_time);

        var context = _database.CreateContext(_time);
        var library = await context.Libraries.SingleAsync(candidate => candidate.Id == SeedData.DefaultLibraryId);
        library.RootPath = _root;
        library.NamingTemplate = "{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}";
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return context;
    }

    /// <summary>A song filed under its single, with a real file (and sidecar) where the row says, when it has one.</summary>
    private async Task<(long SongId, string Path)> SeedAsync(
        WondarrDbContext context,
        string? sourceType,
        string? lyrics = null)
    {
        var song = await SongSeed.AddAsync(context, "Track 1");
        var album = await context.AlbumContexts.SingleAsync(candidate => candidate.SongId == song.Id);
        album.AlbumTitle = "Single 1";
        album.AlbumKey = "s-1";
        album.Kind = AlbumContextKind.Single;
        await context.SaveChangesAsync();

        var path = Path.Combine(_root, "Daft Punk", "Single 1", "01 - Track 1.flac");

        if (sourceType is not null)
        {
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
                QualityId = SongSeed.HighQuality,
                SourceType = sourceType,
                ImportedAt = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc),
            });
            await context.SaveChangesAsync();
        }

        context.ChangeTracker.Clear();

        return (song.Id, path);
    }

    /// <summary>What choosing an album writes to the song's context: the new album, pinned.</summary>
    private static async Task ChooseAlbumAsync(WondarrDbContext context, long songId, string key, string title)
    {
        var album = await context.AlbumContexts.SingleAsync(candidate => candidate.SongId == songId);
        album.AlbumKey = key;
        album.AlbumTitle = title;
        album.Kind = AlbumContextKind.Album;
        album.Pinned = true;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async Task WriteCoverAsync(string folder) =>
        await File.WriteAllTextAsync(Path.Combine(folder, "cover.jpg"), "jpeg bytes");

    /// <summary>
    /// The organizer an import uses, without the tag writer and the placer: it moves the source to the
    /// folder the album names, "writes" the album tag, and reports success (or the failure it is told to).
    /// </summary>
    private sealed class MovingOrganizer(string root, IDiskOperations disk) : ILibraryOrganizer
    {
        public List<OrganizeRequest> Requests { get; } = [];

        public bool Fail { get; set; }

        /// <summary>Called as the organizer starts, before it moves anything.</summary>
        public Action? OnOrganize { get; set; }

        public string? ErrorAfterMove { get; set; }

        public Task<OrganizeResult> OrganizeAsync(OrganizeRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            Requests.Add(request);
            OnOrganize?.Invoke();
            OnOrganize = null;

            if (Fail)
            {
                return Task.FromResult(
                    new OrganizeResult(OrganizeFailure.Tagging, "the tag writer refused", null, null, new Dictionary<string, string>()));
            }

            var directory = Path.Combine(root, request.Album.AlbumArtist, request.Album.AlbumTitle);
            var target = Path.Combine(directory, Path.GetFileName(request.SourcePath));

            disk.CreateDirectory(directory);
            disk.MoveFile(request.SourcePath, target);

            var tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["ALBUM"] = request.Album.AlbumTitle };

            return Task.FromResult(
                ErrorAfterMove is null
                    ? new OrganizeResult(OrganizeFailure.None, null, target, null, tags)
                    : new OrganizeResult(OrganizeFailure.Placement, ErrorAfterMove, target, null, tags));
        }
    }
}
