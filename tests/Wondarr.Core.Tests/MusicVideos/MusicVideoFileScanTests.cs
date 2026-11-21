using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Media;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.References;
using Wondarr.Core.Tests.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>
/// The scan of the video folder and the identification of what it finds (ADR-0014, the 2026-11-19 amendment): temporary
/// folders, a fake probe and the real database, so no ffprobe and no network.
/// </summary>
public sealed class MusicVideoFileScanTests : IDisposable
{
    private readonly MusicVideoHarness _harness = new();
    private readonly ScanProbe _probe = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task A_clean_name_with_a_matching_nfo_is_adopted_where_it_lies_and_the_song_shows_a_present_video()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        var path = _harness.Video("Massive Attack - Teardrop.mkv", nfo: MusicVideoScanTestKit.Nfo("Teardrop", "Massive Attack"));

        var (scan, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        scan.Added.Should().Be(1);
        identified.Adopted.Should().Be(1);

        await using var context = _harness.NewContext();
        var video = await context.MusicVideos.SingleAsync();
        video.SongId.Should().Be(song.Id);
        video.Status.Should().Be(MusicVideoStatus.Present);
        video.FoundBy.Should().Be(MusicVideoFoundBy.Existing);
        video.Path.Should().Be(path, "the file is linked where it lies");
        video.YouTubeVideoId.Should().BeEmpty();
        video.Height.Should().Be(1080);

        var file = await context.MusicVideoFiles.SingleAsync();
        file.State.Should().Be(MusicVideoFileState.Adopted);
        file.SongId.Should().Be(song.Id);
        file.NfoTitle.Should().Be("Teardrop");
        file.NfoArtist.Should().Be("Massive Attack");
        file.Container.Should().Be("matroska,webm");
        File.Exists(path).Should().BeTrue("a found file is never moved, renamed or rewritten");
        File.ReadAllText(path).Should().Be("video bytes");

        (await context.History.CountAsync(item => item.EventType == HistoryEventType.MusicVideoImported)).Should().Be(1);
    }

    [Fact]
    public async Task The_scan_stores_the_codecs_and_an_adopted_video_shows_them()
    {
        await _harness.InitAsync();
        await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("Massive Attack - Teardrop.mkv", nfo: MusicVideoScanTestKit.Nfo("Teardrop", "Massive Attack"));

        await _harness.ScanAndIdentifyAsync(_probe);

        await using var context = _harness.NewContext();
        var file = await context.MusicVideoFiles.SingleAsync();
        file.VideoCodec.Should().Be("h264");
        file.AudioCodec.Should().Be("aac");

        var video = await context.MusicVideos.SingleAsync();
        video.VideoCodec.Should().Be("h264", "copied on adoption");
        video.AudioCodec.Should().Be("aac");
    }

    [Fact]
    public async Task A_file_probed_before_codecs_were_kept_is_probed_once_more_and_its_adopted_video_gets_them()
    {
        await _harness.InitAsync();
        await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("Massive Attack - Teardrop.mkv", nfo: MusicVideoScanTestKit.Nfo("Teardrop", "Massive Attack"));
        await _harness.ScanAndIdentifyAsync(_probe);

        // The state before this change: probed and adopted, no codecs on either row.
        await using (var seed = _harness.NewContext())
        {
            var file = await seed.MusicVideoFiles.SingleAsync();
            file.VideoCodec = null;
            file.AudioCodec = null;
            var video = await seed.MusicVideos.SingleAsync();
            video.VideoCodec = null;
            video.AudioCodec = null;
            await seed.SaveChangesAsync();
        }

        _probe.Probed.Clear();
        var second = await _harness.ScanAndIdentifyAsync(_probe);

        second.Scan.Probed.Should().Be(1, "one probe backfills the codecs");
        _probe.Probed.Should().HaveCount(1);

        await using (var context = _harness.NewContext())
        {
            (await context.MusicVideoFiles.SingleAsync()).VideoCodec.Should().Be("h264");
            var video = await context.MusicVideos.SingleAsync();
            video.VideoCodec.Should().Be("h264");
            video.AudioCodec.Should().Be("aac");
            video.Status.Should().Be(MusicVideoStatus.Present);
        }

        _probe.Probed.Clear();
        var third = await _harness.ScanAndIdentifyAsync(_probe);

        third.Scan.Probed.Should().Be(0, "the backfill happens once");
        _probe.Probed.Should().BeEmpty();
    }

