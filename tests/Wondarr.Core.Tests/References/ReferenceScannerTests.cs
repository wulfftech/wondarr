using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Media;
using Wondarr.Core.Persistence;
using Wondarr.Core.References;
using Wondarr.Core.Sources;
using Wondarr.Core.Tagging;
using Wondarr.Testing;
using Wondarr.Core.Tests.Media;
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
        _probe.ProbeReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
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
        await _probe.Received(2).ProbeReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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

    // --- Likely YouTube rips, and files a library copy replaced (DECISIONS build session 14 #32) -------

    [Fact]
    public async Task A_lossy_file_at_48_kHz_is_flagged_a_likely_youtube_rip()
    {
        Write("rip.mp3", "one");
        Write("cd.mp3", "two");
        Write("studio.flac", "three");

        _probe.ProbeReferenceAsync(Arg.Is<string>(path => path.EndsWith("rip.mp3", StringComparison.Ordinal)), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MediaProbeResult(
                true,
                new MediaInfo("mp3", "mp3", 268, 48_000, null, 2, 231_811, false, 7_857_630),
                null)));
        _probe.ProbeReferenceAsync(Arg.Is<string>(path => path.EndsWith("studio.flac", StringComparison.Ordinal)), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MediaProbeResult(
                true,
                new MediaInfo("flac", "flac", 1411, 48_000, 24, 2, 231_811, true, 40_000_000),
                null)));

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        var flags = await database.ReferenceFiles.AsNoTracking()
            .ToDictionaryAsync(row => row.RelativePath, row => row.LikelyYouTubeRip);

        flags.Should().BeEquivalentTo(new Dictionary<string, bool>
        {
            ["rip.mp3"] = true,
            ["cd.mp3"] = false,
            ["studio.flac"] = false,
        }, "only a lossy file at 48 kHz is a likely rip; 48 kHz is a real rate for a lossless file");
    }

    [Fact]
    public async Task A_changed_file_keeps_its_replace_answer()
    {
        var path = Write("a.mp3", "one");
        Write("keep.mp3", "two");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        var scanner = Scanner(database);
        await scanner.ScanAsync(library.Id, null, CancellationToken.None);
        await OwnAsync(database, "a.mp3");

        var row = await database.ReferenceFiles.SingleAsync(candidate => candidate.RelativePath == "a.mp3");
        row.Replace = true;
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        // Retagged in Picard: the bytes change, the recording does not.
        File.WriteAllText(path, "retagged");
        File.SetLastWriteTimeUtc(path, Start.UtcDateTime.AddHours(1));
        await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        (await database.ReferenceFiles.AsNoTracking().SingleAsync(candidate => candidate.RelativePath == "a.mp3"))
            .Replace.Should().BeTrue("a rescan must not quietly drop what the user asked for");
    }

    [Fact]
    public async Task Deleting_files_a_library_copy_replaced_is_no_mount_problem_and_their_rows_go_after_the_grace()
    {
        var names = Enumerable.Range(1, 6).Select(number => $"song{number}.mp3").ToList();

        foreach (var name in names)
        {
            Write(name, name);
        }

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        var scanner = Scanner(database);
        await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        foreach (var name in names)
        {
            var songId = await OwnAsync(database, name);

            // Five of the six were replaced by a download: the song holds a library file now.
            if (name != names[0])
            {
                await ReplaceWithLibraryCopyAsync(database, songId);
            }
        }

        // The user deletes the five replaced rips at once, which is more than the guard allows for owned files.
        foreach (var name in names.Skip(1))
        {
            File.Delete(Path.Combine(_root, name));
        }

        var result = await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        result.MountProblem.Should().BeFalse("a replaced file owns nothing, so deleting it says nothing about the share");
        result.Missing.Should().Be(5);
        (await new ReferenceMissingFilesHealthCheck(database).CheckAsync(CancellationToken.None)).Type
            .Should().Be(HealthCheckResult.Ok);
        (await database.ReferenceFiles.AsNoTracking().CountAsync()).Should().Be(6, "a share that drops out for a scan keeps its rows");

        // A day later the files are still gone: their rows go, and the owned file and its song stay.
        _time.Advance(TimeSpan.FromHours(25));
        var later = await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        later.Released.Should().Be(0);
        (await database.ReferenceFiles.AsNoTracking().Select(row => row.RelativePath).ToListAsync())
            .Should().Equal(names[0]);
    }

    [Fact]
    public async Task Under_a_mount_problem_a_replaced_file_that_was_not_seen_is_only_missing()
    {
        var names = Enumerable.Range(1, 6).Select(number => $"song{number}.mp3").ToList();

        foreach (var name in names)
        {
            Write(name, name);
        }

        Write("replaced.mp3", "replaced");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        var scanner = Scanner(database);
        await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        foreach (var name in names)
        {
            await OwnAsync(database, name);
        }

        await ReplaceWithLibraryCopyAsync(database, await OwnAsync(database, "replaced.mp3"));

        // Half the share drops out, the replaced file with it: the disk is not trusted.
        foreach (var name in names.Skip(1).Append("replaced.mp3"))
        {
            File.Delete(Path.Combine(_root, name));
        }

        var result = await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        result.MountProblem.Should().BeTrue();
        var replaced = await database.ReferenceFiles.AsNoTracking().SingleAsync(row => row.RelativePath == "replaced.mp3");
        replaced.State.Should().Be(ReferenceFileState.Missing, "a later scan that trusts the disk lets it go");

        // Days later the share is still half gone: the disk is still not trusted, so the row stays.
        _time.Advance(TimeSpan.FromDays(2));
        (await scanner.ScanAsync(library.Id, null, CancellationToken.None)).MountProblem.Should().BeTrue();
        (await database.ReferenceFiles.AsNoTracking().AnyAsync(row => row.RelativePath == "replaced.mp3")).Should().BeTrue();

        // The share comes back, all but the replaced file the user deleted: that scan trusts the disk.
        foreach (var name in names.Skip(1))
        {
            Write(name, name);
        }

        await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        (await database.ReferenceFiles.AsNoTracking().AnyAsync(row => row.RelativePath == "replaced.mp3"))
            .Should().BeFalse("the scan trusts the disk again and the file stayed gone past the grace");
    }

    // --- A missing file and the song it owns -----------------------------------------------------

    [Fact]
    public async Task A_file_that_stays_missing_for_two_scans_a_day_apart_makes_its_song_wanted_again()
    {
        var path = Write("a.mp3", "one");
        Write("keep.mp3", "two");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        var scanner = Scanner(database);
        await scanner.ScanAsync(library.Id, null, CancellationToken.None);
        var songId = await OwnAsync(database, "a.mp3");

        File.Delete(path);

        // The first scan that misses the file only notices.
        var first = await scanner.ScanAsync(library.Id, null, CancellationToken.None);
        first.Released.Should().Be(0);
        (await SongFileCountAsync(database, songId)).Should().Be(1, "the song keeps its file through the grace period");

        // A later scan, but not yet a day on: still held.
        _time.Advance(TimeSpan.FromHours(23));
        (await scanner.ScanAsync(library.Id, null, CancellationToken.None)).Released.Should().Be(0);
        (await SongFileCountAsync(database, songId)).Should().Be(1);

        // A later scan, a day after the file was first missed: the song is let go in the same save.
        _time.Advance(TimeSpan.FromHours(2));
        var released = await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        released.Released.Should().Be(1);
        released.MountProblem.Should().BeFalse();
        (await SongFileCountAsync(database, songId)).Should().Be(0);

        var row = await database.ReferenceFiles.AsNoTracking().SingleAsync(candidate => candidate.RelativePath == "a.mp3");
        row.SongId.Should().BeNull();
        row.State.Should().Be(ReferenceFileState.Missing);

        // Wanted: monitored, no file and no Identified reference row owns it.
        (await database.Songs.AsNoTracking().AnyAsync(song =>
            song.Id == songId
            && song.Monitored
            && song.File == null
            && !database.ReferenceFiles.Any(file => file.SongId == song.Id && file.State == ReferenceFileState.Identified)))
            .Should().BeTrue();
    }

    [Fact]
    public async Task A_second_copy_in_the_same_library_takes_over_the_song_instead_of_releasing_it()
    {
        var path = Write("a.mp3", "one");
        Write("b.mp3", "one, again");
        Write("keep.mp3", "two");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        var scanner = Scanner(database);
        await scanner.ScanAsync(library.Id, null, CancellationToken.None);
        var songId = await OwnAsync(database, "a.mp3");

        // b.mp3 is the same recording, Identified as a duplicate: the song's file is a.mp3's.
        var copy = await database.ReferenceFiles.SingleAsync(row => row.RelativePath == "b.mp3");
        copy.SongId = songId;
        copy.State = ReferenceFileState.Identified;
        copy.Message = "duplicate: the song already has a file";
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        File.Delete(path);
        await scanner.ScanAsync(library.Id, null, CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(25));
        var result = await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        result.Released.Should().Be(0, "the song is still owned through b.mp3");

        var file = await database.SongFiles.AsNoTracking().SingleAsync(candidate => candidate.SongId == songId);
        file.Path.Should().Be(Path.Combine(_root, "b.mp3"));
        file.SourceRef.Should().Contain($"\"referenceFileId\":{copy.Id}");

        var rows = await database.ReferenceFiles.AsNoTracking().ToListAsync();
        rows.Single(row => row.RelativePath == "a.mp3").SongId.Should().BeNull();
        rows.Single(row => row.RelativePath == "b.mp3").Message.Should().BeNull();
    }

    [Fact]
    public async Task A_second_copy_in_another_library_takes_over_the_song_instead_of_releasing_it()
    {
        var path = Write("a.mp3", "one");
        Write("keep.mp3", "two");
        var other = Directory.CreateTempSubdirectory("wondarr-reference-other-").FullName;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(other, "b.mp3"), "one, elsewhere");

            await using var database = await ContextAsync();
            var library = await AddLibraryAsync(database, _root);
            var second = new ReferenceLibrary { Name = "Other", RootPath = other };
            database.ReferenceLibraries.Add(second);
            await database.SaveChangesAsync();

            var scanner = Scanner(database);
            await scanner.ScanAsync(library.Id, null, CancellationToken.None);
            await scanner.ScanAsync(second.Id, null, CancellationToken.None);
            var songId = await OwnAsync(database, "a.mp3");

            var copy = await database.ReferenceFiles.SingleAsync(row => row.ReferenceLibraryId == second.Id);
            copy.SongId = songId;
            copy.State = ReferenceFileState.Identified;
            await database.SaveChangesAsync();
            database.ChangeTracker.Clear();

            File.Delete(path);
            await scanner.ScanAsync(library.Id, null, CancellationToken.None);
            _time.Advance(TimeSpan.FromHours(25));
            var result = await scanner.ScanAsync(library.Id, null, CancellationToken.None);

            result.Released.Should().Be(0);
            (await database.SongFiles.AsNoTracking().SingleAsync(candidate => candidate.SongId == songId)).Path
                .Should().Be(Path.Combine(other, "b.mp3"));
            (await database.ReferenceFiles.AsNoTracking().SingleAsync(row => row.RelativePath == "a.mp3")).SongId
                .Should().BeNull();
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task A_single_scan_never_releases_a_song_however_old_the_row_is()
    {
        var path = Write("a.mp3", "one");
        Write("keep.mp3", "two");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        var scanner = Scanner(database);
        await scanner.ScanAsync(library.Id, null, CancellationToken.None);
        var songId = await OwnAsync(database, "a.mp3");

        // The file went missing long ago as far as the clock goes, but no scan has seen it missing yet.
        _time.Advance(TimeSpan.FromDays(30));
        File.Delete(path);

        var result = await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        result.Missing.Should().Be(1);
        result.Released.Should().Be(0);
        (await SongFileCountAsync(database, songId)).Should().Be(1);
    }

    [Fact]
    public async Task A_file_that_comes_back_inside_the_grace_period_keeps_its_song()
    {
        var path = Write("a.mp3", "one");
        Write("keep.mp3", "two");
        var bytes = await File.ReadAllBytesAsync(path);

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        var scanner = Scanner(database);
        await scanner.ScanAsync(library.Id, null, CancellationToken.None);
        var songId = await OwnAsync(database, "a.mp3");

        File.Delete(path);
        await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, Start.UtcDateTime);
        _time.Advance(TimeSpan.FromDays(2));
        await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        var row = await database.ReferenceFiles.AsNoTracking().SingleAsync(candidate => candidate.RelativePath == "a.mp3");
        row.State.Should().Be(ReferenceFileState.Identified);
        row.SongId.Should().Be(songId);
        (await SongFileCountAsync(database, songId)).Should().Be(1);
    }

    [Fact]
    public async Task A_half_unmounted_share_releases_nothing_and_raises_a_warning_until_the_files_are_back()
    {
        var names = Enumerable.Range(1, 6).Select(number => $"song{number}.mp3").ToList();
        var contents = new Dictionary<string, byte[]>();

        foreach (var name in names)
        {
            contents[name] = await File.ReadAllBytesAsync(Write(name, name));
        }

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        var scanner = Scanner(database);
        await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        var songs = new List<long>();

        foreach (var name in names)
        {
            songs.Add(await OwnAsync(database, name));
        }

        var health = new ReferenceMissingFilesHealthCheck(database);
        (await health.CheckAsync(CancellationToken.None)).Type.Should().Be(HealthCheckResult.Ok);

        // Five of the six owned files vanish at once: more than the threshold allows.
        foreach (var name in names.Skip(1))
        {
            File.Delete(Path.Combine(_root, name));
        }

        var first = await scanner.ScanAsync(library.Id, null, CancellationToken.None);
        first.Missing.Should().Be(5);
        first.MountProblem.Should().BeTrue();

        // Days later the share is still gone: the clock has run out, but nothing is released.
        _time.Advance(TimeSpan.FromDays(3));
        var second = await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        second.MountProblem.Should().BeTrue();
        second.Released.Should().Be(0);
        (await database.ReferenceLibraries.AsNoTracking().SingleAsync()).LastScanMessage
            .Should().Contain("no song was released");

        foreach (var songId in songs)
        {
            (await SongFileCountAsync(database, songId)).Should().Be(1);
        }

        var warning = await health.CheckAsync(CancellationToken.None);
        warning.Type.Should().Be(HealthCheckResult.Warning);
        warning.Message.Should().Contain("Music").And.Contain("5 of 6");

        // The share comes back: the warning clears and the songs never noticed.
        foreach (var name in names.Skip(1))
        {
            var restored = Path.Combine(_root, name);
            await File.WriteAllBytesAsync(restored, contents[name]);
            File.SetLastWriteTimeUtc(restored, Start.UtcDateTime);
        }

        var third = await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        third.MountProblem.Should().BeFalse();
        third.Missing.Should().Be(0);
        (await health.CheckAsync(CancellationToken.None)).Type.Should().Be(HealthCheckResult.Ok);

        foreach (var songId in songs)
        {
            (await SongFileCountAsync(database, songId)).Should().Be(1);
        }
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(5, 3, false)]
    [InlineData(5, 4, true)]
    [InlineData(100, 10, false)]
    [InlineData(100, 11, true)]
    [InlineData(1000, 100, false)]
    [InlineData(1000, 101, true)]
    [InlineData(5000, 200, false)]
    [InlineData(5000, 201, true)]
    [InlineData(100000, 200, false)]
    [InlineData(100000, 201, true)]
    public void More_than_ten_percent_or_two_hundred_owned_files_missing_is_a_mount_problem(
        int owned,
        int missing,
        bool mountProblem) =>
        ReferenceMissingRules.IsMountProblem(owned, missing).Should().Be(mountProblem);

    // --- A changed file and the user's choice ------------------------------------------------------

    [Theory]
    [InlineData(ReferenceFileState.Skipped)]
    [InlineData(ReferenceFileState.Adopted)]
    public async Task A_changed_file_keeps_a_skipped_or_adopted_state(ReferenceFileState state)
    {
        var path = Write("a.mp3", "one");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        var songId = await AddSongAsync(database);
        var row = await database.ReferenceFiles.SingleAsync();
        row.State = state;
        row.SongId = songId;
        row.Message = "kept";
        row.Probe = null;
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        File.WriteAllText(path, "one, but longer now");
        File.SetLastWriteTimeUtc(path, Start.UtcDateTime.AddHours(1));

        var result = await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        result.Changed.Should().Be(1);

        var after = await database.ReferenceFiles.AsNoTracking().SingleAsync();
        after.State.Should().Be(state);
        after.SongId.Should().Be(songId);
        after.Message.Should().Be("kept");
        after.Size.Should().Be(new FileInfo(path).Length, "the size follows the file");
        after.ModifiedAt.Should().Be(Start.UtcDateTime.AddHours(1));
        after.Probe.Should().NotBeNull("the probe follows the file");
    }

    [Fact]
    public async Task A_changed_file_in_any_other_state_is_identified_again()
    {
        var path = Write("a.mp3", "one");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        var row = await database.ReferenceFiles.SingleAsync();
        row.State = ReferenceFileState.Unmatched;
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        File.SetLastWriteTimeUtc(path, Start.UtcDateTime.AddHours(1));
        await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        (await database.ReferenceFiles.AsNoTracking().SingleAsync()).State.Should().Be(ReferenceFileState.Pending);
    }

    // --- A file name with a backslash ----------------------------------------------------------------

    [LinuxFact]
    public async Task A_file_name_with_a_backslash_keeps_it_on_linux()
    {
        Write("AC\\DC.mp3", "thunder");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);

        await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        var row = await database.ReferenceFiles.AsNoTracking().SingleAsync();
        row.RelativePath.Should().Be("AC\\DC.mp3");
        File.Exists(Path.Combine(_root, row.RelativePath.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue();
        ReferenceScanner.RelativeKey("a\\b").Should().Be("a\\b");
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
        await _probe.DidNotReceiveWithAnyArgs().ProbeReferenceAsync(default!, default);
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
        _probe.ProbeReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
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
    public async Task A_file_that_decodes_with_a_few_errors_is_scanned_noted_and_logged()
    {
        var path = Write("Electric Avenue.mp3", "one");
        _probe.ProbeReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Tolerated(errors: 12, decoded: 315.4, duration: 316.0)));
        _tags.Read(Arg.Any<string>()).Returns(new FileTags(
            "Electric Avenue", "Eddy Grant", "Eddy Grant", "Killer on the Rampage", "1982", null,
            null, null, null, null, null, 1, 10, 1, 316_000));

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        var log = new RecordingLogger<ReferenceScanner>();

        var result = await Scanner(database, log).ScanAsync(library.Id, null, CancellationToken.None);

        result.Should().Be(new ReferenceScanResult(Seen: 1, Added: 1, Changed: 0, Unchanged: 0, Missing: 0, Unreadable: 0));

        var row = await database.ReferenceFiles.AsNoTracking().SingleAsync();
        row.State.Should().Be(ReferenceFileState.Pending, "it goes on to identification like any other file");
        row.Probe.Should().NotBeNull();
        row.Tags.Should().NotBeNull();
        row.Message.Should().Be("Decodes with 12 errors");

        var line = log.Entries.Should()
            .ContainSingle(entry => entry.Message.Contains("scanned anyway", StringComparison.Ordinal)).Subject;
        line.Level.Should().Be(LogLevel.Information);
        line.Message.Should().Be($"{path} decodes with 12 errors over 315.4s of 316.0s; scanned anyway");

        // The scan asks the reference question, never the strict one a download gets.
        await _probe.DidNotReceiveWithAnyArgs().ProbeAsync(default!, default);
    }

    [Fact]
    public async Task A_clean_file_gets_no_note_and_no_log_line()
    {
        Write("a.mp3", "one");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        var log = new RecordingLogger<ReferenceScanner>();

        await Scanner(database, log).ScanAsync(library.Id, null, CancellationToken.None);

        (await database.ReferenceFiles.AsNoTracking().SingleAsync()).Message.Should().BeNull();
        log.Entries.Should().NotContain(entry => entry.Message.Contains("scanned anyway", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_that_was_unreadable_is_scanned_once_the_probe_tolerates_it()
    {
        Write("Electric Avenue.mp3", "one");
        _probe.ProbeReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult(new MediaProbeResult(false, null, "Header missing")),
                _ => Task.FromResult(Tolerated(errors: 12, decoded: 315.4, duration: 316.0)));

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);

        var first = await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);
        first.Unreadable.Should().Be(1);

        var second = await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        second.Unreadable.Should().Be(0);

        var row = await database.ReferenceFiles.AsNoTracking().SingleAsync();
        row.State.Should().Be(ReferenceFileState.Pending);
        row.Message.Should().Be("Decodes with 12 errors");
    }

    [Fact]
    public async Task A_changed_skipped_file_follows_the_tolerant_probe_and_keeps_its_message()
    {
        var path = Write("a.mp3", "one");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        var row = await database.ReferenceFiles.SingleAsync();
        row.State = ReferenceFileState.Skipped;
        row.Probe = null;
        row.Message = "left alone";
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        File.SetLastWriteTimeUtc(path, Start.UtcDateTime.AddHours(1));
        _probe.ProbeReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Tolerated(errors: 3, decoded: 209.8, duration: 210.0)));

        await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        var after = await database.ReferenceFiles.AsNoTracking().SingleAsync();
        after.State.Should().Be(ReferenceFileState.Skipped);
        after.Message.Should().Be("left alone");
        after.Probe.Should().NotBeNull("the measurement follows the file");
    }

    [Fact]
    public async Task A_probe_that_failed_is_tried_again_on_the_next_scan_of_the_unchanged_file()
    {
        Write("song.mp3", "one");
        _probe.ProbeReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<MediaProbeResult>(new TimeoutException("ffprobe timed out")),
                _ => Task.FromResult(new MediaProbeResult(
                    true,
                    new MediaInfo("mp3", "mp3", 320, 44100, null, 2, 210_000, false, 1024),
                    null)));

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);

        var first = await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);
        first.Unreadable.Should().Be(1);

        var second = await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        second.Unchanged.Should().Be(1);
        second.Unreadable.Should().Be(0);

        var row = await database.ReferenceFiles.AsNoTracking().SingleAsync();
        row.State.Should().Be(ReferenceFileState.Pending);
        row.Message.Should().BeNull();
        row.Probe.Should().NotBeNull();
    }

    [Fact]
    public async Task An_unchanged_unreadable_file_is_counted_as_unreadable_again()
    {
        Write("broken.mp3", "one");
        _probe.ProbeReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MediaProbeResult(false, null, "Invalid data found when processing input")));

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);

        await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);
        var second = await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        second.Unreadable.Should().Be(1);
        second.Changed.Should().Be(0);
        (await database.ReferenceFiles.AsNoTracking().SingleAsync()).State.Should().Be(ReferenceFileState.Unreadable);
    }

    [Fact]
    public async Task An_unchanged_skipped_file_stays_skipped()
    {
        Write("a.mp3", "one");

        await using var database = await ContextAsync();
        var library = await AddLibraryAsync(database, _root);
        await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        var row = await database.ReferenceFiles.SingleAsync();
        row.State = ReferenceFileState.Skipped;
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        await Scanner(database).ScanAsync(library.Id, null, CancellationToken.None);

        (await database.ReferenceFiles.AsNoTracking().SingleAsync()).State.Should().Be(ReferenceFileState.Skipped);
    }

    [SymlinkFact]
    public async Task A_link_inside_the_root_is_not_followed()
    {
        Write("a.mp3", "one");
        var loop = Path.Combine(_root, "loop");

        // SymlinkFact has already skipped this where links cannot be created, so a failure here is real.
        Directory.CreateSymbolicLink(loop, _root);

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

    private ReferenceScanner Scanner(WondarrDbContext database, ILogger<ReferenceScanner>? logger = null) =>
        new(database, _probe, _tags, _time, logger ?? NullLogger<ReferenceScanner>.Instance);

    /// <summary>What the probe answers for a file with a few bad frames that a reference scan puts up with.</summary>
    private static MediaProbeResult Tolerated(int errors, double decoded, double duration) =>
        new(
            true,
            new MediaInfo("mp3", "mp3", 128, 44100, null, 2, (int)(duration * 1000), false, 1024),
            null,
            Tolerated: new ToleratedDecodeErrors(errors, decoded, duration));

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

    /// <summary>
    /// Makes a scanned file the file of a new song exactly as identification leaves it: the row is
    /// <c>Identified</c> and carries the song, and the song holds a reference <c>song_file</c> at the
    /// file's path.
    /// </summary>
    private async Task<long> OwnAsync(WondarrDbContext database, string relativePath)
    {
        var row = await database.ReferenceFiles.SingleAsync(candidate => candidate.RelativePath == relativePath);
        var songId = await AddSongAsync(database);

        database.SongFiles.Add(new SongFile
        {
            SongId = songId,
            Path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            SourceType = SourceTypes.Reference,
            SourceRef = $"{{\"referenceLibraryId\":{row.ReferenceLibraryId},\"referenceFileId\":{row.Id}}}",
            QualityId = 1,
        });

        row.SongId = songId;
        row.State = ReferenceFileState.Identified;
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        return songId;
    }

    /// <summary>Points a song at a library file, as an import of a download does: its reference file is replaced.</summary>
    private static async Task ReplaceWithLibraryCopyAsync(WondarrDbContext database, long songId)
    {
        var file = await database.SongFiles.SingleAsync(candidate => candidate.SongId == songId);
        file.Path = $"/data/music/{songId}.flac";
        file.SourceType = SourceTypes.Soulseek;
        file.SourceRef = null;
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
    }

    private static Task<int> SongFileCountAsync(WondarrDbContext database, long songId) =>
        database.SongFiles.AsNoTracking().CountAsync(file => file.SongId == songId);

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
