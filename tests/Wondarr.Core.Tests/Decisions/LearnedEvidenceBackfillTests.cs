using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata.AcoustId;
using Wondarr.Core.Persistence;
using Wondarr.Core.Searching;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Core.Tests.Songs;
using Xunit;

namespace Wondarr.Core.Tests.Decisions;

/// <summary>
/// What the one-time pass teaches a song from the refusals recorded before 0.1.18: Phil's "Please Come Tonight"
/// had 80 refusals naming "Please Come Out Tonight" and none of them was a learned row.
/// </summary>
public sealed class LearnedEvidenceBackfillTests : IDisposable
{
    private const string OtherId = "c80ad2a5-1111-2222-3333-444455556666";
    private const string ArtistReason = "Not in AcoustID, and the artist tag names another artist (Middle M)";
    private const string CompilationPath = @"@@peer\Various Artists\Nuggets\Disc 1\1-18 Out on a Limb.flac";

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();
    private readonly List<long> _failSongIds = [];

    [Fact]
    public async Task Learns_a_title_named_by_two_refusals()
    {
        var songId = await SeedSongAsync("Please Come Tonight", "Phil");
        await RefuseAsync(songId, "peer1", @"Music\Phil Collins\11 - Please Come Out Tonight.flac", Fingerprint("Please Come Out Tonight"));
        await RefuseAsync(songId, "peer2", @"Music\Phil Collins\09. Please Come out Tonight.flac", Fingerprint("Please Come out Tonight"));

        await RunAsync();

        var learned = await LearnedRowsAsync();
        var row = learned.Should().ContainSingle(entry => entry.BlocklistKey.Contains("other-title")).Subject;
        row.BlocklistKey.Should().Be(BlocklistKeys.OtherTitle("Please Come out Tonight"), "the newest refusal's spelling");
        row.SongId.Should().Be(songId);
        row.SourceType.Should().Be(SourceTypes.Soulseek);
        row.Reason.Should().Be(Fingerprint("Please Come out Tonight"));
        row.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task One_refusal_is_not_enough_however_many_rows_it_wrote()
    {
        var songId = await SeedSongAsync("Please Come Tonight", "Phil");

        // A refusal writes the file's own key and its folder-and-file-name key in one save.
        await RefuseAsync(songId, "peer1", @"Music\Phil Collins\Both Sides\11 - Please Come Out Tonight.flac", Fingerprint("Please Come Out Tonight"), withFileKey: true);

        await RunAsync();

        (await LearnedRowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Two_refusals_in_one_save_count_as_one()
    {
        var songId = await SeedSongAsync("Please Come Tonight", "Phil");
        await using (var context = _database.CreateContext(_time))
        {
            context.Blocklist.Add(Row(songId, BlocklistKeys.Soulseek("a", @"Music\X\Please Come Out Tonight.flac"), Fingerprint("Please Come Out Tonight")));
            context.Blocklist.Add(Row(songId, BlocklistKeys.Soulseek("b", @"Music\Y\Please Come Out Tonight.flac"), Fingerprint("Please Come Out Tonight")));
            await context.SaveChangesAsync();
        }

        await RunAsync();

        (await LearnedRowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Refusals_below_the_accept_score_teach_nothing()
    {
        var songId = await SeedSongAsync("Please Come Tonight", "Phil");
        await RefuseAsync(songId, "peer1", @"Music\Phil Collins\11 - Please Come Out Tonight.flac", Fingerprint("Please Come Out Tonight", "0.55"));
        await RefuseAsync(songId, "peer2", @"Music\Phil Collins\09. Please Come Out Tonight.flac", Fingerprint("Please Come Out Tonight", "0.69"));

        await RunAsync();

        (await LearnedRowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_songs_own_title_in_another_form_is_not_learned()
    {
        var songId = await SeedSongAsync("Out on a Limb", "The Saddle Club");
        await RefuseAsync(songId, "peer1", @"Music\X\Out on a Limb.flac", Fingerprint("OUT ON A LIMB"));
        await RefuseAsync(songId, "peer2", @"Music\Y\Out on a Limb.mp3", Fingerprint("Out on a Limb (Radio Edit)"));

        await RunAsync();

        BlocklistKeys.OtherTitles((await LearnedRowsAsync()).Select(row => row.BlocklistKey)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_learned_row_is_not_a_refusal_and_a_known_title_is_not_added_again()
    {
        var songId = await SeedSongAsync("Please Come Tonight", "Phil");
        await RefuseAsync(songId, "peer1", @"Music\Phil Collins\11 - Please Come Out Tonight.flac", Fingerprint("Please Come Out Tonight"));

        // A row of 0.1.18's own learning carries the refusal's reason; it is not a second refusal.
        await AddRowsAsync(Row(songId, BlocklistKeys.CompilationMiss(BlocklistKeys.Soulseek("z", @"VA\x.flac")), Fingerprint("Please Come Out Tonight")));

        await RunAsync();

        BlocklistKeys.OtherTitles((await LearnedRowsAsync()).Select(row => row.BlocklistKey)).Should().BeEmpty();

        await RefuseAsync(songId, "peer2", @"Music\Phil Collins\09. Please Come Out Tonight.flac", Fingerprint("Please Come Out Tonight"));
        await AddRowsAsync(Row(songId, BlocklistKeys.OtherTitle("PLEASE COME OUT TONIGHT"), Fingerprint("Please Come Out Tonight")));
        await ResetAsync();

        await RunAsync();

        BlocklistKeys.OtherTitles((await LearnedRowsAsync()).Select(row => row.BlocklistKey)).Should().ContainSingle();
    }

    [Fact]
    public async Task Counts_a_miss_for_each_kind_of_refusal_that_proved_a_compilation_file_was_not_the_song()
    {
        var songId = await SeedSongAsync("Out on a Limb", "The Saddle Club");
        var fingerprint = await RefuseWithCandidateAsync(songId, "a", @"@@a\Various Artists\Nuggets\Disc 1\1-18 Out on a Limb.flac", Fingerprint("Believe Her"));
        var artist = await RefuseWithCandidateAsync(songId, "b", @"@@b\Compilations\Sixties Gold\07 Out on a Limb.mp3", ArtistReason);
        var removed = await RefuseWithCandidateAsync(songId, "c", @"@@c\VA - Ambient Hits\03 - Out on a Limb.flac", SongFileRemover.Reason);
        _ = await RefuseWithCandidateAsync(songId, "d", @"@@d\Various Artists\Kids\05 The Saddle Club - Out on a Limb.mp3", ArtistReason);
        _ = await RefuseWithCandidateAsync(songId, "e", @"@@e\Music\Jazz\Lennie Tristano\01 Out on a Limb.mp3", ArtistReason);
        _ = await RefuseWithCandidateAsync(songId, "f", @"@@f\Various Artists\Hits\02 Out on a Limb.flac", Fingerprint("Believe Her", "0.4"));
        _ = await RefuseWithCandidateAsync(songId, "g", @"@@g\Various Artists\Hits\04 Out on a Limb.flac", ArtistReason, snapshot: false);

        await RunAsync();

        var misses = (await LearnedRowsAsync()).Where(row => row.BlocklistKey.Contains("compilation-miss")).ToList();
        misses.Select(row => row.BlocklistKey).Should().BeEquivalentTo(
            [BlocklistKeys.CompilationMiss(fingerprint), BlocklistKeys.CompilationMiss(artist), BlocklistKeys.CompilationMiss(removed)]);
        misses.Single(row => row.BlocklistKey == BlocklistKeys.CompilationMiss(artist)).Reason.Should().Be(ArtistReason);
        misses.Should().OnlyContain(row => row.ExpiresAt == null && row.SongId == songId && row.SourceType == SourceTypes.Soulseek);
    }

    [Fact]
    public async Task Writes_nothing_twice_and_runs_once()
    {
        var songId = await SeedSongAsync("Out on a Limb", "The Saddle Club");
        await RefuseWithCandidateAsync(songId, "a", CompilationPath, Fingerprint("Believe Her"));
        await RefuseWithCandidateAsync(songId, "b", @"@@b\Various Artists\Nuggets\Disc 1\1-19 Out on a Limb.flac", Fingerprint("Believe Her"));

        await RunAsync();

        var first = await LearnedRowsAsync();
        first.Should().HaveCount(3, "the title and two misses");
        (await SettingAsync()).Should().Be(LearnedEvidenceBackfill.CurrentVersion);

        // The setting stops a second run, even with a refusal it has not seen.
        await RefuseWithCandidateAsync(songId, "c", @"@@c\VA - Hits\03 - Out on a Limb.flac", Fingerprint("Believe Her"));
        await RunAsync();
        (await LearnedRowsAsync()).Should().HaveCount(3);

        // And with the setting gone the pass finds everything written already, and adds only the new miss.
        await ResetAsync();
        await RunAsync();
        (await LearnedRowsAsync()).Should().HaveCount(4);
        await ResetAsync();
        await RunAsync();
        (await LearnedRowsAsync()).Should().HaveCount(4);
    }

    [Fact]
    public async Task A_song_that_fails_does_not_stop_the_others()
    {
        var failing = await SeedSongAsync("Please Come Tonight", "Phil");
        var fine = await SeedSongAsync("Believe", "Cher");
        foreach (var id in new[] { failing, fine })
        {
            await RefuseAsync(id, "p1", @"Music\X\Title One.flac", Fingerprint("Another Name"));
            await RefuseAsync(id, "p2", @"Music\Y\Title Two.flac", Fingerprint("Another Name"));
        }

        _failSongIds.Add(failing);

        await RunAsync();

        var learned = await LearnedRowsAsync();
        learned.Should().ContainSingle().Which.SongId.Should().Be(fine);
        (await SettingAsync()).Should().Be(0, "a failed song is retried at the next start");

        _failSongIds.Clear();
        await RunAsync();

        (await LearnedRowsAsync()).Select(row => row.SongId).Should().BeEquivalentTo([fine, failing]);
        (await SettingAsync()).Should().Be(LearnedEvidenceBackfill.CurrentVersion);
    }

    [Fact]
    public async Task Never_learns_unknown_title()
    {
        var songId = await SeedSongAsync("Please Come Tonight", "Phil");
        await RefuseAsync(songId, "p1", @"Music\X\Title One.flac", Fingerprint("unknown title"));
        await RefuseAsync(songId, "p2", @"Music\Y\Title Two.flac", Fingerprint("Unknown Title"));

        await RunAsync();

        (await LearnedRowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_refusal_naming_the_songs_own_recording_teaches_nothing()
    {
        var songId = await SeedSongAsync("Out on a Limb", "The Saddle Club");
        await using (var context = _database.CreateContext(_time))
        {
            (await context.Songs.SingleAsync(row => row.Id == songId)).MbRecordingId = OtherId.ToUpperInvariant();
            await context.SaveChangesAsync();
        }

        await RefuseWithCandidateAsync(songId, "a", CompilationPath, Fingerprint("Believe Her"));
        await RefuseWithCandidateAsync(songId, "b", @"@@b\Various Artists\Nuggets\Disc 1\1-19 Out on a Limb.flac", Fingerprint("Believe Her"));

        await RunAsync();

        (await LearnedRowsAsync()).Should().BeEmpty();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string Fingerprint(string title, string score = "0.97") =>
        $"Fingerprint belongs to a different recording: {title} ({OtherId}), score {score}";

    private static BlocklistItem Row(long songId, string key, string reason) => new()
    {
        SongId = songId,
        SourceType = SourceTypes.Soulseek,
        BlocklistKey = key,
        Reason = reason,
    };

    private async Task<long> SeedSongAsync(string title, string artist)
    {
        if (!File.Exists(_database.FilePath))
        {
            await _database.MigrateAsync(_time);
        }

        await using var context = _database.CreateContext(_time);

        return (await SongSeed.AddAsync(context, title, artist)).Id;
    }

    private async Task AddRowsAsync(params BlocklistItem[] rows)
    {
        await using var context = _database.CreateContext(_time);
        context.Blocklist.AddRange(rows);
        await context.SaveChangesAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
    }

    /// <summary>One refusal: the candidate's key and its folder-and-file-name key, in one save.</summary>
    private async Task RefuseAsync(long songId, string user, string path, string reason, bool withFileKey = true)
    {
        var rows = new List<BlocklistItem> { Row(songId, BlocklistKeys.Soulseek(user, path), reason) };

        if (withFileKey && BlocklistKeys.SoulseekFile(path) is { } fileKey)
        {
            rows.Add(Row(songId, fileKey, reason));
        }

        await AddRowsAsync([.. rows]);
    }

    /// <summary>A refusal with the stored candidate the search judged; returns the candidate's key.</summary>
    private async Task<string> RefuseWithCandidateAsync(long songId, string user, string path, string reason, bool snapshot = true)
    {
        var key = BlocklistKeys.Soulseek(user, path);
        var parsed = SoulseekFilenameParser.Parse(path).Parsed;

        await using (var context = _database.CreateContext(_time))
        {
            var run = new SearchRun { SongId = songId, StartedAt = _time.GetUtcNow().UtcDateTime };
            context.Candidates.Add(new CandidateRecord
            {
                SearchRun = run,
                SongId = songId,
                SourceType = SourceTypes.Soulseek,
                BlocklistKey = key,
                DisplayName = path[(path.LastIndexOf('\\') + 1)..],
                RemotePath = path,
                Provider = user,
                Normalised = snapshot
                    ? JsonSerializer.Serialize(
                        new Candidate
                        {
                            SourceType = SourceTypes.Soulseek,
                            BlocklistKey = key,
                            DisplayName = path[(path.LastIndexOf('\\') + 1)..],
                            RemotePath = path,
                            Provider = user,
                            QualityId = 36,
                            Parsed = parsed,
                        },
                        SongSearchService.Json)
                    : "{}",
            });
            await context.SaveChangesAsync();
        }

        await RefuseAsync(songId, user, path, reason, withFileKey: false);

        return key;
    }

    private async Task RunAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<AcoustIdOptions>();
        services.AddScoped(_ => _database.CreateContext(_time, new FailingSongSaves(_failSongIds)));
        services.AddScoped<ISettingsRepository, SettingsRepository>();
        await using var provider = services.BuildServiceProvider();

        var backfill = new LearnedEvidenceBackfill(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<LearnedEvidenceBackfill>.Instance);

        await backfill.RunOnceAsync(CancellationToken.None);
    }

    private async Task ResetAsync()
    {
        await using var context = _database.CreateContext(_time);
        await new SettingsRepository(context).DeleteAsync(LearnedEvidenceBackfill.SettingKey, CancellationToken.None);
    }

    private async Task<int> SettingAsync()
    {
        await using var context = _database.CreateContext(_time);

        return await new SettingsRepository(context).GetAsync<int>(LearnedEvidenceBackfill.SettingKey, CancellationToken.None);
    }

    private async Task<List<BlocklistItem>> LearnedRowsAsync()
    {
        await using var context = _database.CreateContext(_time);

        return await context.Blocklist
            .AsNoTracking()
            .Where(row => row.BlocklistKey.StartsWith(BlocklistKeys.LearnedOpening))
            .ToListAsync();
    }

    /// <summary>Fails a save that adds blocklist rows for one of the songs, as a database error would.</summary>
    private sealed class FailingSongSaves(List<long> songIds) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<BlocklistItem>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.SongId is { } id && songIds.Contains(id)))
            {
                throw new InvalidOperationException("The database is locked.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}