    [Fact]
    public async Task The_nfo_alone_identifies_a_file_whose_name_says_nothing()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("clip001.mkv", nfo: MusicVideoScanTestKit.Nfo("Teardrop", "Massive Attack"));

        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Adopted.Should().Be(1);
        (await _harness.FilesAsync()).Single().SongId.Should().Be(song.Id);
    }

    [Fact]
    public async Task The_containers_own_tags_identify_a_file_whose_name_says_nothing()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("clip002.mp4");
        _probe.Answer = _ => ScanProbe.Video(tagTitle: "Teardrop", tagArtist: "Massive Attack");

        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Adopted.Should().Be(1);
        var file = (await _harness.FilesAsync()).Single();
        file.SongId.Should().Be(song.Id);
        file.TagTitle.Should().Be("Teardrop");
        file.TagArtist.Should().Be("Massive Attack");
    }

    [Fact]
    public async Task The_configured_template_and_the_flat_form_are_read_and_mp4_and_avi_are_accepted()
    {
        await _harness.InitAsync();
        var teardrop = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        var creep = await _harness.AddSongAsync("Creep", artist: "Radiohead");
        var angel = await _harness.AddSongAsync("Angel", artist: "Massive Attack");
        _harness.Video("Massive Attack/Massive Attack - Teardrop.mp4");
        _harness.Video("Radiohead - Creep (Official Video).avi");
        _harness.Video("Massive Attack – Angel.mov");

        var (scan, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        scan.Seen.Should().Be(3);
        identified.Adopted.Should().Be(3);

        var files = await _harness.FilesAsync();
        files.Single(file => file.Path.EndsWith("Teardrop.mp4", StringComparison.Ordinal)).SongId.Should().Be(teardrop.Id);
        files.Single(file => file.Path.EndsWith("(Official Video).avi", StringComparison.Ordinal)).SongId.Should().Be(creep.Id, "the decoration is stripped");
        files.Single(file => file.Path.EndsWith("Angel.mov", StringComparison.Ordinal)).SongId.Should().Be(angel.Id, "an en dash separates too");
    }

    [Fact]
    public async Task Two_songs_with_the_same_title_are_told_apart_by_their_artist()
    {
        await _harness.InitAsync();
        var alpha = await _harness.AddSongAsync("Home", artist: "Alpha");
        await _harness.AddSongAsync("Home", artist: "Beta");
        _harness.Video("Alpha - Home.mkv");

        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Adopted.Should().Be(1);
        (await _harness.FilesAsync()).Single().SongId.Should().Be(alpha.Id);
    }

    [Fact]
    public async Task The_same_artist_and_title_on_two_songs_is_ambiguous()
    {
        await _harness.InitAsync();
        var studio = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        var other = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("Massive Attack - Teardrop.mkv");

        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Adopted.Should().Be(0);
        identified.Ambiguous.Should().Be(1);

        var file = (await _harness.FilesAsync()).Single();
        file.State.Should().Be(MusicVideoFileState.Ambiguous);
        file.SongId.Should().BeNull();
        MusicVideoFileRules.Read(file.Candidates).Select(candidate => candidate.SongId).Should().BeEquivalentTo([studio.Id, other.Id]);

        await using var context = _harness.NewContext();
        (await context.MusicVideos.CountAsync()).Should().Be(0, "a wrong link is worse than none");
    }

    [Fact]
    public async Task A_title_that_is_close_but_not_exact_is_ambiguous_with_its_one_candidate_stored()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("Massive Attack - Teardrops.mkv");

        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Ambiguous.Should().Be(1);
        var candidate = MusicVideoFileRules.Read((await _harness.FilesAsync()).Single().Candidates).Should().ContainSingle().Subject;
        candidate.SongId.Should().Be(song.Id);
        candidate.Score.Should().BeInRange(0.6, 0.95);
    }

    [Fact]
    public async Task Length_is_a_tiebreaker_only_it_never_makes_a_candidate_and_it_never_separates_equal_songs()
    {
        await _harness.InitAsync();
        var fits = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        var other = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        await using (var context = _harness.NewContext())
        {
            await context.Songs.Where(song => song.Id == fits.Id).ExecuteUpdateAsync(set => set.SetProperty(song => song.DurationMs, 284_000));
            await context.Songs.Where(song => song.Id == other.Id).ExecuteUpdateAsync(set => set.SetProperty(song => song.DurationMs, 120_000));
        }

        _harness.Video("Massive Attack - Teardrop.mkv");
        _harness.Video("Massive Attack - Unrelated Words.mkv");

        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Adopted.Should().Be(0, "length only orders songs that score alike; it never separates them enough to adopt");
        identified.Unmatched.Should().Be(1, "a fitting length cannot make a song a candidate");

        var teardrop = (await _harness.FilesAsync()).Single(file => file.Path.EndsWith("Teardrop.mkv", StringComparison.Ordinal));
        var candidates = MusicVideoFileRules.Read(teardrop.Candidates);
        candidates[0].SongId.Should().Be(fits.Id);
        candidates[0].Score.Should().BeApproximately(1.0, 0.0001, "the score is the title alone");
        candidates[1].Score.Should().BeApproximately(1.0, 0.0001);
    }

    [Fact]
    public async Task A_song_that_already_has_a_video_is_never_adopted_again_and_the_file_says_why()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, path: _harness.Temp.Full("videos/downloaded/Massive Attack - Teardrop.mkv"));
        _harness.Video("Massive Attack - Teardrop.mkv");

        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Adopted.Should().Be(0);
        identified.Ambiguous.Should().Be(1);
        var candidate = MusicVideoFileRules.Read((await _harness.FilesAsync()).Single().Candidates).Single();
        candidate.SongId.Should().Be(song.Id);
        candidate.Reason.Should().Be("the song already has a video");
    }

    [Fact]
    public async Task A_second_file_for_a_song_the_first_was_adopted_for_goes_to_ambiguous()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("Massive Attack - Teardrop.mkv");
        _harness.Video("Massive Attack - Teardrop (Official Video).mkv");

        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Adopted.Should().Be(1);
        identified.Ambiguous.Should().Be(1);

        var files = await _harness.FilesAsync();
        files.Count(file => file.State == MusicVideoFileState.Adopted).Should().Be(1);
        var second = files.Single(file => file.State == MusicVideoFileState.Ambiguous);
        MusicVideoFileRules.Read(second.Candidates).Single().Reason.Should().Be("the song already has a video");

        await using var context = _harness.NewContext();
        (await context.MusicVideos.SingleAsync()).SongId.Should().Be(song.Id);
    }

    [Fact]
    public async Task An_unknown_artist_or_a_different_artist_with_the_same_title_is_unmatched()
    {
        await _harness.InitAsync();
        await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("Nobody Known - Some Song.mkv");
        _harness.Video("Somebody Else - Teardrop.mkv");

        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Unmatched.Should().Be(2);
        identified.Adopted.Should().Be(0);
        (await _harness.FilesAsync()).Should().OnlyContain(file => file.State == MusicVideoFileState.Unmatched && file.Candidates == "[]");
    }

    [Fact]
    public async Task A_partial_artist_name_is_not_the_artist()
    {
        await _harness.InitAsync();
        await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("Massive - Teardrop.mkv");

        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Unmatched.Should().Be(1, "'Massive' is not 'Massive Attack' as a whole name");
    }

    [Fact]
    public async Task A_different_version_of_the_title_is_not_the_song()
    {
        await _harness.InitAsync();
        await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("Massive Attack - Teardrop (Live).mkv");

        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Unmatched.Should().Be(1);
    }

    [Fact]
    public async Task A_file_that_could_not_be_read_is_never_adopted_and_is_probed_again_next_time()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("Massive Attack - Teardrop.mkv");
        _probe.Answer = _ => new VideoProbeResult(false, null, "moov atom not found");

        var (_, first) = await _harness.ScanAndIdentifyAsync(_probe);

        first.Adopted.Should().Be(0);
        first.Ambiguous.Should().Be(1);

        _probe.Answer = null;
        await using (var context = _harness.NewContext())
        {
            // The unread file is read again although nothing about it changed.
            await _harness.Scanner(context, _probe).ScanAsync(null, CancellationToken.None);
            (await _harness.Identifier(context).IdentifyPendingAsync(null, CancellationToken.None)).Adopted.Should().Be(1);
        }

        _probe.Probed.Should().HaveCount(2);
        (await _harness.FilesAsync()).Single().SongId.Should().Be(song.Id);
    }

    [Fact]
    public async Task Hidden_and_nas_metadata_folders_and_non_video_files_are_left_out()
    {
        await _harness.InitAsync();
        await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("Massive Attack - Teardrop.mkv");
        _harness.Video("@eaDir/Massive Attack - Teardrop.mkv/SYNOFILE_THUMB.mkv");
        _harness.Video(".hidden/Massive Attack - Teardrop.mkv");
        _harness.Video("._Massive Attack - Teardrop.mkv");
        _harness.Video("#recycle/Massive Attack - Teardrop.mkv");
        _harness.Video("Massive Attack - Teardrop.txt");
        _harness.Video("Massive Attack - Teardrop.jpg");
        _harness.Video("Massive Attack - Teardrop.nfo");
        _harness.Temp.CreateFile("videos/bin/" + RecycleBin.MarkerFileName, "marker");
        _harness.Video("bin/Massive Attack - Teardrop.mkv");

        var (scan, _) = await _harness.ScanAndIdentifyAsync(_probe);

        scan.Seen.Should().Be(1);
        (await _harness.FilesAsync()).Should().ContainSingle().Which.Path.Should().EndWith(Path.Combine("videos", "Massive Attack - Teardrop.mkv"));
    }

    [Fact]
    public async Task A_recycle_bin_inside_the_video_root_is_refused_by_the_root_rules_not_walked()
    {
        await _harness.InitAsync();
        _harness.Video("Massive Attack - Teardrop.mkv");
        _harness.Import.RecycleBinPath = _harness.Temp.Full("videos/trash");
        _harness.Video("trash/old/Massive Attack - Angel.mkv");

        // The root rules refuse it, so the scan says so instead of walking the bin.
        await using var context = _harness.NewContext();
        var scanning = () => _harness.Scanner(context, _probe).ScanAsync(null, CancellationToken.None);

        await scanning.Should().ThrowAsync<MusicVideoScanUnavailableException>().WithMessage("*recycle bin*");
    }

    [Fact]
    public async Task A_file_wondarr_downloaded_is_not_the_scans_business()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        var downloaded = _harness.Video("Massive Attack/Massive Attack - Teardrop.mkv");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, path: downloaded);
        _harness.Video("Other - Song.mkv");

        var (scan, _) = await _harness.ScanAndIdentifyAsync(_probe);

        scan.Seen.Should().Be(1);
        (await _harness.FilesAsync()).Should().ContainSingle().Which.Path.Should().NotBe(downloaded);
    }

    [Fact]
    public async Task Unchanged_files_are_not_probed_again()
    {
        await _harness.InitAsync();
        await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("Massive Attack - Teardrop.mkv");
        _harness.Video("Nobody - Nothing.mkv");

        await _harness.ScanAndIdentifyAsync(_probe);
        _probe.Probed.Should().HaveCount(2);

        var (scan, _) = await _harness.ScanAndIdentifyAsync(_probe);

        scan.Unchanged.Should().Be(2);
        scan.Probed.Should().Be(0);
        _probe.Probed.Should().HaveCount(2, "a second scan costs a stat per file, not a probe");
    }

    [Fact]
    public async Task A_changed_file_is_read_and_identified_again()
    {
        await _harness.InitAsync();
        var path = _harness.Video("Massive Attack - Teardrop.mkv");

        var (_, first) = await _harness.ScanAndIdentifyAsync(_probe);
        first.Unmatched.Should().Be(1, "the library has no such song yet");

        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");

        var (unchanged, stillUnmatched) = await _harness.ScanAndIdentifyAsync(_probe);
        unchanged.Unchanged.Should().Be(1);
        stillUnmatched.Adopted.Should().Be(1, "an unchanged file is not probed again, but it is judged again against the songs as they are now");
        unchanged.Probed.Should().Be(0);

        await File.WriteAllTextAsync(path, "different, longer video bytes");

        var (changed, second) = await _harness.ScanAndIdentifyAsync(_probe);

        changed.Changed.Should().Be(1);
        second.Adopted.Should().Be(1);
        (await _harness.FilesAsync()).Single().SongId.Should().Be(song.Id);
    }

    [Fact]
    public async Task An_adopted_file_that_changes_is_identified_again_and_keeps_one_row_for_its_song()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        var path = _harness.Video("Massive Attack - Teardrop.mkv");
        await _harness.ScanAndIdentifyAsync(_probe);

        await File.WriteAllTextAsync(path, "re-encoded, longer video bytes");
        _probe.Answer = _ => ScanProbe.Video(durationMs: 300_000);

        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Adopted.Should().Be(1);
        await using var context = _harness.NewContext();
        var video = await context.MusicVideos.SingleAsync();
        video.SongId.Should().Be(song.Id);
        video.DurationMs.Should().Be(300_000);
    }

    [Fact]
    public async Task The_users_ignore_outlives_a_changed_file()
    {
        await _harness.InitAsync();
        await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        var path = _harness.Video("Massive Attack - Teardrop.mkv");
        await _harness.ScanAndIdentifyAsync(_probe);

        await using (var context = _harness.NewContext())
        {
            var matches = _harness.Matches(context);
            var id = (await context.MusicVideoFiles.SingleAsync()).Id;
            (await matches.IgnoreAsync(id, ignore: true, CancellationToken.None)).Outcome.Should().Be(MusicVideoMatchOutcome.Ignored);
        }

        await File.WriteAllTextAsync(path, "something else entirely");
        await _harness.ScanAndIdentifyAsync(_probe);

        (await _harness.FilesAsync()).Single().State.Should().Be(MusicVideoFileState.Ignored);
        await using var check = _harness.NewContext();
        (await check.MusicVideos.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task At_most_two_probes_run_at_once()
    {
        await _harness.InitAsync();

        for (var index = 0; index < 8; index++)
        {
            _harness.Video($"Artist {index} - Title {index}.mkv", content: $"bytes {index}");
        }

        _probe.Delay = TimeSpan.FromMilliseconds(40);

        await _harness.ScanAndIdentifyAsync(_probe);

        _probe.Probed.Should().HaveCount(8);
        _probe.MaxConcurrent.Should().BeLessThanOrEqualTo(MusicVideoFileScanner.MaxConcurrentProbes);
    }

    [Fact]
    public async Task A_file_that_is_gone_is_missing_and_is_released_only_after_a_later_scan_and_a_day()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        var gone = _harness.Video("Massive Attack - Teardrop.mkv");
        _harness.Video("Other Artist - Other Song.mkv");
        _harness.Video("Third Artist - Third Song.mkv");
        _harness.Video("Fourth Artist - Fourth Song.mkv");
        await _harness.ScanAndIdentifyAsync(_probe);

        File.Delete(gone);

        // The first scan that does not find it only marks it.
        var (first, _) = await _harness.ScanAndIdentifyAsync(_probe);
        first.Missing.Should().Be(1);
        first.Released.Should().Be(0);
        var row = (await _harness.FilesAsync()).Single(file => file.Path == gone);
        row.State.Should().Be(MusicVideoFileState.Missing);
        row.MissingSince.Should().NotBeNull();
        row.SongId.Should().Be(song.Id, "the song keeps its video through the grace");

        // A later scan, but not a day later: still held.
        _harness.Time.Advance(TimeSpan.FromHours(23));
        var (second, _) = await _harness.ScanAndIdentifyAsync(_probe);
        second.Released.Should().Be(0);
        await using (var held = _harness.NewContext())
        {
            (await held.MusicVideos.CountAsync(video => video.SongId == song.Id)).Should().Be(1);
        }

        // A later scan, more than a day after it was first missing: released.
        _harness.Time.Advance(TimeSpan.FromHours(2));
        var (third, _) = await _harness.ScanAndIdentifyAsync(_probe);
        third.Released.Should().Be(1);
        third.MountProblem.Should().BeFalse();

        await using var context = _harness.NewContext();
        (await context.MusicVideos.CountAsync(video => video.SongId == song.Id)).Should().Be(0);
        (await context.MusicVideoFiles.CountAsync(file => file.Path == gone)).Should().Be(0);
        (await context.MusicVideoFiles.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task A_file_that_comes_back_unchanged_was_never_really_gone()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        var path = _harness.Video("Massive Attack - Teardrop.mkv");
        _harness.Video("Other Artist - Other Song.mkv");
        await _harness.ScanAndIdentifyAsync(_probe);
        var stamp = File.GetLastWriteTimeUtc(path);
        var bytes = await File.ReadAllBytesAsync(path);

        File.Delete(path);
        await _harness.ScanAndIdentifyAsync(_probe);
        (await _harness.FilesAsync()).Single(file => file.Path == path).State.Should().Be(MusicVideoFileState.Missing);

        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, stamp);
        await _harness.ScanAndIdentifyAsync(_probe);

        var row = (await _harness.FilesAsync()).Single(file => file.Path == path);
        row.State.Should().Be(MusicVideoFileState.Adopted);
        row.SongId.Should().Be(song.Id);
        row.MissingSince.Should().BeNull();
    }

    [Fact]
    public async Task When_too_many_files_go_missing_at_once_nothing_is_released_and_the_health_check_says_how_many()
    {
        await _harness.InitAsync();
        var paths = new List<string>();

        for (var index = 0; index < 10; index++)
        {
            await _harness.AddSongAsync($"Title {index}", artist: $"Artist {index}");
            paths.Add(_harness.Video($"Artist {index} - Title {index}.mkv", content: $"bytes {index}"));
        }

        await _harness.ScanAndIdentifyAsync(_probe);

        // Four of ten gone at once: more than the three a library of this size may lose.
        foreach (var path in paths.Take(4))
        {
            File.Delete(path);
        }

        await _harness.ScanAndIdentifyAsync(_probe);
        _harness.Time.Advance(TimeSpan.FromHours(25));
        var (scan, _) = await _harness.ScanAndIdentifyAsync(_probe);

        scan.MountProblem.Should().BeTrue();
        scan.Released.Should().Be(0);
        scan.Missing.Should().Be(4);

        await using var context = _harness.NewContext();
        (await context.MusicVideos.CountAsync()).Should().Be(10, "no song was let go");
        (await context.MusicVideoFiles.CountAsync(file => file.State == MusicVideoFileState.Missing)).Should().Be(4);

        var check = new MusicVideoRootHealthCheck(
            new TestOptionsMonitor<MusicVideoOptions>(_harness.Options),
            new LibraryRoots(new DiskOperations()),
            context,
            new WondarrPaths(_harness.Temp.Full("config")),
            new TestOptionsMonitor<ImportOptions>(_harness.Import));

        var result = await check.CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Warning);
        result.Message.Should().Contain("4 of the 10 videos");
    }

    [Fact]
    public async Task A_folder_that_is_suddenly_empty_is_an_unmounted_share_not_a_deleted_collection()
    {
        await _harness.InitAsync();
        await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        var path = _harness.Video("Massive Attack - Teardrop.mkv");
        await _harness.ScanAndIdentifyAsync(_probe);

        File.Delete(path);

        await using var context = _harness.NewContext();
        var scanning = () => _harness.Scanner(context, _probe).ScanAsync(null, CancellationToken.None);

        await scanning.Should().ThrowAsync<MusicVideoScanUnavailableException>().WithMessage("*mounted*");
        (await _harness.FilesAsync()).Single().State.Should().Be(MusicVideoFileState.Adopted, "nothing was changed");
    }

    [Fact]
    public async Task A_root_without_its_marker_is_not_scanned_at_all()
    {
        await _harness.InitAsync();
        _harness.Video("Massive Attack - Teardrop.mkv");
        File.Delete(Path.Combine(_harness.VideoRoot, LibraryRoots.MarkerFileName));

        await using var context = _harness.NewContext();
        var scanning = () => _harness.Scanner(context, _probe).ScanAsync(null, CancellationToken.None);

        await scanning.Should().ThrowAsync<MusicVideoScanUnavailableException>().WithMessage("*unmounted*");
        _probe.Probed.Should().BeEmpty();
        (await _harness.FilesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_scan_command_reports_a_root_that_is_not_ready_instead_of_failing()
    {
        await _harness.InitAsync();
        File.Delete(Path.Combine(_harness.VideoRoot, LibraryRoots.MarkerFileName));
        await using var context = _harness.NewContext();
        var handler = new MusicVideoScanCommandHandler(
            _harness.Scanner(context, _probe),
            _harness.Identifier(context),
            new TestOptionsMonitor<MusicVideoOptions>(_harness.Options),
            NullLogger<MusicVideoScanCommandHandler>.Instance);

        var message = await handler.ExecuteAsync(
            new Wondarr.Core.Jobs.CommandContext(1, null, Wondarr.Core.Jobs.CommandTrigger.Manual, _ => Task.CompletedTask),
            CancellationToken.None);

        message.Should().StartWith("The video folder was not scanned");
    }

    [Fact]
    public async Task A_song_deleted_after_its_file_was_adopted_leaves_the_file_to_be_identified_again()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("Massive Attack - Teardrop.mkv");
        _harness.Video("Other Artist - Other Song.mkv");
        await _harness.ScanAndIdentifyAsync(_probe);

        await using (var context = _harness.NewContext())
        {
            await context.Songs.Where(candidate => candidate.Id == song.Id).ExecuteDeleteAsync();
        }

        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Unmatched.Should().Be(2, "the song is gone, so its file is matched again and finds nothing (and the other unmatched file is judged again too)");
        (await _harness.FilesAsync()).Should().OnlyContain(file => file.SongId == null && file.State == MusicVideoFileState.Unmatched);
    }

    [Fact]
    public async Task An_edited_nfo_is_read_again_and_identifies_the_file_without_a_new_probe()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        var path = _harness.Video("clip001.mkv", nfo: MusicVideoScanTestKit.Nfo("Nothing", "Nobody"));
        var (_, first) = await _harness.ScanAndIdentifyAsync(_probe);
        first.Unmatched.Should().Be(1);

        var nfo = Path.ChangeExtension(path, ".nfo");
        await File.WriteAllTextAsync(nfo, MusicVideoScanTestKit.Nfo("Teardrop", "Massive Attack"));
        File.SetLastWriteTimeUtc(nfo, DateTime.UtcNow.AddMinutes(5));

        var (_, second) = await _harness.ScanAndIdentifyAsync(_probe);

        _probe.Probed.Should().HaveCount(1, "the video itself was not probed again");
        second.Adopted.Should().Be(1);
        (await _harness.FilesAsync()).Single().SongId.Should().Be(song.Id);
    }

    [Fact]
    public async Task The_length_never_lifts_a_title_over_the_adopt_line()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Heartbreaker Song", artist: "Massive Attack");
        await using (var context = _harness.NewContext())
        {
            await context.Songs.Where(candidate => candidate.Id == song.Id).ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.DurationMs, 284_000));
        }

        _harness.Video("Massive Attack - Heartbreaker Songs.mkv");

        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Adopted.Should().Be(0, "0.94 of title is not 0.95, whatever the length");
        identified.Ambiguous.Should().Be(1);
    }

    [Fact]
    public async Task A_lyric_video_or_visualiser_is_never_adopted()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("Massive Attack - Teardrop (Lyric Video).mkv");

        var (_, identified) = await _harness.ScanAndIdentifyAsync(_probe);

        identified.Adopted.Should().Be(0);
        var candidate = MusicVideoFileRules.Read((await _harness.FilesAsync()).Single().Candidates).Single();
        candidate.SongId.Should().Be(song.Id);
        candidate.Reason.Should().Be("lyric video or visualiser");
    }

    [Fact]
    public async Task A_symlinked_file_is_not_followed()
    {
        await _harness.InitAsync();
        var target = _harness.Temp.CreateFile("elsewhere/Real.mkv");
        _harness.Video("Massive Attack - Teardrop.mkv");

        try
        {
            File.CreateSymbolicLink(Path.Combine(_harness.VideoRoot, "Linked - Video.mkv"), target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var (scan, _) = await _harness.ScanAndIdentifyAsync(_probe);

        scan.Seen.Should().Be(1);
    }

    [Fact]
    public async Task A_changed_root_lets_the_old_roots_rows_go_whatever_the_mount_guard_says()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        var oldVideo = _harness.Video("Massive Attack - Teardrop.mkv");
        _harness.Video("Other Artist - Other.mkv");
        await _harness.ScanAndIdentifyAsync(_probe);

        var newRoot = _harness.Temp.Full("videos2");
        new LibraryRoots(new DiskOperations()).Mark(newRoot);
        _harness.Temp.CreateFile("videos2/Third Artist - Third.mkv");
        _harness.Options.RootPath = newRoot;

        var (scan, _) = await _harness.ScanAndIdentifyAsync(_probe);

        scan.Released.Should().Be(2, "every row of the old root goes, which a mount guard would have refused");
        scan.MountProblem.Should().BeFalse();
        (await _harness.FilesAsync()).Should().ContainSingle().Which.Path.Should().Be(Path.Combine(newRoot, "Third Artist - Third.mkv"));

        await using var context = _harness.NewContext();
        (await context.MusicVideos.CountAsync(video => video.SongId == song.Id)).Should().Be(0);
        File.Exists(oldVideo).Should().BeTrue("no file is ever touched");
    }

    [Fact]
    public async Task A_new_empty_root_is_fine_and_the_old_rows_go()
    {
        await _harness.InitAsync();
        await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("Massive Attack - Teardrop.mkv");
        await _harness.ScanAndIdentifyAsync(_probe);

        var newRoot = _harness.Temp.Full("videos2");
        new LibraryRoots(new DiskOperations()).Mark(newRoot);
        _harness.Options.RootPath = newRoot;

        var (scan, _) = await _harness.ScanAndIdentifyAsync(_probe);

        scan.Released.Should().Be(1);
        (await _harness.FilesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_root_changed_while_the_scan_runs_discards_what_it_read_and_the_command_scans_again()
    {
        await _harness.InitAsync();
        await _harness.AddSongAsync("Teardrop", artist: "Massive Attack");
        _harness.Video("Massive Attack - Teardrop.mkv");
        var newRoot = _harness.Temp.Full("videos2");
        new LibraryRoots(new DiskOperations()).Mark(newRoot);
        _harness.Temp.CreateFile("videos2/Massive Attack - Teardrop.mkv");
        var changed = false;
        _probe.Answer = _ =>
        {
            if (!changed)
            {
                changed = true;
                _harness.Options.RootPath = newRoot;
            }

            return ScanProbe.Video();
        };

        await using (var context = _harness.NewContext())
        {
            var scanning = () => _harness.Scanner(context, _probe).ScanAsync(null, CancellationToken.None);

            await scanning.Should().ThrowAsync<MusicVideoScanRootChangedException>();
        }

        (await _harness.FilesAsync()).Should().BeEmpty("nothing the old root's scan read was saved");

        // Run as the command does, with the root changing during the first of its scans.
        changed = false;
        _harness.Options.RootPath = _harness.VideoRoot;
        await using var second = _harness.NewContext();
        var handler = new MusicVideoScanCommandHandler(
            _harness.Scanner(second, _probe),
            _harness.Identifier(second),
            new TestOptionsMonitor<MusicVideoOptions>(_harness.Options),
            NullLogger<MusicVideoScanCommandHandler>.Instance);

        var message = await handler.ExecuteAsync(
            new Wondarr.Core.Jobs.CommandContext(1, null, Wondarr.Core.Jobs.CommandTrigger.Manual, _ => Task.CompletedTask),
            CancellationToken.None);

        message.Should().Contain("1 adopted");
        (await _harness.FilesAsync()).Should().ContainSingle().Which.Path.Should().StartWith(newRoot);
    }

    [Fact]
    public void The_mount_guard_uses_the_reference_scans_thresholds()
    {
        ReferenceMissingRules.IsMountProblem(650, 65).Should().BeFalse();
        ReferenceMissingRules.IsMountProblem(650, 66).Should().BeTrue();
        ReferenceMissingRules.IsMountProblem(5000, 200).Should().BeFalse();
        ReferenceMissingRules.IsMountProblem(5000, 201).Should().BeTrue();
    }
}
