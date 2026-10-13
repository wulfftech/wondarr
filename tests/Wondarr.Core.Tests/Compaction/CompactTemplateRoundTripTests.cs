using System.Globalization;
using Wondarr.Core.Compaction;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Importing;
using Wondarr.Core.Lyrics;
using Wondarr.Core.Media;
using Wondarr.Core.Metadata.CoverArt;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Wondarr.Core.Tagging;
using Wondarr.Core.Tests.Organizer;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Compaction;

/// <summary>
/// The template pass end to end on a real temp folder: plan, run the executor (with the real organizer
/// and placer, only the tag writer faked), plan again. The second plan must be empty whatever the
/// titles, extensions and folders look like, or Compact would move the same files on every run.
/// </summary>
[Collection(CompactExecutorTests.Serial)]
public sealed class CompactTemplateRoundTripTests : IDisposable
{
    private const long FlacQualityId = 36;

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new() { AutoAdvanceAmount = TimeSpan.FromSeconds(2) };
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "wondarr-roundtrip-tests", Guid.NewGuid().ToString("N"));

    private string Root => Path.Combine(_temp, "music");

    /// <summary>One song to seed: where its file starts and what the library knows about it.</summary>
    private sealed record Spec(
        string Title,
        string Album = "Random Access Memories",
        string OldFolder = "Daft Punk/Random Access Memories",
        string OldFile = "old.flac",
        string Date = "2013-05-17",
        int Disc = 1,
        int Track = 1,
        string Key = "r1");

    public static TheoryData<string> Cases { get; } =
    [
        "plain title",
        "trailing dot",
        "trailing space",
        "reserved device name",
        "illegal characters",
        "300 character title",
        "title that looks like a collision",
        "upper-case extension",
        "decomposed unicode folder",
        "disc 2 with disc 1's title",
        "file already named (100)",
        "siblings whose dates disagree",
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Plan_run_plan_leaves_nothing_to_move(string name)
    {
        var specs = SpecsFor(name);

        await using var context = await ContextAsync();
        var artistId = await SeedArtistAsync(context);
        var index = 0;

        foreach (var spec in specs)
        {
            await SeedSongAsync(context, artistId, ++index, spec);
        }

        var planner = NewPlanner(context);
        var first = await planner.PlanAsync(SeedData.DefaultLibraryId, CancellationToken.None);

        first.Moves.Should().NotBeEmpty($"{name}: the files start in the wrong place");

        var plannedTo = first.Moves.ToDictionary(move => move.SongId, move => move.ToPath!);

        var result = await NewExecutor(context, planner).RunAsync(SeedData.DefaultLibraryId, null, CancellationToken.None);

        result.Failed.Should().Be(0, name);
        result.Moved.Should().Be(first.Moves.Count, name);

        // Every file is where the plan said (or at that name's collision variant), and exists.
        await using var fresh = _database.CreateContext(_time);
        var files = await fresh.SongFiles.AsNoTracking().ToListAsync();

        foreach (var file in files)
        {
            File.Exists(file.Path).Should().BeTrue($"{name}: {file.Path}");

            if (plannedTo.TryGetValue(file.SongId, out var planned))
            {
                CompactPlanner.IsInPlace(file.Path, planned).Should().BeTrue($"{name}: {file.Path} vs plan {planned}");
            }
        }

        // And the second plan has nothing left, so a second Compact would do nothing.
        var second = await NewPlanner(fresh).PlanAsync(SeedData.DefaultLibraryId, CancellationToken.None);

        second.Moves.Should().BeEmpty($"{name}: a second run must plan zero moves");
    }

    private static List<Spec> SpecsFor(string name) => name switch
    {
        "plain title" => [new("Song")],
        "trailing dot" => [new("Song.")],
        "trailing space" => [new("Song ")],
        "reserved device name" => [new("CON")],
        "illegal characters" => [new("A:B?")],
        "300 character title" => [new(new string('x', 300))],
        "title that looks like a collision" => [new("Song (2)")],
        "upper-case extension" => [new("Song", OldFile: "old.FLAC")],
        "decomposed unicode folder" => [new("Song", Album: "Café", OldFolder: "Daft Punk/Café")],
        "disc 2 with disc 1's title" => [new("Song", Disc: 1, Track: 1), new("Song", Disc: 2, Track: 1, OldFile: "old2.flac")],
        "file already named (100)" => [new("Song", OldFolder: "Daft Punk/Random Access Memories (2013)", OldFile: "01 - Song (100).flac")],
        "siblings whose dates disagree" =>
        [
            new("Song One", Track: 1, Date: "2013-05-17"),
            new("Song Two", Track: 2, Date: "2014-02-02", OldFile: "old2.flac"),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    public void Dispose()
    {
        _database.Dispose();

        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_time);

        var context = _database.CreateContext(_time);
        var library = await context.Libraries.SingleAsync(candidate => candidate.Id == SeedData.DefaultLibraryId);

        // The seeded Plexamp copy: it follows the preset, which is the point of the whole pass.
        library.RootPath = Root;
        library.PlexSectionId = null;
        library.PlexLibraryPath = null;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Directory.CreateDirectory(Root);

        return context;
    }

    private static async Task<long> SeedArtistAsync(WondarrDbContext context)
    {
        var artist = new Artist { Name = "Daft Punk", SortName = "Daft Punk", MbArtistId = "a1" };

        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        return artist.Id;
    }

    private async Task SeedSongAsync(WondarrDbContext context, long artistId, int index, Spec spec)
    {
        var song = new Song
        {
            Title = spec.Title,
            ArtistCredit = "Daft Punk",
            PrimaryArtistId = artistId,
            MbRecordingId = "m-" + index.ToString(CultureInfo.InvariantCulture),
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };

        context.Songs.Add(song);
        await context.SaveChangesAsync();

        context.SongArtists.Add(new SongArtist { SongId = song.Id, ArtistId = artistId, Role = ArtistRole.Main, Position = 1 });

        // Pinned: the re-plan leaves the album alone, so only the folder template is under test.
        context.AlbumContexts.Add(new AlbumContext
        {
            SongId = song.Id,
            Kind = AlbumContextKind.Album,
            AlbumTitle = spec.Album,
            AlbumArtist = "Daft Punk",
            AlbumKey = spec.Key,
            MbReleaseId = spec.Key,
            MbReleaseGroupId = spec.Key + "-group",
            TrackNo = spec.Track,
            DiscNo = spec.Disc,
            TotalTracks = 2,
            Date = spec.Date,
            Pinned = true,
        });

        var path = Path.Combine(Root, spec.OldFolder.Replace('/', Path.DirectorySeparatorChar), spec.OldFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "the audio bytes " + index.ToString(CultureInfo.InvariantCulture));

        context.SongFiles.Add(new SongFile
        {
            SongId = song.Id,
            Path = path,
            Size = new FileInfo(path).Length,
            Codec = "flac",
            Container = "flac",
            BitrateKbps = 1000,
            SampleRate = 44_100,
            BitDepth = 16,
            Channels = 2,
            DurationMs = 369_000,
            QualityId = FlacQualityId,
            SourceType = SourceTypes.Soulseek,
            ImportedAt = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc),
        });

        await context.SaveChangesAsync();
    }

    private static CompactPlanner NewPlanner(WondarrDbContext context)
    {
        var counter = 0;
        var songs = new SongService(
            context,
            Substitute.For<IIdentityResolver>(),
            new AlbumPolicyEngine(() => new Guid(++counter, 0, 0, new byte[8])),
            Substitute.For<IMusicBrainzClient>(),
            Substitute.For<ICoverArtResolver>(),
            Wondarr.Core.Tests.Songs.SearchOnAddOff.Commands,
            Wondarr.Core.Tests.Songs.SearchOnAddOff.Options,
            NullLogger<SongService>.Instance);

        return new CompactPlanner(context, songs);
    }

    private CompactExecutor NewExecutor(WondarrDbContext context, ICompactPlanner planner)
    {
        IDiskOperations disk = new DiskOperations();
        var options = new TestOptionsMonitor<ImportOptions>(new ImportOptions { RecycleBinPath = Path.Combine(_temp, "recycle") });
        var recycleBin = new RecycleBin(disk, options, new WondarrPaths(Path.Combine(_temp, "config")), _time, NullLogger<RecycleBin>.Instance);
        var placer = new FilePlacer(disk, recycleBin, options, NullLogger<FilePlacer>.Instance);

        var tagWriter = Substitute.For<ITagWriter>();
        tagWriter
            .WriteAsync(Arg.Any<string>(), Arg.Any<TagSet>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TagWriteResult(true, null, new Dictionary<string, string>())));

        var covers = Substitute.For<ICoverFetcher>();
        covers.FetchAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<byte[]?>(null));

        var organizer = new LibraryOrganizer(
            tagWriter,
            placer,
            covers,
            disk,
            context,
            Substitute.For<ICoverImageProcessor>(),
            Substitute.For<ILrclibClient>(),
            new TestOptionsMonitor<LyricsOptions>(new LyricsOptions { Enabled = false }),
            options,
            NullLogger<LibraryOrganizer>.Instance);

        return new CompactExecutor(
            context,
            planner,
            organizer,
            disk,
            recycleBin,
            Substitute.For<IPlexConnectionService>(),
            Substitute.For<IPlexServerClient>(),
            Substitute.For<IPlexLibraryUpdater>(),
            _time,
            new SongFileLock(),
            NullLogger<CompactExecutor>.Instance);
    }
}
