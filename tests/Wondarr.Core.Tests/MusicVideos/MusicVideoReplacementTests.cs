using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.MusicVideos;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>
/// M-68: a person's pick replaces a video found in the folder (the found file goes to the recycle bin), a failed
/// replacement is stored and shown, the same video may be downloaded again under 720p, and the row's
/// <c>updated_at</c> moves with its status.
/// </summary>
public sealed class MusicVideoReplacementTests : IDisposable
{
    private const string VideoId = "azdwsXLmrHE";

    private readonly MusicVideoHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private string FoundPath => _harness.Temp.Full("videos/Queen/Queen - Radio Ga Ga.mkv");

    /// <summary>Queen's Radio Ga Ga with a 360p found file in the folder, adopted by the song.</summary>
    private async Task<Song> SeedFoundRadioGaGaAsync()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Radio Ga Ga", artist: "Queen");
        _harness.Temp.CreateFile("videos/Queen/Queen - Radio Ga Ga.mkv", "the user's own 360p video");

        await using var context = _harness.NewContext();
        context.MusicVideos.Add(new MusicVideo
        {
            SongId = song.Id,
            Status = MusicVideoStatus.Present,
            YouTubeVideoId = string.Empty,
            FoundBy = MusicVideoFoundBy.Existing,
            Path = FoundPath,
            Width = 640,
            Height = 360,
        });
        context.MusicVideoFiles.Add(new MusicVideoFile
        {
            Path = FoundPath,
            Size = 20,
            ModifiedAt = DateTime.UtcNow,
            State = MusicVideoFileState.Adopted,
            SongId = song.Id,
        });
        await context.SaveChangesAsync();

