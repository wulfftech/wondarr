using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>
/// Downloading, checking and filing one video (ADR-0014): a fake downloader writes real files, a fake probe says what
/// they are, and the real resolver, recycle bin and disk put them in place under a temporary folder.
/// </summary>
public sealed class MusicVideoImporterTests : IDisposable
{
    private const string VideoId = "u7K72X4eo_s";

    private readonly MusicVideoHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task A_download_is_checked_filed_with_its_sidecars_and_the_row_is_filled_in()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);
        _harness.Options.MaxHeight = 1080;

        await using (var context = _harness.NewContext())
        {
            var message = await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

            message.Should().Contain("filed");
        }

        var video = _harness.Temp.Full("videos/Massive Attack/Massive Attack - Teardrop.mkv");
        File.Exists(video).Should().BeTrue();
        File.Exists(_harness.Temp.Full("videos/Massive Attack/Massive Attack - Teardrop.nfo")).Should().BeTrue();
        File.Exists(_harness.Temp.Full("videos/Massive Attack/Massive Attack - Teardrop-thumb.jpg")).Should().BeTrue("Kodi and Jellyfin read that name");
        _harness.Downloader.Calls.Should().ContainSingle().Which.Should().Be((VideoId, (int?)1080));
        _harness.Downloader.Discarded.Should().ContainSingle();
        Directory.Exists(_harness.Downloader.WorkFolders[0]).Should().BeFalse("the work folder is removed");

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Present);
        row.Path.Should().Be(video);
        row.Width.Should().Be(1920);
        row.Height.Should().Be(1080);
        row.VideoCodec.Should().Be("h264");
        row.AudioCodec.Should().Be("aac");
        row.SizeBytes.Should().Be(14);
        row.DurationMs.Should().Be(284_000);
        row.FailureReason.Should().BeNull();

        var history = await fresh.History.SingleAsync();
        history.EventType.Should().Be(HistoryEventType.MusicVideoImported);
        (await fresh.MusicVideoBlocklist.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Nothing_but_an_mkv_is_ever_filed()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);
        }

        Directory.GetFiles(_harness.VideoRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(LibraryRoots.MarkerFileName, StringComparison.Ordinal))
            .Should().OnlyContain(path => path.EndsWith(".mkv", StringComparison.Ordinal)
                || path.EndsWith(".nfo", StringComparison.Ordinal)
                || path.EndsWith("-thumb.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_nfo_is_utf8_without_a_byte_order_mark_and_names_the_song()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Tear & Drop <Live>", artist: "Massive Attack");
        await using (var seed = _harness.NewContext())
        {
            var context = await seed.AlbumContexts.FirstAsync(row => row.SongId == song.Id);
            context.Kind = AlbumContextKind.Album;
            context.AlbumTitle = "Mezzanine";
            context.Date = "1998-04-17";
            await seed.SaveChangesAsync();
        }

        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);
        _harness.Options.NamingTemplate = "{Artist}/{Artist} - Teardrop";

        await using (var context = _harness.NewContext())
        {
            await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);
        }

        var nfo = _harness.Temp.Full("videos/Massive Attack/Massive Attack - Teardrop.nfo");
        var bytes = await File.ReadAllBytesAsync(nfo);
        bytes.Take(3).Should().NotEqual(Encoding.UTF8.GetPreamble(), "no byte order mark");

        var document = XDocument.Parse(Encoding.UTF8.GetString(bytes));
        document.Root!.Name.LocalName.Should().Be("musicvideo");
        document.Root.Element("title")!.Value.Should().Be("Tear & Drop <Live>");
        document.Root.Element("artist")!.Value.Should().Be("Massive Attack");
        document.Root.Element("album")!.Value.Should().Be("Mezzanine");
        document.Root.Element("year")!.Value.Should().Be("1998");
    }

    [Fact]
    public async Task The_sidecars_follow_their_switches()
    {
        await _harness.InitAsync();
        _harness.Options.WriteNfo = false;
        _harness.Options.WriteThumbnail = false;
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);
        }

        File.Exists(_harness.Temp.Full("videos/Massive Attack/Massive Attack - Teardrop.mkv")).Should().BeTrue();
        File.Exists(_harness.Temp.Full("videos/Massive Attack/Massive Attack - Teardrop.nfo")).Should().BeFalse();
        File.Exists(_harness.Temp.Full("videos/Massive Attack/Massive Attack - Teardrop-thumb.jpg")).Should().BeFalse();
    }

    [Fact]
    public async Task Beside_track_files_the_video_next_to_the_audio_with_the_suffix()
    {
        await _harness.InitAsync();
        _harness.Options.Layout = MusicVideoOptions.BesideTrackLayout;
        var audio = _harness.Temp.CreateFile("music/Massive Attack/Mezzanine/03 - Teardrop.flac");
        var song = await _harness.AddSongAsync(audioPath: audio);
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);
        }

        var video = _harness.Temp.Full("music/Massive Attack/Mezzanine/03 - Teardrop-video.mkv");
        File.Exists(video).Should().BeTrue();
        File.Exists(_harness.Temp.Full("music/Massive Attack/Mezzanine/03 - Teardrop-video.nfo")).Should().BeTrue();
        File.Exists(_harness.Temp.Full("music/Massive Attack/Mezzanine/03 - Teardrop-video-thumb.jpg")).Should().BeTrue();
        File.Exists(audio).Should().BeTrue("the audio file is never touched");
        (await File.ReadAllTextAsync(audio)).Should().Be("content");
    }

    [Fact]
    public async Task A_missing_video_stream_fails_the_row_and_blocklists_the_video()
    {
        await _harness.InitAsync();
        _harness.Probe.Result = FakeVideoProbe.Ok(videoStreams: 0);
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            var act = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

            (await act.Should().ThrowAsync<MusicVideoImportException>()).Which.Message.Should().Contain("no video stream");
        }

        await AssertFailedAndBlocklistedAsync(song.Id, "no video stream");
        _harness.Downloader.Discarded.Should().ContainSingle("the temporary files are deleted");
        Directory.Exists(_harness.Downloader.WorkFolders[0]).Should().BeFalse();
        Directory.GetFiles(_harness.VideoRoot, "*.mkv", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_audio_stream_fails_the_row_and_blocklists_the_video()
    {
        await _harness.InitAsync();
        _harness.Probe.Result = FakeVideoProbe.Ok(audioStreams: 0);
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            var act = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

            await act.Should().ThrowAsync<MusicVideoImportException>();
        }

        await AssertFailedAndBlocklistedAsync(song.Id, "no audio stream");
    }

    [Fact]
    public async Task A_length_more_than_15_seconds_from_the_reported_one_fails_the_row_and_blocklists_the_video()
    {
        await _harness.InitAsync();
        _harness.Downloader.ReportedDurationMs = 284_000;
        _harness.Probe.Result = FakeVideoProbe.Ok(durationMs: 284_000 + 15_001);
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            var act = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

            await act.Should().ThrowAsync<MusicVideoImportException>();
        }

        await AssertFailedAndBlocklistedAsync(song.Id, "YouTube says");
    }

    [Fact]
    public async Task A_length_within_15_seconds_is_accepted()
    {
        await _harness.InitAsync();
        _harness.Downloader.ReportedDurationMs = 284_000;
        _harness.Probe.Result = FakeVideoProbe.Ok(durationMs: 284_000 - 15_000);
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);
        }

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.SingleAsync()).Status.Should().Be(MusicVideoStatus.Present);
    }

    [Fact]
    public async Task A_file_the_probe_cannot_read_fails_the_row_and_blocklists_the_video()
    {
        await _harness.InitAsync();
        _harness.Probe.Result = new VideoProbeResult(false, null, "Invalid data found when processing input");
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            var act = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

            await act.Should().ThrowAsync<MusicVideoImportException>();
        }

        await AssertFailedAndBlocklistedAsync(song.Id, "could not be read");
    }

    [Fact]
    public async Task A_probe_that_ran_out_of_time_fails_the_row_but_does_not_blocklist_the_video()
    {
        await _harness.InitAsync();
        _harness.Probe.Result = new VideoProbeResult(false, null, "ffprobe timed out", TimedOut: true);
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            var act = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

            await act.Should().ThrowAsync<MusicVideoImportException>();
        }

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.SingleAsync()).Status.Should().Be(MusicVideoStatus.Failed);
        (await fresh.MusicVideoBlocklist.CountAsync()).Should().Be(0, "a tool that timed out says nothing about the video");
    }

    [Fact]
    public async Task A_failed_download_fails_the_row_without_blocklisting()
    {
        await _harness.InitAsync();
        _harness.Downloader.Failure = new InvalidOperationException("Video unavailable");
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            var act = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

            (await act.Should().ThrowAsync<MusicVideoImportException>()).Which.Message.Should().Contain("Video unavailable");
        }

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Failed);
        row.FailureReason.Should().Contain("Video unavailable");
        (await fresh.MusicVideoBlocklist.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_row_removed_while_downloading_drops_the_temporary_files()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);
        _harness.Downloader.DuringDownload = async () =>
        {
            await using var other = _harness.NewContext();
            await other.MusicVideos.ExecuteDeleteAsync();
        };

        await using (var context = _harness.NewContext())
        {
            var message = await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

            message.Should().Contain("removed").And.Contain("dropped");
        }

        _harness.Downloader.Discarded.Should().ContainSingle();
        Directory.Exists(_harness.Downloader.WorkFolders[0]).Should().BeFalse();
        Directory.GetFiles(_harness.VideoRoot, "*.mkv", SearchOption.AllDirectories).Should().BeEmpty("nothing is filed for a video that was removed");
        _harness.Probe.Probed.Should().BeEmpty("a removed row is not worth probing");

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.CountAsync()).Should().Be(0, "the row is not written back");
        (await fresh.MusicVideoBlocklist.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_row_that_is_gone_before_the_download_starts_downloads_nothing()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");

        await using var context = _harness.NewContext();
        var message = await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

        message.Should().Contain("nothing was downloaded");
        _harness.Downloader.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_marker_defers_before_downloading_and_leaves_the_song_searchable()
    {
        await _harness.InitAsync();
        File.Delete(Path.Combine(_harness.VideoRoot, LibraryRoots.MarkerFileName));
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            var message = await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

            message.Should().Contain("deferred").And.Contain(LibraryRoots.MarkerFileName);
        }

        _harness.Downloader.Calls.Should().BeEmpty("nothing is downloaded for a video that cannot be filed");
        Directory.GetFiles(_harness.VideoRoot, "*.mkv", SearchOption.AllDirectories).Should().BeEmpty();

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.CountAsync()).Should().Be(0, "a deferred video is tried again later, not marked failed");
    }

    [Fact]
    public async Task A_marker_that_goes_while_downloading_defers_and_drops_the_files()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);
        _harness.Downloader.DuringDownload = () =>
        {
            File.Delete(Path.Combine(_harness.VideoRoot, LibraryRoots.MarkerFileName));

            return Task.CompletedTask;
        };

        await using (var context = _harness.NewContext())
        {
            var message = await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

            message.Should().Contain("deferred");
        }

        Directory.GetFiles(_harness.VideoRoot, "*.mkv", SearchOption.AllDirectories).Should().BeEmpty();
        _harness.Downloader.Discarded.Should().ContainSingle();
    }

    [Fact]
    public async Task A_file_already_at_the_destination_is_never_overwritten()
    {
        await _harness.InitAsync();
        var existing = _harness.Temp.CreateFile("videos/Massive Attack/Massive Attack - Teardrop.mkv", "someone else's");
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            var act = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

            await act.Should().ThrowAsync<MusicVideoImportException>();
        }

        (await File.ReadAllTextAsync(existing)).Should().Be("someone else's");
        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Failed);
        (await fresh.MusicVideoBlocklist.CountAsync()).Should().Be(0, "the video is not at fault");
    }

    [Fact]
    public async Task Replacing_a_present_video_recycles_the_old_file_and_its_sidecars()
    {
        await _harness.InitAsync();
        _harness.Options.Layout = MusicVideoOptions.BesideTrackLayout;
        var audio = _harness.Temp.CreateFile("music/Massive Attack/03 - Teardrop.flac");
        var song = await _harness.AddSongAsync(audioPath: audio);

        // The old video lies in the video root (the layout was folder when it was filed), so it moves by the new layout.
        var oldVideo = _harness.Temp.CreateFile("videos/Massive Attack/Massive Attack - Teardrop.mkv", "old video");
        var oldNfo = _harness.Temp.CreateFile("videos/Massive Attack/Massive Attack - Teardrop.nfo", "old nfo");
        var oldThumb = _harness.Temp.CreateFile("videos/Massive Attack/Massive Attack - Teardrop-thumb.jpg", "old thumb");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, "AAAAAAAAAAA", oldVideo, MusicVideoFoundBy.YouTubeSearch);

        await using (var context = _harness.NewContext())
        {
            await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.Manual, CancellationToken.None);
        }

        File.Exists(oldVideo).Should().BeFalse();
        File.Exists(oldNfo).Should().BeFalse();
        File.Exists(oldThumb).Should().BeFalse();
        RecycledFiles().Should().HaveCount(3, "the old video and its two sidecars are in the recycle bin");
        File.Exists(_harness.Temp.Full("music/Massive Attack/03 - Teardrop-video.mkv")).Should().BeTrue();

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Present);
        row.YouTubeVideoId.Should().Be(VideoId);
        row.FoundBy.Should().Be(MusicVideoFoundBy.Manual);
        row.Path.Should().Be(_harness.Temp.Full("music/Massive Attack/03 - Teardrop-video.mkv"));
    }

    [Fact]
    public async Task Replacing_a_video_where_it_lies_recycles_the_old_one_first()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        var oldVideo = _harness.Temp.CreateFile("videos/Massive Attack/Massive Attack - Teardrop.mkv", "old video");
        var oldNfo = _harness.Temp.CreateFile("videos/Massive Attack/Massive Attack - Teardrop.nfo", "old nfo");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, "AAAAAAAAAAA", oldVideo, MusicVideoFoundBy.YouTubeSearch);

        await using (var context = _harness.NewContext())
        {
            await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.Manual, CancellationToken.None);
        }

        (await File.ReadAllTextAsync(oldVideo)).Should().Be("matroska bytes", "the new video is at the old path");
        (await File.ReadAllTextAsync(oldNfo)).Should().Contain("<musicvideo>", "the old .nfo was recycled and a new one written");
        RecycledFiles().Should().HaveCount(2, "the old video and its .nfo");
    }

    [Fact]
    public async Task A_replacement_that_fails_leaves_the_present_video_alone()
    {
        await _harness.InitAsync();
        _harness.Probe.Result = FakeVideoProbe.Ok(videoStreams: 0);
        var song = await _harness.AddSongAsync("Teardrop");
        var oldVideo = _harness.Temp.CreateFile("videos/Massive Attack/Massive Attack - Teardrop.mkv", "old video");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, "AAAAAAAAAAA", oldVideo, MusicVideoFoundBy.YouTubeSearch);

        await using (var context = _harness.NewContext())
        {
            var act = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.Manual, CancellationToken.None);

            await act.Should().ThrowAsync<MusicVideoImportException>();
        }

        (await File.ReadAllTextAsync(oldVideo)).Should().Be("old video");
        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Present);
        row.YouTubeVideoId.Should().Be("AAAAAAAAAAA");
        (await fresh.MusicVideoBlocklist.SingleAsync()).YouTubeVideoId.Should().Be(VideoId, "the video that failed the check is blocklisted");
    }

    [Fact]
    public async Task While_music_videos_are_off_nothing_is_downloaded()
    {
        await _harness.InitAsync();
        _harness.Options.Enabled = false;
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using var context = _harness.NewContext();
        var message = await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

        message.Should().Contain("turned off");
        _harness.Downloader.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_row_that_now_wants_another_video_is_left_alone()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued, "BBBBBBBBBBB");

        await using var context = _harness.NewContext();
        var message = await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

        message.Should().Contain("another video");
        _harness.Downloader.Calls.Should().BeEmpty();
    }

    /// <summary>What is in the recycle bin, without the marker file the bin writes into its root.</summary>
    private List<string> RecycledFiles() =>
        Directory.Exists(_harness.Temp.Full("bin"))
            ? [.. Directory.GetFiles(_harness.Temp.Full("bin"), "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetFileName(path).StartsWith(".wondarr", StringComparison.Ordinal))]
            : [];

    private async Task AssertFailedAndBlocklistedAsync(long songId, string reason)
    {
        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Failed);
        row.FailureReason.Should().Contain(reason);
        row.Path.Should().BeNull();

        var blocked = await fresh.MusicVideoBlocklist.SingleAsync();
        blocked.SongId.Should().Be(songId);
        blocked.YouTubeVideoId.Should().Be(VideoId);
        blocked.Reason.Should().Contain(reason);
    }
}
