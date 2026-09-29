using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.Persistence;
using Wondarr.Core.References;
using Wondarr.Core.Tagging;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.References;

/// <summary>
/// The incremental walk of a reference library: one row per file, re-read only when its size or
/// modification time moved, and never a change to the folder itself.
/// </summary>
public sealed class ReferenceScannerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("wondarr-reference-").FullName;
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly IMediaProbe _probe = Substitute.For<IMediaProbe>();
    private readonly ITagReader _tags = Substitute.For<ITagReader>();

    public ReferenceScannerTests()
    {
        _probe.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MediaProbeResult(
                true,
                new MediaInfo("mp3", "mp3", 320, 44100, null, 2, 210_000, false, 1024),
                null)));
    }

    [Fact]
    public async Task The_first_scan_adds_one_pending_row_per_audio_file()
    {
        Write("a.mp3", "one");
        Write(Path.Combine("Album", "b.flac"), "two");
        Write(".hidden.mp3", "hidden");
        Write("notes.txt", "not audio");
        Write(Path.Combine("@eaDir", "c.mp3"), "nas metadata");
        _tags.Read(Arg.Any<string>()).Returns(new FileTags(
            "Get Lucky", "Daft Punk", "Daft Punk", "Random Access Memories", "2013-05-17", null,
            null, null, null, null, null, 8, 13, 1, 210_000));

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);

        var result = await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        result.Should().Be(new ReferenceScanResult(Seen: 2, Added: 2, Changed: 0, Unchanged: 0, Missing: 0, Unreadable: 0));

        var rows = await database.ReferenceFiles.OrderBy(row => row.RelativePath).ToListAsync();
        rows.Select(row => row.RelativePath).Should().Equal("Album/b.flac", "a.mp3");
        rows.Should().OnlyContain(row => row.State == ReferenceFileState.Pending);
        rows.Should().OnlyContain(row => row.Probe != null && row.Tags != null);
        rows.Should().OnlyContain(row => row.LastSeenAt == Start.UtcDateTime);
        rows.Should().OnlyContain(row => row.Size > 0 && row.ModifiedAt.Kind == DateTimeKind.Utc);

        library.LastScannedAt.Should().Be(Start.UtcDateTime);
        library.LastScanMessage.Should().Contain("2 files");
    }

    [Fact]
    public async Task A_second_scan_of_an_unchanged_library_probes_nothing()
    {
        Write("a.mp3", "one");
        Write(Path.Combine("Album", "b.flac"), "two");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        var scanner = Scanner(database);

        await scanner.ScanAsync(library.Id, null, CancellationToken.None);
        var second = await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        second.Should().Be(new ReferenceScanResult(Seen: 2, Added: 0, Changed: 0, Unchanged: 2, Missing: 0, Unreadable: 0));
        await _probe.Received(2).ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_touched_file_is_read_again_and_loses_its_candidates_but_keeps_its_song()
    {
        var path = Write("a.mp3", "one");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        var songId = await AddSongAsync(database);
        var row = await database.ReferenceFiles.SingleAsync();
        row.SongId = songId;
        row.State = ReferenceFileState.Identified;
        row.Fingerprint = "fingerprint";
        row.Confidence = 0.9;
        row.IdentifiedBy = "tag_mbid";
        database.MatchCandidates.Add(new MatchCandidate { ReferenceFileId = row.Id, Rank = 1, Identity = "{}", Score = 0.9, Reason = "tags" });
        await database.SaveChangesAsync();

        File.SetLastWriteTimeUtc(path, Start.UtcDateTime.AddHours(1));

        var result = await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        result.Should().Be(new ReferenceScanResult(Seen: 1, Added: 0, Changed: 1, Unchanged: 0, Missing: 0, Unreadable: 0));

        var changed = await database.ReferenceFiles.SingleAsync();
        changed.State.Should().Be(ReferenceFileState.Pending);
        changed.SongId.Should().Be(songId, "the file is the same song, only its bytes moved");
        changed.Fingerprint.Should().BeNull();
        changed.IdentifiedBy.Should().BeNull();
        changed.Confidence.Should().Be(0);
        (await database.MatchCandidates.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_deleted_file_becomes_missing_and_goes_back_when_it_returns()
    {
        var path = Write("a.mp3", "one");
        // A second file keeps the walk non-empty: an empty root is the unmounted-share case, tested below.
        Write("keep.mp3", "two");
        var bytes = await File.ReadAllBytesAsync(path);
        var modified = File.GetLastWriteTimeUtc(path);

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        var scanner = Scanner(database);
        await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        File.Delete(path);
        var deleted = await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        deleted.Should().Be(new ReferenceScanResult(Seen: 1, Added: 0, Changed: 0, Unchanged: 1, Missing: 1, Unreadable: 0));

        var missing = await database.ReferenceFiles.SingleAsync(row => row.RelativePath == "a.mp3");
        missing.State.Should().Be(ReferenceFileState.Missing);
        missing.MissingSince.Should().Be(Start.UtcDateTime);

        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, modified);

        var restored = await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        restored.Should().Be(new ReferenceScanResult(Seen: 2, Added: 0, Changed: 0, Unchanged: 2, Missing: 0, Unreadable: 0));

        var back = await database.ReferenceFiles.SingleAsync(row => row.RelativePath == "a.mp3");
        back.State.Should().Be(ReferenceFileState.Pending);
        back.MissingSince.Should().BeNull();
    }

    [Fact]
    public async Task A_second_scan_through_a_fresh_context_still_sees_nothing_changed()
    {
        Write("a.mp3", "one");
        Write("Artist/Album/01 - b.flac", "two");

        long libraryId;
        await using (var first = await ContextAsync())
        {
            var library = await AddLibraryAsync(first, _root);
            libraryId = library.Id;
            await Scanner(first).ScanAsync(libraryId, null, CancellationToken.None);
        }

        _probe.ClearReceivedCalls();

        // The sizes and times now come back from SQLite, not from the tracked rows of the first scan:
        // this is what a daily scan does.
        await using var second = _database.CreateContext(_time);
        var result = await Scanner(second).ScanAsync(libraryId, null, CancellationToken.None);

        result.Should().Be(new ReferenceScanResult(Seen: 2, Added: 0, Changed: 0, Unchanged: 2, Missing: 0, Unreadable: 0));
        await _probe.DidNotReceiveWithAnyArgs().ProbeAsync(default!, default);
    }

    [Fact]
    public async Task An_adopted_file_that_left_the_folder_stays_adopted()
    {
        var path = Write("a.mp3", "one");
        Write("keep.mp3", "two");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        var scanner = Scanner(database);
        await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        var adopted = await database.ReferenceFiles.SingleAsync(row => row.RelativePath == "a.mp3");
        adopted.State = ReferenceFileState.Adopted;
        await database.SaveChangesAsync();

        // Adoption moves the file into the managed library; its absence here is the point.
        File.Delete(path);
        var result = await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        result.Missing.Should().Be(0);
        (await database.ReferenceFiles.SingleAsync(row => row.RelativePath == "a.mp3")).State
            .Should().Be(ReferenceFileState.Adopted);
    }

    [Theory]
    [InlineData("Artist/Album/01.mp3", true)]
    [InlineData("Artist/Album", true)]
    [InlineData("Artist/Albums/01.mp3", false)]
    [InlineData("Other/01.mp3", false)]
    [InlineData("loose.mp3", true)]
    public void A_row_under_an_entry_the_walk_skipped_is_left_alone(string relativePath, bool underSkipped)
    {
        ReferenceScanner.IsUnderSkipped(relativePath, ["Artist/Album", "loose.mp3"]).Should().Be(underSkipped);
    }

    [Fact]
    public async Task A_root_that_is_gone_throws_and_changes_no_row()
    {
        Write("a.mp3", "one");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        library.RootPath = Path.Combine(_root, "not-mounted");

        var act = async () => await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        await act.Should().ThrowAsync<ReferenceLibraryUnavailableException>();
        (await database.ReferenceFiles.SingleAsync()).State.Should().Be(ReferenceFileState.Pending);
    }

    [Fact]
    public async Task An_empty_root_with_rows_throws_rather_than_marking_them_missing()
    {
        var path = Write("a.mp3", "one");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        // A share that dropped out of the mount table reads exactly like this.
        File.Delete(path);

        var act = async () => await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ReferenceLibraryUnavailableException>();
        exception.Which.Message.Should().Contain("share mounted");

        (await database.ReferenceFiles.SingleAsync()).State.Should().Be(ReferenceFileState.Pending);
        library.LastScanMessage.Should().Contain("share mounted");
    }

    [Fact]
    public async Task A_library_that_does_not_exist_throws()
    {
        await using var database = await ContextAsync();

        var act = async () => await Scanner(database).ScanAsync(404, null, CancellationToken.None);

        await act.Should().ThrowAsync<ReferenceLibraryUnavailableException>();
    }

    [Fact]
    public async Task A_file_that_does_not_decode_is_unreadable()
    {
        Write("broken.mp3", "one");
        _probe.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MediaProbeResult(false, null, "Invalid data found when processing input")));

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);

        var result = await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        result.Unreadable.Should().Be(1);

        var row = await database.ReferenceFiles.SingleAsync();
        row.State.Should().Be(ReferenceFileState.Unreadable);
        row.Message.Should().Contain("Invalid data");
        row.Probe.Should().BeNull();
    }

    [Fact]
    public async Task A_link_inside_the_root_is_not_followed()
    {
        Write("a.mp3", "one");
        var loop = Path.Combine(_root, "loop");

        try
        {
            Directory.CreateSymbolicLink(loop, _root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Creating a link needs a privilege Windows only grants to administrators or with
            // Developer Mode on; without it there is nothing to test here.
            return;
        }

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);

        var result = await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        result.Seen.Should().Be(1, "the walk must not follow the link back into the folder it came from");
    }

    [Fact]
    public async Task The_scan_reports_progress_and_says_so_in_the_library()
    {
        Write("a.mp3", "one");
        Write("b.mp3", "two");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        var messages = new List<string>();

        await Scanner(database).ScanAsync(library.Id, message =>
        {
            messages.Add(message);
            return Task.CompletedTask;
        }, CancellationToken.None);

        messages.Should().NotBeEmpty();
        messages.Should().OnlyContain(message => message.StartsWith("Scanned ", StringComparison.Ordinal));
        library.LastScanMessage.Should().Be("2 files: 2 added, 0 changed, 0 unchanged, 0 missing, 0 unreadable");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _database.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder under the OS temp directory is harmless.
        }
    }

    /// <summary>A migrated database, as the startup migrator leaves one.</summary>
    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_time);

        return _database.CreateContext(_time);
    }

    private ReferenceScanner Scanner(WondarrDbContext database) =>
        new(database, _probe, _tags, _time, NullLogger<ReferenceScanner>.Instance);

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, Start.UtcDateTime);

        return path;
    }

    private static async Task<ReferenceLibrary> AddLibraryAsync(WondarrDbContext database, string root)
    {
        var library = new ReferenceLibrary { Name = "Music", RootPath = root };
        database.ReferenceLibraries.Add(library);
        await database.SaveChangesAsync();

        return library;
    }

    private static async Task<long> AddSongAsync(WondarrDbContext database)
    {
        var song = new Song
        {
            Title = "Get Lucky",
            ArtistCredit = "Daft Punk",
            PrimaryArtist = new Artist { Name = "Daft Punk", SortName = "Daft Punk" },
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };

        database.Songs.Add(song);
        await database.SaveChangesAsync();

        return song.Id;
    }
}