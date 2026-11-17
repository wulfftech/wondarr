using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Organizer;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Core.Tests.Songs;
using Xunit;
using HealthReport = Wondarr.Core.HealthCheck.HealthCheck;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>
/// The orphan check: a warning, never an error, for videos whose file is not where the row says; named at most 20
/// times, counted in full, and quiet while the feature is off.
/// </summary>
public sealed class MusicVideoOrphanHealthCheckTests : IDisposable
{
    private readonly TempRoot _temp = new();
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();
    private WondarrDbContext? _context;

    public void Dispose()
    {
        _context?.Dispose();
        _database.Dispose();
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task While_the_feature_is_off_nothing_is_reported_even_for_a_missing_file()
    {
        await SeedAsync(missing: 1, present: 0);

        var result = await CheckAsync(enabled: false);

        result.Type.Should().Be(HealthCheckResult.Ok);
    }

    [Fact]
    public async Task Videos_that_are_all_there_are_fine()
    {
        await SeedAsync(missing: 0, present: 2);

        var result = await CheckAsync(enabled: true);

        result.Type.Should().Be(HealthCheckResult.Ok);
    }

    [Fact]
    public async Task A_missing_file_is_a_warning_that_names_it()
    {
        var paths = await SeedAsync(missing: 1, present: 1);

        var result = await CheckAsync(enabled: true);

        result.Type.Should().Be(HealthCheckResult.Warning);
        result.Message.Should().Contain("1 music video file is missing").And.Contain(paths.Missing[0]);
        result.Message.Should().NotContain(paths.Present[0]);
    }

    [Fact]
    public async Task More_than_twenty_missing_files_are_counted_in_full_and_twenty_are_named()
    {
        var paths = await SeedAsync(missing: 23, present: 0);

        var result = await CheckAsync(enabled: true);

        result.Type.Should().Be(HealthCheckResult.Warning);
        result.Message.Should().Contain("23 music video files are missing").And.Contain("and 3 more");
        paths.Missing.Count(path => result.Message.Contains(path, StringComparison.Ordinal)).Should().Be(20);
    }

    [Fact]
    public async Task A_row_with_no_file_yet_is_not_an_orphan()
    {
        await SeedAsync(missing: 0, present: 0);
        var song = await SongSeed.AddAsync(_context!, "Queued", "Artist");
        _context!.MusicVideos.Add(new MusicVideo
        {
            SongId = song.Id,
            Status = MusicVideoStatus.Queued,
            YouTubeVideoId = "queued",
            FoundBy = MusicVideoFoundBy.Manual,
        });
        await _context.SaveChangesAsync();

        var result = await CheckAsync(enabled: true);

        result.Type.Should().Be(HealthCheckResult.Ok);
    }

    private async Task<(List<string> Missing, List<string> Present)> SeedAsync(int missing, int present)
    {
        await _database.MigrateAsync(_time);
        _context = _database.CreateContext(_time);

        var missingPaths = new List<string>();
        var presentPaths = new List<string>();

        for (var index = 0; index < missing + present; index++)
        {
            var song = await SongSeed.AddAsync(_context, "Song " + index, "Artist " + index);
            var isMissing = index < missing;
            var path = _temp.Full($"music/Artist {index}/Song {index}-video.mkv");

            if (!isMissing)
            {
                _temp.CreateFile($"music/Artist {index}/Song {index}-video.mkv");
            }

            (isMissing ? missingPaths : presentPaths).Add(path);

            _context.MusicVideos.Add(new MusicVideo
            {
                SongId = song.Id,
                Status = MusicVideoStatus.Present,
                YouTubeVideoId = "yt" + index,
                FoundBy = MusicVideoFoundBy.Manual,
                Path = path,
            });
        }

        await _context.SaveChangesAsync();

        return (missingPaths, presentPaths);
    }

    private Task<HealthReport> CheckAsync(bool enabled)
    {
        _context ??= _database.CreateContext(_time);

        var check = new MusicVideoOrphanHealthCheck(
            new TestOptionsMonitor<MusicVideoOptions>(new MusicVideoOptions { Enabled = enabled }),
            _context,
            new DiskOperations());

        return check.CheckAsync(CancellationToken.None);
    }
}
