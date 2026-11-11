using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;
using Wondarr.Core.Searching;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.Songs;

/// <summary>Taking a wrong file away from its song, against a real migrated SQLite database and real files.</summary>
public sealed class SongFileRemoverTests : IDisposable
{
    private const string RemotePath = @"@@peer1\_Music - Various Artists (compilations)\Artur - Wonderland (Vyze)\01 Artur - Wonderland.mp3";

    private const string CompilationPath = @"@@peer1\Various Artists\Nuggets\Disc 1\1-18 Wonderland.mp3";

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wondarr-tests", Guid.NewGuid().ToString("N"));
    private readonly IRecycleBin _bin = Substitute.For<IRecycleBin>();
    private readonly IPlexLibraryUpdater _plex = Substitute.For<IPlexLibraryUpdater>();

    public SongFileRemoverTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "music", "The Saddle Club", "Singles"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));

        // The bin moves the file, as the real one does, so the test sees where it went.
        _bin.RecycleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var target = Path.Combine(_root, "bin", Path.GetFileName(call.ArgAt<string>(0)));
                File.Move(call.ArgAt<string>(0), target);

                return Task.FromResult(target);
            });
    }

    [Fact]
    public async Task Recycles_the_file_and_its_lyrics_forgets_it_and_blocklists_the_download()
    {
        await using var context = await ContextAsync();
        var (song, file, candidate) = await SeedAsync(context);
        File.WriteAllText(Path.ChangeExtension(file, ".lrc"), "[00:01.00] la");

        var removal = await Remover(context).RemoveAsync(song.Id, blocklist: true, CancellationToken.None);

        removal.Should().NotBeNull();
        removal!.Path.Should().Be(file);
        removal.RecycledPath.Should().Be(Path.Combine(_root, "bin", "03 - Wonderland.mp3"));
        removal.Blocklisted.Should().BeTrue();
        File.Exists(file).Should().BeFalse();
        File.Exists(Path.ChangeExtension(file, ".lrc")).Should().BeFalse("the lyrics sidecar goes with the file");

        await using var fresh = _database.CreateContext(_time);
        (await fresh.SongFiles.CountAsync()).Should().Be(0);
        (await fresh.Songs.CountAsync()).Should().Be(1, "the song stays, missing, so it is searched for again");

        var keys = await fresh.Blocklist.Select(entry => entry.BlocklistKey).ToListAsync();
        keys.Should().BeEquivalentTo([candidate.BlocklistKey, BlocklistKeys.SoulseekFile(RemotePath, song.Title)]);
        (await fresh.Blocklist.AllAsync(entry => entry.Reason == SongFileRemover.Reason && entry.ExpiresAt == null)).Should().BeTrue();

        var history = await fresh.History.SingleAsync();
        history.EventType.Should().Be(HistoryEventType.Deleted);
        using var data = JsonDocument.Parse(history.Data);
        data.RootElement.GetProperty("path").GetString().Should().Be(file);
        data.RootElement.GetProperty("blocklisted").GetBoolean().Should().BeTrue();

        _plex.Received(1).RequestFolder(song.LibraryId, Path.GetDirectoryName(file)!);
    }

    [Fact]
    public async Task Blocklisting_a_compilation_file_that_passed_on_its_title_alone_counts_a_miss()
    {
        await using var context = await ContextAsync();
        var (song, _, candidate) = await SeedAsync(context, remote: CompilationPath);
        await StoreSnapshotAsync(context, candidate);

        await Remover(context).RemoveAsync(song.Id, blocklist: true, CancellationToken.None);

        await using var fresh = _database.CreateContext(_time);
        var miss = await fresh.Blocklist.SingleAsync(entry => entry.BlocklistKey == BlocklistKeys.CompilationMiss(candidate.BlocklistKey));
        miss.SongId.Should().Be(song.Id);
        miss.Reason.Should().Be(SongFileRemover.Reason);
        miss.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task A_removal_that_counted_its_miss_already_does_not_count_it_twice()
    {
        await using var context = await ContextAsync();
        var (song, _, candidate) = await SeedAsync(context, remote: CompilationPath);
        await StoreSnapshotAsync(context, candidate);
        context.Blocklist.Add(new BlocklistItem
        {
            SongId = song.Id,
            SourceType = SourceTypes.Soulseek,
            BlocklistKey = BlocklistKeys.CompilationMiss(candidate.BlocklistKey),
            Reason = "earlier",
        });
        await context.SaveChangesAsync();

        await Remover(context).RemoveAsync(song.Id, blocklist: true, CancellationToken.None);

        await using var fresh = _database.CreateContext(_time);
        BlocklistKeys.CompilationMisses(await fresh.Blocklist.Select(entry => entry.BlocklistKey).ToListAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Without_blocklist_no_miss_is_counted()
    {
        await using var context = await ContextAsync();
        var (song, _, candidate) = await SeedAsync(context, remote: CompilationPath);
        await StoreSnapshotAsync(context, candidate);

        await Remover(context).RemoveAsync(song.Id, blocklist: false, CancellationToken.None);

        await using var fresh = _database.CreateContext(_time);
        (await fresh.Blocklist.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_file_on_a_path_that_names_the_artist_counts_no_miss()
    {
        await using var context = await ContextAsync();
        var (song, _, candidate) = await SeedAsync(context, remote: @"@@peer1\Music\The Saddle Club\Singles\01 The Saddle Club - Wonderland.mp3");
        await StoreSnapshotAsync(context, candidate);

        await Remover(context).RemoveAsync(song.Id, blocklist: true, CancellationToken.None);

        await using var fresh = _database.CreateContext(_time);
        BlocklistKeys.CompilationMisses(await fresh.Blocklist.Select(entry => entry.BlocklistKey).ToListAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Without_blocklist_the_download_stays_allowed()
    {
        await using var context = await ContextAsync();
        var (song, _, _) = await SeedAsync(context);

        var removal = await Remover(context).RemoveAsync(song.Id, blocklist: false, CancellationToken.None);

        removal!.Blocklisted.Should().BeFalse();
        await using var fresh = _database.CreateContext(_time);
        (await fresh.Blocklist.CountAsync()).Should().Be(0);
        (await fresh.SongFiles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_file_already_gone_from_disk_is_forgotten()
    {
        await using var context = await ContextAsync();
        var (song, file, _) = await SeedAsync(context);
        File.Delete(file);

        var removal = await Remover(context).RemoveAsync(song.Id, blocklist: true, CancellationToken.None);

        removal!.RecycledPath.Should().BeNull();
        await _bin.DidNotReceiveWithAnyArgs().RecycleAsync(default!, default!, default);
        await using var fresh = _database.CreateContext(_time);
        (await fresh.SongFiles.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(SourceTypes.Reference)]
    [InlineData(SourceTypes.Adopted)]
    public async Task A_reference_or_adopted_file_is_never_removed(string sourceType)
    {
        await using var context = await ContextAsync();
        var (song, file, _) = await SeedAsync(context, sourceType);

        var act = () => Remover(context).RemoveAsync(song.Id, blocklist: true, CancellationToken.None);

        await act.Should().ThrowAsync<SongFileIsReferenceException>();
        File.Exists(file).Should().BeTrue();
        await using var fresh = _database.CreateContext(_time);
        (await fresh.SongFiles.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_song_with_a_download_in_flight_is_refused()
    {
        await using var context = await ContextAsync();
        var (song, file, candidate) = await SeedAsync(context);
        context.QueueItems.Add(new QueueItem
        {
            SongId = song.Id,
            CandidateId = candidate.Id,
            SearchRunId = candidate.SearchRunId,
            SourceType = SourceTypes.Soulseek,
            Destination = "wondarr/1",
            State = QueueItemState.Downloading,
            StateChangedAt = _time.GetUtcNow().UtcDateTime,
            LastProgressAt = _time.GetUtcNow().UtcDateTime,
        });
        await context.SaveChangesAsync();

        var act = () => Remover(context).RemoveAsync(song.Id, blocklist: true, CancellationToken.None);

        (await act.Should().ThrowAsync<SongsBusyException>()).Which.BusyIds.Should().Equal(song.Id);
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public async Task A_song_without_a_file_or_an_unknown_song_is_nothing_to_remove()
    {
        await using var context = await ContextAsync();
        var bare = await SongSeed.AddAsync(context, "No file");

        (await Remover(context).RemoveAsync(bare.Id, blocklist: true, CancellationToken.None)).Should().BeNull();
        (await Remover(context).RemoveAsync(987654, blocklist: true, CancellationToken.None)).Should().BeNull();
    }

    public void Dispose()
    {
        _database.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory must never fail a test run.
        }

        GC.SuppressFinalize(this);
    }

    private SongFileRemover Remover(WondarrDbContext context) =>
        new(context, _bin, NullLogger<SongFileRemover>.Instance, _plex);

    /// <summary>A song in the default library, moved under the temp root, with a real file from a Soulseek grab.</summary>
    private async Task<(Song Song, string File, CandidateRecord Candidate)> SeedAsync(
        WondarrDbContext context,
        string sourceType = SourceTypes.Soulseek,
        string? remote = null)
    {
        var remotePath = remote ?? RemotePath;
        var library = await context.Libraries.SingleAsync(row => row.Id == SeedData.DefaultLibraryId);
        library.RootPath = Path.Combine(_root, "music");
        await context.SaveChangesAsync();

        var song = await SongSeed.AddAsync(context, "Wonderland", "The Saddle Club");
        var run = new SearchRun { SongId = song.Id, StartedAt = _time.GetUtcNow().UtcDateTime };
        var candidate = new CandidateRecord
        {
            SearchRun = run,
            SongId = song.Id,
            SourceType = sourceType,
            BlocklistKey = BlocklistKeys.Soulseek("peer1", remotePath),
            DisplayName = "01 Artur - Wonderland.mp3",
            RemotePath = remotePath,
            Provider = "peer1",
        };
        context.Candidates.Add(candidate);
        await context.SaveChangesAsync();

        var path = Path.Combine(_root, "music", "The Saddle Club", "Singles", "03 - Wonderland.mp3");
        File.WriteAllBytes(path, [1, 2, 3]);

        context.SongFiles.Add(new SongFile
        {
            SongId = song.Id,
            Path = path,
            Codec = "mp3",
            Container = "mp3",
            Size = 3,
            QualityId = (await context.Qualities.FirstAsync()).Id,
            SourceType = sourceType,
            SourceRef = JsonSerializer.Serialize(
                new { provider = "peer1", remotePath, candidateId = candidate.Id, queueItemId = 0, searchRunId = run.Id }),
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return (song, path, candidate);
    }

    /// <summary>Gives the stored candidate the snapshot a search writes, as a current database has.</summary>
    private static async Task StoreSnapshotAsync(WondarrDbContext context, CandidateRecord record)
    {
        var stored = await context.Candidates.SingleAsync(row => row.Id == record.Id);
        stored.Normalised = JsonSerializer.Serialize(
            new Candidate
            {
                SourceType = stored.SourceType,
                BlocklistKey = stored.BlocklistKey,
                DisplayName = stored.DisplayName,
                RemotePath = stored.RemotePath,
                Provider = stored.Provider,
                QualityId = stored.QualityId,
                Parsed = SoulseekFilenameParser.Parse(stored.RemotePath).Parsed,
            },
            SongSearchService.Json);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_time);

        return _database.CreateContext(_time);
    }
}
