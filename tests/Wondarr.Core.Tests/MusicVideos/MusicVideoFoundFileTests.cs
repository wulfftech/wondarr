using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>
/// How downloads, searches and removals treat a video that was already in the folder (ADR-0014, the 2026-11-19
/// amendment): a download never lands on, replaces or recycles one, and removing one is the user's own action.
/// </summary>
public sealed class MusicVideoFoundFileTests : IDisposable
{
    private const string VideoId = "u7K72X4eo_s";

    private readonly MusicVideoHarness _harness = new();
    private readonly ScanProbe _probe = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task A_download_is_refused_onto_the_path_of_a_video_that_a_file_row_of_no_song_holds()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);
        var held = _harness.Video("Massive Attack/Massive Attack - Teardrop.mkv", content: "the user's own video");
        await AddFileRowAsync(held, MusicVideoFileState.Unmatched, songId: null);

        await using (var context = _harness.NewContext())
        {
            var importing = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

            (await importing.Should().ThrowAsync<MusicVideoImportException>())
                .Which.Message.Should().Be("A video already in the folder has this name: Massive Attack - Teardrop.mkv; match it on the Match page");
        }

        _harness.Downloader.Calls.Should().BeEmpty("nothing is downloaded for a name that is taken");
        File.ReadAllText(held).Should().Be("the user's own video");

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Failed);
        row.FailureReason.Should().Be("A video already in the folder has this name: Massive Attack - Teardrop.mkv; match it on the Match page");
        (await fresh.MusicVideoBlocklist.CountAsync()).Should().Be(0, "the video itself is not at fault");
    }

    [Fact]
    public async Task A_download_is_refused_onto_the_path_of_a_video_that_belongs_to_another_song()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        var other = await _harness.AddSongAsync("Angel");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);
        var held = _harness.Video("Massive Attack/Massive Attack - Teardrop.mkv");
        await AddFileRowAsync(held, MusicVideoFileState.Adopted, songId: other.Id);

        await using var context = _harness.NewContext();
        var importing = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

        await importing.Should().ThrowAsync<MusicVideoImportException>().WithMessage("A video already in the folder has this name*");
        File.Exists(held).Should().BeTrue();
    }

    [Fact]
    public async Task A_row_whose_file_has_gone_does_not_hold_its_name()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);
        await AddFileRowAsync(_harness.Temp.Full("videos/Massive Attack/Massive Attack - Teardrop.mkv"), MusicVideoFileState.Missing, songId: null);

        await using (var context = _harness.NewContext())
        {
            await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);
        }

        File.Exists(_harness.Temp.Full("videos/Massive Attack/Massive Attack - Teardrop.mkv")).Should().BeTrue();
        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideoFiles.CountAsync()).Should().Be(0, "nothing is at that path but the download");
    }

    [Fact]
    public async Task A_search_for_a_song_whose_own_adopted_video_is_present_is_already_filed()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        _harness.Video("Massive Attack - Teardrop.mkv");
        await _harness.ScanAndIdentifyAsync(_probe);

        await using var context = _harness.NewContext();
        var queued = await _harness.Search(context)
            .QueueDownloadAsync(song.Id, VideoId, MusicVideoFoundBy.YouTubeSearch, CommandTrigger.Manual, CancellationToken.None);

        queued.Outcome.Should().Be(MusicVideoQueueOutcome.AlreadyFiled, "an adopted video behaves exactly like any present one");
        (await _harness.Search(context).NextMissingAsync(10, CancellationToken.None)).Should().NotContain(song.Id);
    }

    [Fact]
    public async Task A_chosen_download_replaces_an_adopted_video_and_recycles_the_users_file()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        var found = _harness.Video("old collection/clip.mkv", nfo: MusicVideoScanTestKit.Nfo("Teardrop", "Massive Attack"));
        await _harness.ScanAndIdentifyAsync(_probe);

        await using (var context = _harness.NewContext())
        {
            await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.Manual, CancellationToken.None);
        }

        File.Exists(found).Should().BeFalse("a person's pick recycles the found file it replaces");
        Directory.EnumerateFiles(_harness.Temp.Full("bin"), "*", SearchOption.AllDirectories).Should().Contain(path => path.EndsWith("clip.mkv", StringComparison.Ordinal));
        File.Exists(_harness.Temp.Full("videos/Massive Attack/Massive Attack - Teardrop.mkv")).Should().BeTrue();

        await using var fresh = _harness.NewContext();
        var video = await fresh.MusicVideos.SingleAsync();
        video.FoundBy.Should().Be(MusicVideoFoundBy.Manual);
        video.YouTubeVideoId.Should().Be(VideoId);

        var file = await fresh.MusicVideoFiles.SingleAsync(row => row.Path == found);
        file.State.Should().Be(MusicVideoFileState.Ignored, "the file is in the bin and the scan must not adopt it again");
        file.SongId.Should().BeNull();
    }

    [Fact]
    public async Task Removing_a_found_video_recycles_the_users_file_ignores_its_row_blocklists_nothing_and_it_is_not_adopted_again()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        var path = _harness.Video("Massive Attack - Teardrop.mkv", nfo: MusicVideoScanTestKit.Nfo("Teardrop", "Massive Attack"));
        _harness.Video("Other Artist - Other Song.mkv");
        await _harness.ScanAndIdentifyAsync(_probe);

        MusicVideoRemoval? removal;

        await using (var context = _harness.NewContext())
        {
            removal = await _harness.Videos(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);
        }

        removal.Should().NotBeNull();
        removal!.Blocklisted.Should().BeFalse("a found video has no YouTube id to blocklist");
        removal.RecycledPaths.Should().HaveCount(2, "the video and its .nfo");
        removal.RecycledPaths.Should().OnlyContain(recycled => File.Exists(recycled));
        File.Exists(path).Should().BeFalse("removing is the user's own action, and it recycles");

        await using (var after = _harness.NewContext())
        {
            (await after.MusicVideos.CountAsync()).Should().Be(0);
            (await after.MusicVideoBlocklist.CountAsync()).Should().Be(0);
            (await after.MusicVideoFiles.SingleAsync(file => file.Path == path)).State.Should().Be(MusicVideoFileState.Ignored);
        }

        // The user puts a copy back: the scan leaves it alone.
        await File.WriteAllTextAsync(path, "a copy that was put back");
        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Adopted.Should().Be(0);
        await using var check = _harness.NewContext();
        (await check.MusicVideos.CountAsync()).Should().Be(0);
        (await check.MusicVideoFiles.SingleAsync(file => file.Path == path)).State.Should().Be(MusicVideoFileState.Ignored);
    }

    [Fact]
    public async Task Removing_a_downloaded_video_still_blocklists_its_youtube_id()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        var path = _harness.Video("Massive Attack/Massive Attack - Teardrop.mkv");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, path: path);

        await using var context = _harness.NewContext();
        var removal = await _harness.Videos(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        removal!.Blocklisted.Should().BeTrue();
    }

    [Fact]
    public async Task No_mover_loads_or_moves_a_found_video_even_beside_track()
    {
        await _harness.InitAsync();
        _harness.Options.Layout = MusicVideoOptions.BesideTrackLayout;
        var song = await _harness.AddSongAsync("Teardrop");
        var path = _harness.Video("old/clip.mkv");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, videoId: string.Empty, path: path, foundBy: MusicVideoFoundBy.Existing);

        await using var context = _harness.NewContext();

        (await BesideTrackVideoMover.TryLoadAsync(context, song.Id)).Should().BeNull("the loader every mover uses skips a found video");

        var row = await context.MusicVideos.AsNoTracking().SingleAsync();
        var landed = _harness.Temp.CreateFile("music/Massive Attack/03 - Teardrop.flac");
        var moved = await BesideTrackVideoMover.TryAlignAsync(context, new DiskOperations(), _harness.Options, row, landed);

        moved.Should().BeNull();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_a_song_leaves_its_found_video_where_it_lies_and_the_file_is_identified_again()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        var path = _harness.Video("Massive Attack - Teardrop.mkv");
        var downloaded = _harness.Video("dl/Other - Song.mkv");
        var other = await _harness.AddSongAsync("Song", artist: "Other");
        await _harness.AddRowAsync(other.Id, MusicVideoStatus.Present, path: downloaded);
        await _harness.ScanAndIdentifyAsync(_probe);

        await using (var context = _harness.NewContext())
        {
            await _harness.Videos(context).RecycleForDeletedSongsAsync([song.Id, other.Id], CancellationToken.None);
        }

        File.Exists(path).Should().BeTrue("a found video is the user's, and deleting the song does not recycle it");
        File.Exists(downloaded).Should().BeFalse("a downloaded video still goes with its song");

        await using (var context = _harness.NewContext())
        {
            await context.Songs.Where(candidate => candidate.Id == song.Id).ExecuteDeleteAsync();
        }

        _harness.Video("Unrelated Artist - Unrelated.mkv");
        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Unmatched.Should().BeGreaterThan(0);
        (await _harness.FilesAsync()).Single(file => file.Path == path).SongId.Should().BeNull();
    }

    [Fact]
    public async Task After_a_found_video_is_removed_a_download_may_take_its_name_whatever_the_case()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        var path = _harness.Video("Massive Attack/Massive Attack - Teardrop.mkv");
        _harness.Video("Other Artist - Other.mkv");
        await _harness.ScanAndIdentifyAsync(_probe);

        await using (var context = _harness.NewContext())
        {
            await _harness.Videos(context).DeleteAsync(song.Id, blocklist: false, CancellationToken.None);
        }

        File.Exists(path).Should().BeFalse();
        (await _harness.FilesAsync()).Single(file => file.Path == path).MissingSince.Should().NotBeNull();
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);

        await using (var context = _harness.NewContext())
        {
            await _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);
        }

        File.Exists(path).Should().BeTrue("the removed video's name is free again");
    }

    [Fact]
    public async Task A_name_that_differs_only_in_case_is_still_held()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);
        var held = _harness.Video("Massive Attack/MASSIVE ATTACK - TEARDROP.mkv");
        await AddFileRowAsync(held, MusicVideoFileState.Unmatched, songId: null);

        await using var context = _harness.NewContext();
        var importing = () => _harness.Importer(context).ImportAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CancellationToken.None);

        await importing.Should().ThrowAsync<MusicVideoImportException>().WithMessage("A video already in the folder has this name*");
    }

    private async Task AddFileRowAsync(string path, MusicVideoFileState state, long? songId)
    {
        await using var context = _harness.NewContext();
        context.MusicVideoFiles.Add(new MusicVideoFile
        {
            Path = path,
            Size = 20,
            ModifiedAt = DateTime.UtcNow,
            State = state,
            SongId = songId,
        });
        await context.SaveChangesAsync();
    }
}