        return song;
    }

    private List<string> Recycled() =>
        Directory.Exists(_harness.Temp.Full("bin"))
            ? [.. Directory.GetFiles(_harness.Temp.Full("bin"), "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetFileName(path).StartsWith(".wondarr", StringComparison.Ordinal))]
            : [];

    private async Task ImportAsync(long songId, string videoId = VideoId)
    {
        await using var context = _harness.NewContext();
        await _harness.Importer(context).ImportAsync(songId, videoId, MusicVideoFoundBy.Manual, CancellationToken.None);
    }

    [Fact]
    public async Task A_found_file_the_new_video_is_named_like_is_recycled_and_replaced()
    {
        var song = await SeedFoundRadioGaGaAsync();

        await ImportAsync(song.Id);

        File.ReadAllText(FoundPath).Should().Be("matroska bytes", "the downloaded video is at the name now");
        Recycled().Should().Contain(path => path.EndsWith("Queen - Radio Ga Ga.mkv", StringComparison.Ordinal), "the user's file is in the bin");

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Present);
        row.FoundBy.Should().Be(MusicVideoFoundBy.Manual);
        row.YouTubeVideoId.Should().Be(VideoId);
        row.ReplaceFailureReason.Should().BeNull();
        (await fresh.MusicVideoFiles.CountAsync()).Should().Be(0, "the found file's row is gone with the name taken by the download");

        var history = await fresh.History.SingleAsync();
        history.EventType.Should().Be(HistoryEventType.MusicVideoImported);
        history.Data.Should().Contain("Queen - Radio Ga Ga.mkv");
    }

    [Fact]
    public async Task A_found_file_the_new_video_is_not_named_like_is_recycled_once_the_new_one_is_in_place()
    {
        var song = await SeedFoundRadioGaGaAsync();
        _harness.Options.NamingTemplate = "{Artist} - {Title} (video)";

        await ImportAsync(song.Id);

        File.Exists(FoundPath).Should().BeFalse();
        File.Exists(_harness.Temp.Full("videos/Queen - Radio Ga Ga (video).mkv")).Should().BeTrue();
        Recycled().Should().ContainSingle(path => path.EndsWith("Queen - Radio Ga Ga.mkv", StringComparison.Ordinal));

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideoFiles.SingleAsync()).State.Should().Be(MusicVideoFileState.Ignored, "the scan must not adopt a recycled file again");
    }

    [Fact]
    public async Task A_found_file_is_put_back_when_filing_the_new_video_fails_and_the_failure_is_stored_and_in_the_history()
    {
        var song = await SeedFoundRadioGaGaAsync();
        _harness.Disk = new MusicVideoImporterTests.FailingPlaceDisk();

        await using (var context = _harness.NewContext())
        {
            var act = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.Manual, CancellationToken.None);

            (await act.Should().ThrowAsync<MusicVideoImportException>()).Which.Message.Should().Contain("could not be filed");
        }

        File.ReadAllText(FoundPath).Should().Be("the user's own 360p video", "the file is back where it was");
        Recycled().Should().BeEmpty();

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Present);
        row.FoundBy.Should().Be(MusicVideoFoundBy.Existing, "what is present is unchanged");
        row.ReplaceFailureReason.Should().Contain("could not be filed");
        row.ReplaceFailedAt.Should().NotBeNull();

        var history = await fresh.History.SingleAsync();
        history.EventType.Should().Be(HistoryEventType.MusicVideoFailed);
        history.Data.Should().Contain("could not be filed");
        history.SongId.Should().Be(song.Id);
    }

    [Fact]
    public async Task Without_a_usable_recycle_bin_a_found_file_is_not_replaced_and_nothing_is_downloaded()
    {
        var song = await SeedFoundRadioGaGaAsync();

        // A bin inside the video folder is no bin: what went in would be cleaned out with the folder.
        _harness.Import.RecycleBinPath = _harness.Temp.Full("videos/bin");

        await using (var context = _harness.NewContext())
        {
            var act = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.Manual, CancellationToken.None);

            (await act.Should().ThrowAsync<MusicVideoImportException>())
                .Which.Message.Should().Be("Set up a recycle bin to replace a video that was in your folder");
        }

        _harness.Downloader.Calls.Should().BeEmpty();
        File.ReadAllText(FoundPath).Should().Be("the user's own 360p video");

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.SingleAsync()).ReplaceFailureReason.Should().Be("Set up a recycle bin to replace a video that was in your folder");
        (await fresh.History.SingleAsync()).EventType.Should().Be(HistoryEventType.MusicVideoFailed);

        var refusal = await _harness.Videos(fresh).ReplaceRefusalAsync(await fresh.MusicVideos.SingleAsync(), CancellationToken.None);
        refusal.Should().Be("Set up a recycle bin to replace a video that was in your folder");
    }

    [Fact]
    public async Task A_video_that_was_downloaded_needs_no_recycle_bin_check_and_a_usable_bin_is_not_refused()
    {
        var song = await SeedFoundRadioGaGaAsync();

        await using var context = _harness.NewContext();
        var row = await context.MusicVideos.SingleAsync();

        (await _harness.Videos(context).ReplaceRefusalAsync(row, CancellationToken.None)).Should().BeNull();

        row.FoundBy = MusicVideoFoundBy.Manual;
        _harness.Import.RecycleBinPath = _harness.Temp.Full("videos/bin");
        (await _harness.Videos(context).ReplaceRefusalAsync(row, CancellationToken.None)).Should().BeNull("only a found file is refused");
        song.Id.Should().BePositive();
    }

    [Fact]
    public async Task A_successful_replacement_clears_the_stored_failure()
    {
        var song = await SeedFoundRadioGaGaAsync();

        await using (var context = _harness.NewContext())
        {
            var row = await context.MusicVideos.SingleAsync();
            row.ReplaceFailureReason = "The video could not be filed: disk full";
            row.ReplaceFailedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        await ImportAsync(song.Id);

        await using var fresh = _harness.NewContext();
        var after = await fresh.MusicVideos.SingleAsync();
        after.ReplaceFailureReason.Should().BeNull();
        after.ReplaceFailedAt.Should().BeNull();
    }

    [Fact]
    public async Task Dismissing_the_failure_clears_it_and_leaves_the_video()
    {
        var song = await SeedFoundRadioGaGaAsync();

        await using (var context = _harness.NewContext())
        {
            var row = await context.MusicVideos.SingleAsync();
            row.ReplaceFailureReason = "The video could not be filed: disk full";
            row.ReplaceFailedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        await using var fresh = _harness.NewContext();
        (await _harness.Videos(fresh).DismissReplaceFailureAsync(song.Id, CancellationToken.None)).Should().BeTrue();
        (await _harness.Videos(fresh).DismissReplaceFailureAsync(song.Id, CancellationToken.None)).Should().BeFalse("nothing is left to dismiss");

        await using var after = _harness.NewContext();
        var video = await after.MusicVideos.SingleAsync();
        video.ReplaceFailureReason.Should().BeNull();
        video.Status.Should().Be(MusicVideoStatus.Present);
        video.Path.Should().Be(FoundPath);
    }

    [Fact]
    public async Task An_automatic_run_never_replaces_a_found_file()
    {
        var song = await SeedFoundRadioGaGaAsync();

        await using var context = _harness.NewContext();
        var queued = await _harness.Search(context)
            .QueueDownloadAsync(song.Id, VideoId, MusicVideoFoundBy.YouTubeSearch, CommandTrigger.Scheduled, CancellationToken.None);

        queued.Outcome.Should().Be(MusicVideoQueueOutcome.AlreadyFiled);
        _harness.CommandCount.Should().Be(0);
        File.ReadAllText(FoundPath).Should().Be("the user's own 360p video");
        (await _harness.Search(context).NextMissingAsync(10, CancellationToken.None)).Should().NotContain(song.Id, "the scheduled task skips a song that has a video");
    }

    [Fact]
    public async Task The_row_moves_updated_at_when_it_becomes_downloading()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);
        _harness.Time.Advance(TimeSpan.FromHours(2));
        var now = _harness.Time.GetUtcNow().UtcDateTime;

        MusicVideo? during = null;
        _harness.Downloader.DuringDownload = async () =>
        {
            await using var reader = _harness.NewContext();
            during = await reader.MusicVideos.AsNoTracking().SingleAsync();
        };

        await ImportAsync(song.Id, "u7K72X4eo_s");

        during.Should().NotBeNull();
        during!.Status.Should().Be(MusicVideoStatus.Downloading);
        during.UpdatedAt.Should().Be(now);
    }

    [Fact]
    public async Task A_failed_download_moves_updated_at_too()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);
        _harness.Time.Advance(TimeSpan.FromHours(3));
        _harness.Downloader.Failure = new InvalidOperationException("no network");

        var act = () => ImportAsync(song.Id, "u7K72X4eo_s");
        await act.Should().ThrowAsync<MusicVideoImportException>();

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Failed);
        row.UpdatedAt.Should().Be(_harness.Time.GetUtcNow().UtcDateTime);
    }

    [Theory]
    [InlineData(360, true)]
    [InlineData(719, true)]
    [InlineData(720, false)]
    [InlineData(1080, false)]
    [InlineData(null, false)]
    public async Task The_video_the_song_already_has_is_grabbed_again_only_under_720p(int? height, bool queued)
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Hello World", artist: "The Saddle Club");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, "qR9u10s59BE", _harness.Temp.Full("videos/x.mkv"));

        await using (var context = _harness.NewContext())
        {
            await context.MusicVideos.ExecuteUpdateAsync(set => set.SetProperty(video => video.Height, height));
        }

        await using var fresh = _harness.NewContext();
        var result = await _harness.Search(fresh)
            .QueueDownloadAsync(song.Id, "qR9u10s59BE", MusicVideoFoundBy.Manual, CommandTrigger.Manual, CancellationToken.None, replace: true);

        result.Outcome.Should().Be(queued ? MusicVideoQueueOutcome.Queued : MusicVideoQueueOutcome.AlreadyFiled);
    }

    [Fact]
    public async Task A_search_never_downloads_the_same_video_again_even_under_720p()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Hello World", artist: "The Saddle Club");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, "qR9u10s59BE", _harness.Temp.Full("videos/x.mkv"));

        await using var context = _harness.NewContext();
        await context.MusicVideos.ExecuteUpdateAsync(set => set.SetProperty(video => video.Height, 360));

        var result = await _harness.Search(context)
            .QueueDownloadAsync(song.Id, "qR9u10s59BE", MusicVideoFoundBy.YouTubeSearch, CommandTrigger.Manual, CancellationToken.None);

        result.Outcome.Should().Be(MusicVideoQueueOutcome.AlreadyFiled);
    }

    [Fact]
    public async Task Downloading_the_same_video_again_recycles_the_old_file_and_replaces_it()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        var path = _harness.Temp.CreateFile("videos/Massive Attack/Massive Attack - Teardrop.mkv", "360p copy");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, "u7K72X4eo_s", path, MusicVideoFoundBy.Manual);

        await ImportAsync(song.Id, "u7K72X4eo_s");

        File.ReadAllText(path).Should().Be("matroska bytes");
        Recycled().Should().ContainSingle(file => file.EndsWith("Massive Attack - Teardrop.mkv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Downloading_the_same_video_again_puts_the_old_file_back_when_filing_fails()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        var path = _harness.Temp.CreateFile("videos/Massive Attack/Massive Attack - Teardrop.mkv", "360p copy");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, "u7K72X4eo_s", path, MusicVideoFoundBy.Manual);
        _harness.Disk = new MusicVideoImporterTests.FailingPlaceDisk();

        var act = () => ImportAsync(song.Id, "u7K72X4eo_s");
        await act.Should().ThrowAsync<MusicVideoImportException>();

        File.ReadAllText(path).Should().Be("360p copy");
        Recycled().Should().BeEmpty();
        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.SingleAsync()).ReplaceFailureReason.Should().NotBeNull();
    }
}
