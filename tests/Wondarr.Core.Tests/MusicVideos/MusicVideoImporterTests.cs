using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.Tests.Organizer;
using Wondarr.Testing;
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
    public async Task A_row_is_queued_while_the_download_waits_for_the_gate_and_downloading_once_it_starts()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        MusicVideoStatus? whileWaiting = null;
        MusicVideoStatus? whileRunning = null;

        _harness.Downloader.WaitingForGate = async () =>
        {
            await using var peek = _harness.NewContext();
            whileWaiting = (await peek.MusicVideos.SingleAsync()).Status;
        };
        _harness.Downloader.DuringDownload = async () =>
        {
            await using var peek = _harness.NewContext();
            whileRunning = (await peek.MusicVideos.SingleAsync()).Status;
        };

        await using (var context = _harness.NewContext())
        {
            await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);
        }

        whileWaiting.Should().Be(MusicVideoStatus.Queued, "a download that waits for the yt-dlp gate has not started");
        whileRunning.Should().Be(MusicVideoStatus.Downloading);
    }

    [Fact]
    public async Task The_title_of_the_chosen_video_is_stored_on_the_row()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.Manual, CancellationToken.None, "Massive Attack - Teardrop (Official Video)");
        }

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.SingleAsync()).VideoTitle.Should().Be("Massive Attack - Teardrop (Official Video)");
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
    public async Task A_missing_marker_defers_before_downloading_and_leaves_a_failed_row_that_a_new_pick_replaces()
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
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Failed, "the row stays visible");
        row.FailureReason.Should().Contain(LibraryRoots.MarkerFileName);
        (await fresh.MusicVideoBlocklist.CountAsync()).Should().Be(0, "the video is not at fault");

        // The song stays searchable: a new pick replaces the failed row.
        (await _harness.Search(fresh).QueueDownloadAsync(song.Id, VideoId, MusicVideoFoundBy.Manual, Wondarr.Core.Jobs.CommandTrigger.Manual, CancellationToken.None))
            .Outcome.Should().Be(MusicVideoQueueOutcome.Queued);
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
        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.SingleAsync()).Status.Should().Be(MusicVideoStatus.Failed);
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
    public async Task A_same_path_replacement_that_cannot_be_filed_puts_the_old_files_back()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        var oldVideo = _harness.Temp.CreateFile("videos/Massive Attack/Massive Attack - Teardrop.mkv", "old video");
        var oldNfo = _harness.Temp.CreateFile("videos/Massive Attack/Massive Attack - Teardrop.nfo", "old nfo");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, "AAAAAAAAAAA", oldVideo, MusicVideoFoundBy.YouTubeSearch);
        _harness.Disk = new FailingPlaceDisk { FailSwap = true };

        await using (var context = _harness.NewContext())
        {
            var act = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.Manual, CancellationToken.None);

            (await act.Should().ThrowAsync<MusicVideoImportException>()).Which.Message.Should().Contain("could not be filed");
        }

        File.Exists(oldVideo + MusicVideoImporter.StagingSuffix).Should().BeFalse("the staged copy is removed");
        (await File.ReadAllTextAsync(oldVideo)).Should().Be("old video");
        (await File.ReadAllTextAsync(oldNfo)).Should().Be("old nfo");
        RecycledFiles().Should().BeEmpty("the old files are back where they were");
        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Present);
        row.YouTubeVideoId.Should().Be("AAAAAAAAAAA");
        row.Path.Should().Be(oldVideo);
    }

    [Fact]
    public async Task When_the_old_files_cannot_be_put_back_it_is_logged_at_error_with_both_paths()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        var oldVideo = _harness.Temp.CreateFile("videos/Massive Attack/Massive Attack - Teardrop.mkv", "old video");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, "AAAAAAAAAAA", oldVideo, MusicVideoFoundBy.YouTubeSearch);
        _harness.Disk = new FailingPlaceDisk { FailMoveBack = true, FailSwap = true };
        var logger = new ErrorCapture();

        await using (var context = _harness.NewContext())
        {
            var importer = new MusicVideoImporter(
                context,
                new TestOptionsMonitor<MusicVideoOptions>(_harness.Options),
                _harness.Resolver(context),
                _harness.Downloader,
                _harness.Probe,
                _harness.Videos(context),
                _harness.Disk,
                new TestOptionsMonitor<ImportOptions>(_harness.Import),
                logger);

            var act = () => importer.ImportAsync(song.Id, VideoId, MusicVideoFoundBy.Manual, CancellationToken.None);

            await act.Should().ThrowAsync<MusicVideoImportException>();
        }

        logger.Errors.Should().ContainSingle().Which.Should().Contain(oldVideo).And.Contain(_harness.Temp.Full("bin"));
    }

    [UnixFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task The_configured_modes_are_set_on_the_video_its_sidecars_and_the_folders_it_created()
    {
        await _harness.InitAsync();
        _harness.Import.SetPermissions = true;
        _harness.Import.FileMode = "0640";
        _harness.Import.FolderMode = "0750";
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);
        }

        var folder = _harness.Temp.Full("videos/Massive Attack");
        File.GetUnixFileMode(folder).Should().Be((UnixFileMode)Convert.ToInt32("0750", 8));
        foreach (var name in new[] { ".mkv", ".nfo", "-thumb.jpg" })
        {
            File.GetUnixFileMode(Path.Combine(folder, "Massive Attack - Teardrop" + name)).Should().Be((UnixFileMode)Convert.ToInt32("0640", 8), name);
        }
    }

    [UnixFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Modes_are_left_alone_while_the_setting_is_off()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);
        var before = File.GetUnixFileMode(_harness.VideoRoot);

        await using (var context = _harness.NewContext())
        {
            await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);
        }

        File.GetUnixFileMode(Path.Combine(_harness.VideoRoot, "Massive Attack")).Should().NotBe((UnixFileMode)Convert.ToInt32("0750", 8));
        File.GetUnixFileMode(_harness.VideoRoot).Should().Be(before);
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
    public async Task A_download_cancelled_half_way_marks_the_row_failed_instead_of_leaving_it_downloading()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);
        _harness.Downloader.Failure = new OperationCanceledException();

        await using (var context = _harness.NewContext())
        {
            var act = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Failed);
        row.FailureReason.Should().Contain("interrupted");
    }

    [Fact]
    public async Task An_unexpected_exception_marks_the_row_failed_and_a_filed_video_is_not_touched()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        var filed = await _harness.AddSongAsync("Angel");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);
        await _harness.AddRowAsync(filed.Id, MusicVideoStatus.Present, VideoId, "/videos/y.mkv");
        _harness.Probe.Result = null!;

        await using (var context = _harness.NewContext())
        {
            var act = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

            await act.Should().ThrowAsync<Exception>();
        }

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.SingleAsync(row => row.SongId == song.Id)).Status.Should().Be(MusicVideoStatus.Failed);
        (await fresh.MusicVideos.SingleAsync(row => row.SongId == filed.Id)).Status.Should().Be(MusicVideoStatus.Present);
    }

    [Fact]
    public async Task A_control_character_in_the_artist_name_does_not_stop_the_video_being_filed()
    {
        await _harness.InitAsync();
        _harness.Options.NamingTemplate = "{Title}";
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive\u0001 Attack\u0007");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);
        }

        File.Exists(_harness.Temp.Full("videos/Teardrop.mkv")).Should().BeTrue();
        var nfo = await File.ReadAllTextAsync(_harness.Temp.Full("videos/Teardrop.nfo"));
        XDocument.Parse(nfo).Root!.Element("artist")!.Value.Should().Be("Massive Attack");
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

    /// <summary>A disk whose placing of the new video fails, and optionally whose move-back does too.</summary>
    internal sealed class FailingPlaceDisk : IDiskOperations
    {
        private readonly DiskOperations _real = new();

        public bool FailMoveBack { get; init; }

        /// <summary>Gets a value indicating whether staging works but the rename of the staged video into place fails.</summary>
        public bool FailSwap { get; init; }

        public bool FileExists(string path) => _real.FileExists(path);

        public bool DirectoryExists(string path) => _real.DirectoryExists(path);

        public void CreateDirectory(string path) => _real.CreateDirectory(path);

        public void CreateEmptyFile(string path) => _real.CreateEmptyFile(path);

        public void WriteNewFile(string path, byte[] bytes) => _real.WriteNewFile(path, bytes);

        public bool HasAnyEntry(string directory) => _real.HasAnyEntry(directory);

        public long GetFileSize(string path) => _real.GetFileSize(path);

        public void MoveFile(string source, string target)
        {
            if (FailMoveBack || (FailSwap && source.EndsWith(MusicVideoImporter.StagingSuffix, StringComparison.Ordinal)))
            {
                throw new IOException("cannot move back");
            }

            _real.MoveFile(source, target);
        }

        public void CopyFile(string source, string target)
        {
            if (!FailSwap)
            {
                throw new IOException("disk full");
            }

            _real.CopyFile(source, target);
        }

        public bool TryCreateHardLink(string source, string target) => false;

        public void WriteAllBytes(string path, byte[] bytes) => _real.WriteAllBytes(path, bytes);

        public void DeleteFile(string path) => _real.DeleteFile(path);

        public void SetUnixFileMode(string path, UnixFileMode mode) => _real.SetUnixFileMode(path, mode);

        public bool AreSameFile(string first, string second) => _real.AreSameFile(first, second);

        public IEnumerable<string> EnumerateFiles(string directory) => _real.EnumerateFiles(directory);

        public DateTime GetLastWriteTimeUtc(string path) => _real.GetLastWriteTimeUtc(path);

        public void SetLastWriteTimeUtc(string path, DateTime utc) => _real.SetLastWriteTimeUtc(path, utc);

        public void DeleteEmptyDirectory(string path) => _real.DeleteEmptyDirectory(path);
    }

    /// <summary>Keeps the errors the importer logs, rendered.</summary>
    private sealed class ErrorCapture : Microsoft.Extensions.Logging.ILogger<MusicVideoImporter>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }
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
