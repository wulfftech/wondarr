using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>
/// M-70 item 2: downloading the very same YouTube video again first asks yt-dlp for the best height it can get under the
/// download's own format rule. Nothing better means no download, the file stays and the row records the answer. The case
/// is Queen's Fat Bottomed Girls (<c>VMnjF1O4eH0</c>), whose official upload tops out at 360p.
/// </summary>
public sealed class MusicVideoBetterCopyTests : IDisposable
{
    private const string VideoId = "VMnjF1O4eH0";

    private readonly MusicVideoHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private async Task<(Song Song, string Path)> SeedFiled360pAsync()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Fat Bottomed Girls", artist: "Queen");
        var path = _harness.Temp.CreateFile("videos/Queen/Queen - Fat Bottomed Girls.mkv", "the 360p copy");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, VideoId, path, MusicVideoFoundBy.Manual);

        await using var context = _harness.NewContext();
        await context.MusicVideos.ExecuteUpdateAsync(set => set.SetProperty(video => video.Height, 360).SetProperty(video => video.Width, 640));

        return (song, path);
    }

    private async Task<string> ImportAsync(long songId, string videoId = VideoId)
    {
        await using var context = _harness.NewContext();

        return await _harness.Importer(context).ImportAsync(songId, videoId, MusicVideoFoundBy.Manual, CancellationToken.None);
    }

    [Theory]
    [InlineData(360)]
    [InlineData(240)]
    public async Task When_youtube_has_nothing_better_nothing_is_downloaded_and_the_row_records_the_answer(int best)
    {
        var (song, path) = await SeedFiled360pAsync();
        _harness.Downloader.BestHeight = best;

        var message = await ImportAsync(song.Id);

        message.Should().Contain("YouTube has no better copy than " + best + "p");
        _harness.Downloader.Calls.Should().BeEmpty("the video is not downloaded again");
        _harness.Downloader.BestHeightCalls.Should().ContainSingle().Which.Should().Be((VideoId, _harness.Options.MaxHeight));
        File.ReadAllText(path).Should().Be("the 360p copy");

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Present);
        row.BestAvailableHeight.Should().Be(best);
        row.BestAvailableCheckedAt.Should().Be(_harness.Time.GetUtcNow().UtcDateTime);
        row.ReplaceFailureReason.Should().BeNull("not finding a better copy is not a failure");
        (await fresh.History.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task When_youtube_has_a_better_copy_the_download_goes_ahead_and_the_answer_is_cleared_once_it_is_filed()
    {
        var (song, path) = await SeedFiled360pAsync();
        _harness.Downloader.BestHeight = 1080;

        await ImportAsync(song.Id);

        _harness.Downloader.Calls.Should().ContainSingle();
        File.ReadAllText(path).Should().Be("matroska bytes");

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.BestAvailableHeight.Should().BeNull("a new file was filed, so the old answer no longer applies");
        row.BestAvailableCheckedAt.Should().BeNull();
    }

    [Fact]
    public async Task When_the_best_height_is_unknown_the_download_goes_ahead()
    {
        var (song, _) = await SeedFiled360pAsync();
        _harness.Downloader.BestHeight = null;

        await ImportAsync(song.Id);

        _harness.Downloader.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task A_check_that_fails_downloads_nothing_and_leaves_a_failed_replacement_on_the_row()
    {
        var (song, path) = await SeedFiled360pAsync();
        _harness.Downloader.BestHeightFailure = new InvalidOperationException("yt-dlp is busy");

        var act = () => ImportAsync(song.Id);

        (await act.Should().ThrowAsync<MusicVideoImportException>()).Which.Message.Should().Contain("check for a better copy failed");
        _harness.Downloader.Calls.Should().BeEmpty();
        File.ReadAllText(path).Should().Be("the 360p copy");

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Present);
        row.ReplaceFailureReason.Should().Contain("check for a better copy failed");
    }

    [Fact]
    public async Task A_different_video_is_downloaded_without_the_check()
    {
        var (song, _) = await SeedFiled360pAsync();

        await ImportAsync(song.Id, "u7K72X4eo_s");

        _harness.Downloader.BestHeightCalls.Should().BeEmpty();
        _harness.Downloader.Calls.Should().ContainSingle();
    }
}
