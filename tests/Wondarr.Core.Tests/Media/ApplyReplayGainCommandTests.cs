using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Jobs;
using Wondarr.Core.Media;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using Wondarr.Core.Tagging;
using Wondarr.Core.Tests.Importing;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.Media;

/// <summary>
/// The <c>ApplyReplayGain</c> command: files without values are measured and re-tagged in place;
/// reference files and files that already have values are skipped; one failure does not stop the rest.
/// </summary>
public sealed class ApplyReplayGainCommandTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeReplayGainAnalyzer _analyzer = new();
    private readonly FakeTagWriter _tagWriter = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wondarr-rg-tests", Guid.NewGuid().ToString("N"));
    private ServiceProvider? _services;

    [Fact]
    public async Task Files_without_values_are_measured_stored_and_retagged_in_place()
    {
        await using var host = await StartAsync(replayGain: true);
        var first = await SeedAsync();
        var second = await SeedAsync();

        var message = await RunAsync();

        message.Should().Be("ReplayGain for Music: 2 done, 0 failed, 0 skipped");
        _tagWriter.ReplayGainWrites.Select(write => write.Path).Should().BeEquivalentTo([first.Path, second.Path]);
        _tagWriter.ReplayGainWrites.Should().OnlyContain(write => write.GainDb == -8.52 && write.Peak == 1.047129);
        _tagWriter.Writes.Should().BeEmpty("the full tag writer, which replaces every tag, is not used");

        await using var context = _database.CreateContext(_time);
        var files = await context.SongFiles.AsNoTracking().ToListAsync();
        files.Should().OnlyContain(file => file.ReplayGainDb == -8.52 && file.ReplayGainPeak == 1.047129);
        files.Should().OnlyContain(file => file.TagsWritten != null && file.TagsWritten.Contains("ReplayGainTrackGain"));
    }

    [Fact]
    public async Task Reference_files_and_files_with_values_are_skipped()
    {
        await using var host = await StartAsync(replayGain: true);
        var reference = await SeedAsync(sourceType: SourceTypes.Reference);
        var measured = await SeedAsync(gain: -2.0, peak: 0.9);
        var open = await SeedAsync();

        var message = await RunAsync();

        message.Should().Be("ReplayGain for Music: 1 done, 0 failed, 2 skipped");
        _analyzer.Measured.Should().Equal(open.Path);
        _tagWriter.ReplayGainWrites.Should().ContainSingle().Which.Path.Should().Be(open.Path);

        await using var context = _database.CreateContext(_time);
        (await context.SongFiles.AsNoTracking().SingleAsync(file => file.Id == reference.FileId)).ReplayGainDb.Should().BeNull();
        (await context.SongFiles.AsNoTracking().SingleAsync(file => file.Id == measured.FileId)).ReplayGainDb.Should().Be(-2.0);
    }

    [Fact]
    public async Task One_failure_does_not_stop_the_rest()
    {
        await using var host = await StartAsync(replayGain: true);
        var bad = await SeedAsync();
        var good = await SeedAsync();
        _analyzer.Answer = path => path == bad.Path ? null : new ReplayGainValues(-1.5, 0.8);

        var message = await RunAsync();

        message.Should().StartWith("ReplayGain for Music: 1 done, 1 failed, 0 skipped");
        _tagWriter.ReplayGainWrites.Should().ContainSingle().Which.Path.Should().Be(good.Path);

        await using var context = _database.CreateContext(_time);
        (await context.SongFiles.AsNoTracking().SingleAsync(file => file.Id == bad.FileId)).ReplayGainDb.Should().BeNull();
        (await context.SongFiles.AsNoTracking().SingleAsync(file => file.Id == good.FileId)).ReplayGainDb.Should().Be(-1.5);
    }

    [Fact]
    public async Task A_tag_write_failure_leaves_the_row_without_values_and_the_batch_going()
    {
        await using var host = await StartAsync(replayGain: true);
        await SeedAsync();
        await SeedAsync();
        _tagWriter.Success = false;

        var message = await RunAsync();

        message.Should().StartWith("ReplayGain for Music: 0 done, 2 failed, 0 skipped");

        await using var context = _database.CreateContext(_time);
        (await context.SongFiles.AsNoTracking().ToListAsync()).Should().OnlyContain(file => file.ReplayGainDb == null);
    }

    [Fact]
    public async Task A_file_missing_from_disk_fails_without_being_measured()
    {
        await using var host = await StartAsync(replayGain: true);
        var gone = await SeedAsync();
        File.Delete(gone.Path);

        var message = await RunAsync();

        message.Should().StartWith("ReplayGain for Music: 0 done, 1 failed, 0 skipped");
        _analyzer.Measured.Should().BeEmpty();
    }

    [Fact]
    public async Task A_library_with_replaygain_off_refuses_the_command()
    {
        await using var host = await StartAsync(replayGain: false);
        await SeedAsync();

        var act = () => RunAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*turn it on*");
        _analyzer.Measured.Should().BeEmpty();
    }

    [Fact]
    public async Task A_body_without_a_library_is_refused()
    {
        await using var host = await StartAsync(replayGain: true);
        var handler = _services!.GetRequiredService<ApplyReplayGainCommandHandler>();

        var act = () => handler.ExecuteAsync(
            new CommandContext(1, "{}", CommandTrigger.Manual, _ => Task.CompletedTask),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    public void Dispose()
    {
        _services?.Dispose();
        _database.Dispose();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<string?> RunAsync()
    {
        var handler = _services!.GetRequiredService<ApplyReplayGainCommandHandler>();

        return await handler.ExecuteAsync(
            new CommandContext(1, $$"""{"name":"ApplyReplayGain","libraryId":{{SeedData.DefaultLibraryId}}}""", CommandTrigger.Manual, _ => Task.CompletedTask),
            CancellationToken.None);
    }

    private async Task<IAsyncDisposable> StartAsync(bool replayGain)
    {
        await _database.MigrateAsync(_time);
        Directory.CreateDirectory(_root);

        await using (var context = _database.CreateContext(_time))
        {
            var library = await context.Libraries.SingleAsync(candidate => candidate.Id == SeedData.DefaultLibraryId);
            library.Name = "Music";
            library.RootPath = _root;
            library.ReplayGain = replayGain;
            await context.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<IReplayGainAnalyzer>(_analyzer);
        services.AddSingleton<ITagWriter>(_tagWriter);
        services.AddSingleton<ISongFileLock, SongFileLock>();
        services.AddDbContext<WondarrDbContext>(builder => builder
            .UseSqlite($"Data Source={_database.FilePath}")
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IReplayGainApplier, ReplayGainApplier>();
        services.AddSingleton<ApplyReplayGainCommandHandler>();
        _services = services.BuildServiceProvider();

        return new NoopDisposable();
    }

    private async Task<(long FileId, string Path)> SeedAsync(
        string sourceType = SourceTypes.Soulseek,
        double? gain = null,
        double? peak = null)
    {
        await using var context = _database.CreateContext(_time);
        var index = await context.Songs.CountAsync() + 1;

        var artist = new Artist { Name = "Artist " + index, SortName = "Artist " + index };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var song = new Song
        {
            Title = "Song " + index,
            ArtistCredit = artist.Name,
            PrimaryArtistId = artist.Id,
            MbRecordingId = "m-" + index,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };
        context.Songs.Add(song);
        await context.SaveChangesAsync();

        var path = Path.Combine(_root, $"song-{index}.flac");
        await File.WriteAllTextAsync(path, "the audio bytes");

        var file = new SongFile
        {
            SongId = song.Id,
            Path = path,
            Size = 15,
            Codec = "flac",
            Container = "flac",
            DurationMs = 200_000,
            QualityId = 36,
            SourceType = sourceType,
            ImportedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            ReplayGainDb = gain,
            ReplayGainPeak = peak,
        };
        context.SongFiles.Add(file);
        await context.SaveChangesAsync();

        return (file.Id, path);
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
