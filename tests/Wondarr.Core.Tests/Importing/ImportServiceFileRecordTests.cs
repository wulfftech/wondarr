using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Metadata.AcoustId;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using Wondarr.Core.Verification;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// "Once a file has moved, the database records where it is" (code review 2026-10-10, M-12): the
/// import's behaviour around a placement that moved the file and failed, a shutdown between the
/// placement and the record, a YouTube transcode that is later deferred or rejected, the sidecars of
/// an upgrade, a container the tag writer cannot handle, and a rejected AcoustID key.
/// </summary>
public class ImportServiceFileRecordTests
{
    private static readonly VerificationResult Failed =
        new(VerificationOutcome.Failed, "Fingerprint matches a different recording", FakeDownloadVerifier.Flac(), 36, null, null, "someone-else", null, false);

    private static VerificationResult Deferred(bool keyRejected = false) =>
        new(VerificationOutcome.Deferred, "AcoustID is unavailable", null, null, null, null, null, null, false, keyRejected);

    [Fact]
    public async Task Records_a_file_the_placer_reports_as_failed_but_that_did_land()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Placer.LandedWithError = "chmod failed: operation not permitted";

        var seed = await host.SeedAsync();

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported);

        var target = host.LibraryRoot + "/" + ImportTestHost.DaftPunkRelativePath + ".flac";
        File.Exists(target).Should().BeTrue();

        (await host.Context.SongFiles.SingleAsync()).Path.Should().Be(target, "the row says where the file is");
        (await host.Context.QueueItems.SingleAsync()).State.Should().Be(QueueItemState.Imported);
        host.Search.Grabs.Should().BeEmpty("no next candidate is grabbed for a file that is already placed");
    }

    [Fact]
    public async Task Records_the_file_when_the_import_is_cancelled_after_the_placement()
    {
        await using var host = await ImportTestHost.CreateAsync();
        using var shutdown = new CancellationTokenSource();

        // The container stops while the file is being moved into the library.
        host.Placer.OnPlaceAsync = _ =>
        {
            shutdown.Cancel();

            return Task.CompletedTask;
        };

        var seed = await host.SeedAsync();

        try
        {
            await host.Import.ImportAsync(seed.QueueItemId, shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            // Telling the world about the import may notice the shutdown; the record may not.
        }

        var file = await host.Context.SongFiles.SingleAsync();
        file.Path.Should().Be(host.LibraryRoot + "/" + ImportTestHost.DaftPunkRelativePath + ".flac");
        (await host.Context.QueueItems.AsNoTracking().SingleAsync()).State.Should().Be(QueueItemState.Imported);
    }

    [Fact]
    public async Task A_deferred_youtube_import_comes_back_to_the_transcoded_file()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Deferred();

        var seed = await host.SeedAsync(options =>
        {
            options.SourceType = SourceTypes.YouTube;
            options.DownloadName = "dQw4w9WgXcQ.opus";
            options.RemotePath = "dQw4w9WgXcQ";
        });

        var transcoded = Path.Combine(seed.DownloadDirectory, "dQw4w9WgXcQ.m4a");

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Deferred);

        var item = await host.Context.QueueItems.AsNoTracking().SingleAsync();
        item.State.Should().Be(QueueItemState.Completed);
        item.DownloadPath.Should().Be(transcoded, "the Opus original is gone, and the item must say so");
        File.Exists(transcoded).Should().BeTrue();

        // The next pass finds the file, transcodes nothing again, and imports it.
        host.Verifier.Result = FakeDownloadVerifier.Passed();

        (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);

        host.Transcoder.Requests.Should().ContainSingle("the second pass skips the transcode");
        host.Placer.Requests.Should().ContainSingle().Which.SourcePath.Should().Be(transcoded);
    }

    [Fact]
    public async Task A_rejected_youtube_import_deletes_the_transcoded_file()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Failed;

        var seed = await host.SeedAsync(options =>
        {
            options.SourceType = SourceTypes.YouTube;
            options.DownloadName = "dQw4w9WgXcQ.opus";
            options.RemotePath = "dQw4w9WgXcQ";
        });

        var transcoded = Path.Combine(seed.DownloadDirectory, "dQw4w9WgXcQ.m4a");

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Rejected);

        File.Exists(transcoded).Should().BeFalse("the rejection cleans up the file that is really there");
        File.Exists(seed.DownloadPath).Should().BeFalse();
        Directory.Exists(seed.DownloadDirectory).Should().BeFalse("the per-grab folder goes with it");
    }

    [Fact]
    public async Task A_failed_tagging_does_not_save_the_organizers_album_alignment()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.TagWriter.Success = false;
        host.TagWriter.Error = "The file is not a writable container";

        // A file of the same album is already in the library, under another album title: the
        // organizer re-files the incoming song under that title before it tags.
        var first = await host.SeedAsync(options =>
        {
            options.AlbumKey = "shared";
            options.AlbumTitle = "Random Access Memories (Deluxe)";
            options.CurrentFilePath = host.LibraryRoot + "/Daft Punk/Random Access Memories/01 - Give Life Back to Music.flac";
            options.Title = "Give Life Back to Music";
        });

        var second = await host.SeedAsync(options =>
        {
            options.AlbumKey = "shared";
            options.AlbumTitle = "Random Access Memories";
            options.MbArtistId = null;
        });

        (await host.Import.ImportAsync(second.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Failed);

        first.SongId.Should().NotBe(second.SongId);

        var album = await host.Context.AlbumContexts.AsNoTracking().SingleAsync(entry => entry.SongId == second.SongId);
        album.AlbumTitle.Should().Be("Random Access Memories", "a failed import re-files nothing");
    }

    [Fact]
    public async Task Moves_the_old_files_sidecars_when_an_upgrade_renames_the_file()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var folder = host.LibraryRoot + "/Daft Punk/Random Access Memories (2013)";
        Directory.CreateDirectory(folder);

        var held = folder + "/08 - Get Lucky (old).mp3";
        await File.WriteAllBytesAsync(held, new byte[8]);
        await File.WriteAllTextAsync(folder + "/08 - Get Lucky (old).lrc", "[00:01.00]hello");

        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = held;
            options.CurrentFileQualityId = 29;
        });

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Upgraded);

        File.Exists(folder + "/08 - Get Lucky.lrc").Should().BeTrue("the lyrics follow the upgraded file");
        File.Exists(folder + "/08 - Get Lucky (old).lrc").Should().BeFalse();
    }

    [Fact]
    public async Task Keeps_the_beside_track_video_under_the_new_name_when_an_upgrade_renames_the_file()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var folder = host.LibraryRoot + "/Daft Punk/Random Access Memories (2013)";
        Directory.CreateDirectory(folder);

        var held = folder + "/08 - Get Lucky (old).mp3";
        await File.WriteAllBytesAsync(held, new byte[8]);

        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = held;
            options.CurrentFileQualityId = 29;
        });

        await SeedVideoAsync(host, seed.SongId, folder + "/08 - Get Lucky (old)", "-clip");

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Upgraded);

        foreach (var suffix in new[] { ".mkv", ".nfo", "-thumb.jpg" })
        {
            File.Exists(folder + "/08 - Get Lucky-clip" + suffix).Should().BeTrue(suffix);
            File.Exists(folder + "/08 - Get Lucky (old)-clip" + suffix).Should().BeFalse(suffix);
        }

        host.Context.ChangeTracker.Clear();
        (await host.Context.MusicVideos.AsNoTracking().SingleAsync()).Path.Should().Be(folder + "/08 - Get Lucky-clip.mkv");
    }

    [Fact]
    public async Task A_video_read_that_fails_never_stops_the_lyrics_or_the_audios_record()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var folder = host.LibraryRoot + "/Daft Punk/Random Access Memories (2013)";
        Directory.CreateDirectory(folder);

        var held = folder + "/08 - Get Lucky (old).mp3";
        await File.WriteAllBytesAsync(held, new byte[8]);
        await File.WriteAllTextAsync(folder + "/08 - Get Lucky (old).lrc", "[00:01.00]hello");

        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = held;
            options.CurrentFileQualityId = 29;
        });

        await SeedVideoAsync(host, seed.SongId, folder + "/08 - Get Lucky (old)", "-video");
        await host.Context.Database.ExecuteSqlRawAsync("DROP TABLE music_video");

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Upgraded);

        File.Exists(folder + "/08 - Get Lucky.lrc").Should().BeTrue("the lyrics move whatever the video read does");
        host.Context.ChangeTracker.Clear();
        (await host.Context.SongFiles.AsNoTracking().SingleAsync()).Path.Should().NotContain("(old)");
    }

    [Fact]
    public async Task Moves_a_video_left_in_another_album_folder_of_the_library_beside_the_songs_first_file()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.MusicVideoOptions.BesideTrackSuffix = "-clip";

        var elsewhere = host.LibraryRoot + "/Daft Punk/Some Other Album";
        var folder = host.LibraryRoot + "/Daft Punk/Random Access Memories (2013)";
        Directory.CreateDirectory(elsewhere);

        var seed = await host.SeedAsync(options => { });
        await SeedVideoAsync(host, seed.SongId, elsewhere + "/07 - Old Name", "-video");

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);

        File.Exists(elsewhere + "/07 - Old Name-video.mkv").Should().BeFalse();
        File.Exists(folder + "/08 - Get Lucky-clip.mkv").Should().BeTrue();
        host.Context.ChangeTracker.Clear();
        (await host.Context.MusicVideos.AsNoTracking().SingleAsync()).Path.Should().Be(folder + "/08 - Get Lucky-clip.mkv");
    }

    [Fact]
    public async Task Leaves_a_video_outside_every_library_and_the_video_root_alone()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.MusicVideoOptions.Enabled = true;
        host.MusicVideoOptions.RootPath = host.LibraryRoot + "-videos";

        var stray = host.LibraryRoot + "-stray/Daft Punk";
        Directory.CreateDirectory(stray);

        var seed = await host.SeedAsync(options => { });
        await SeedVideoAsync(host, seed.SongId, stray + "/Daft Punk - Get Lucky", string.Empty);

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);

        File.Exists(stray + "/Daft Punk - Get Lucky.mkv").Should().BeTrue();
        host.Context.ChangeTracker.Clear();
        (await host.Context.MusicVideos.AsNoTracking().SingleAsync()).Path.Should().Be(stray + "/Daft Punk - Get Lucky.mkv");
    }

    [Fact]
    public async Task Does_not_replace_a_file_that_holds_the_videos_new_name_when_an_upgrade_renames_the_file()
    {
        await using var host = await ImportTestHost.CreateAsync();

        var folder = host.LibraryRoot + "/Daft Punk/Random Access Memories (2013)";
        Directory.CreateDirectory(folder);

        var held = folder + "/08 - Get Lucky (old).mp3";
        await File.WriteAllBytesAsync(held, new byte[8]);
        await File.WriteAllTextAsync(folder + "/08 - Get Lucky-video.mkv", "somebody else's video");

        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = held;
            options.CurrentFileQualityId = 29;
        });

        await SeedVideoAsync(host, seed.SongId, folder + "/08 - Get Lucky (old)", "-video");

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Upgraded);

        File.ReadAllText(folder + "/08 - Get Lucky-video.mkv").Should().Be("somebody else's video");
        File.Exists(folder + "/08 - Get Lucky (old)-video.mkv").Should().BeTrue();

        host.Context.ChangeTracker.Clear();
        (await host.Context.MusicVideos.AsNoTracking().SingleAsync()).Path.Should().Be(folder + "/08 - Get Lucky (old)-video.mkv");
    }

    [Fact]
    public async Task Leaves_a_video_found_in_the_folder_where_it_is_when_the_song_gets_a_file_beside_track()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.MusicVideoOptions.Layout = MusicVideoOptions.BesideTrackLayout;
        host.MusicVideoOptions.BesideTrackSuffix = "-clip";

        var folder = host.LibraryRoot + "/Daft Punk/Random Access Memories (2013)";
        Directory.CreateDirectory(folder);
        var seed = await host.SeedAsync(options => { });
        await SeedVideoAsync(host, seed.SongId, folder + "/07 - Old Name", "-video", MusicVideoFoundBy.Existing);

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);

        foreach (var suffix in new[] { ".mkv", ".nfo", "-thumb.jpg" })
        {
            File.Exists(folder + "/07 - Old Name-video" + suffix).Should().BeTrue(suffix);
            File.Exists(folder + "/08 - Get Lucky-clip" + suffix).Should().BeFalse(suffix);
        }

        host.Context.ChangeTracker.Clear();
        (await host.Context.MusicVideos.AsNoTracking().SingleAsync()).Path.Should().Be(folder + "/07 - Old Name-video.mkv");
    }

    [Fact]
    public async Task Moves_a_video_that_remove_file_left_behind_beside_the_songs_next_file()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.MusicVideoOptions.Layout = MusicVideoOptions.BesideTrackLayout;
        host.MusicVideoOptions.BesideTrackSuffix = "-clip";

        var folder = host.LibraryRoot + "/Daft Punk/Random Access Memories (2013)";
        Directory.CreateDirectory(folder);

        // The song has no file: Remove file recycled it and left the video where it was.
        var seed = await host.SeedAsync(options => { });
        await SeedVideoAsync(host, seed.SongId, folder + "/07 - Old Name", "-video");

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);

        foreach (var suffix in new[] { ".mkv", ".nfo", "-thumb.jpg" })
        {
            File.Exists(folder + "/08 - Get Lucky-clip" + suffix).Should().BeTrue(suffix);
            File.Exists(folder + "/07 - Old Name-video" + suffix).Should().BeFalse(suffix);
        }

        host.Context.ChangeTracker.Clear();
        (await host.Context.MusicVideos.AsNoTracking().SingleAsync()).Path.Should().Be(folder + "/08 - Get Lucky-clip.mkv");
    }

    [Theory]
    [InlineData(MusicVideoOptions.FolderLayout, true)]
    [InlineData(MusicVideoOptions.BesideTrackLayout, false)]
    public async Task Leaves_a_video_that_was_never_beside_a_track_where_it_is(string layout, bool insideLibrary)
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.MusicVideoOptions.Layout = layout;

        var libraryFolder = host.LibraryRoot + "/Daft Punk/Random Access Memories (2013)";
        var videoRoot = host.LibraryRoot + "-videos/Daft Punk";
        var folder = insideLibrary ? libraryFolder : videoRoot;
        Directory.CreateDirectory(folder);

        var seed = await host.SeedAsync(options => { });
        await SeedVideoAsync(host, seed.SongId, folder + "/Daft Punk - Get Lucky", string.Empty);

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);

        File.Exists(folder + "/Daft Punk - Get Lucky.mkv").Should().BeTrue();

        host.Context.ChangeTracker.Clear();
        (await host.Context.MusicVideos.AsNoTracking().SingleAsync()).Path.Should().Be(folder + "/Daft Punk - Get Lucky.mkv");
    }

    [Fact]
    public async Task Moves_a_video_filed_in_the_video_root_beside_the_songs_first_library_file()
    {
        await using var host = await ImportTestHost.CreateAsync();
        var videoRoot = host.LibraryRoot + "-videos";
        host.MusicVideoOptions.Enabled = true;
        host.MusicVideoOptions.Layout = MusicVideoOptions.BesideTrackLayout;
        host.MusicVideoOptions.RootPath = videoRoot;
        host.MusicVideoOptions.BesideTrackSuffix = "-clip";
        Directory.CreateDirectory(videoRoot + "/Daft Punk");

        var seed = await host.SeedAsync(options => { });
        await SeedVideoAsync(host, seed.SongId, videoRoot + "/Daft Punk/Daft Punk - Get Lucky", string.Empty);

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);

        var folder = host.LibraryRoot + "/Daft Punk/Random Access Memories (2013)";

        foreach (var suffix in new[] { ".mkv", ".nfo", "-thumb.jpg" })
        {
            File.Exists(folder + "/08 - Get Lucky-clip" + suffix).Should().BeTrue(suffix);
            File.Exists(videoRoot + "/Daft Punk/Daft Punk - Get Lucky" + suffix).Should().BeFalse(suffix);
        }

        host.Context.ChangeTracker.Clear();
        (await host.Context.MusicVideos.AsNoTracking().SingleAsync()).Path.Should().Be(folder + "/08 - Get Lucky-clip.mkv");
    }

    [Fact]
    public async Task Reports_and_keeps_a_video_whose_new_name_is_taken_when_the_song_gets_its_first_file()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.MusicVideoOptions.Layout = MusicVideoOptions.BesideTrackLayout;
        host.MusicVideoOptions.BesideTrackSuffix = "-clip";

        var folder = host.LibraryRoot + "/Daft Punk/Random Access Memories (2013)";
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(folder + "/08 - Get Lucky-clip.mkv", "somebody else's video");

        var seed = await host.SeedAsync(options => { });
        await SeedVideoAsync(host, seed.SongId, folder + "/07 - Old Name", "-video");

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);

        File.ReadAllText(folder + "/08 - Get Lucky-clip.mkv").Should().Be("somebody else's video");
        File.Exists(folder + "/07 - Old Name-video.mkv").Should().BeTrue();

        host.Context.ChangeTracker.Clear();
        (await host.Context.MusicVideos.AsNoTracking().SingleAsync()).Path.Should().Be(folder + "/07 - Old Name-video.mkv");
    }

    /// <summary>A song's video group at <paramref name="stem"/> plus <paramref name="suffix"/>, with its row.</summary>
    private static async Task SeedVideoAsync(ImportTestHost host, long songId, string stem, string suffix, MusicVideoFoundBy foundBy = MusicVideoFoundBy.Manual)
    {
        foreach (var extension in new[] { ".mkv", ".nfo", "-thumb.jpg" })
        {
            await File.WriteAllTextAsync(stem + suffix + extension, "video group " + extension);
        }

        host.Context.MusicVideos.Add(new MusicVideo
        {
            SongId = songId,
            Status = MusicVideoStatus.Present,
            YouTubeVideoId = foundBy == MusicVideoFoundBy.Existing ? string.Empty : "yt" + songId,
            FoundBy = foundBy,
            Path = stem + suffix + ".mkv",
        });

        await host.Context.SaveChangesAsync();
        host.Context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Rejects_a_container_the_tag_writer_cannot_handle()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Probe.Result = new MediaProbeResult(true, Ape(), null);

        var seed = await host.SeedAsync(options => options.DownloadName = "08 - Get Lucky.ape");

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Rejected);

        var item = await host.Context.QueueItems.SingleAsync();
        item.Message.Should().Contain("Cannot tag .ape files");
        host.TagWriter.Writes.Should().BeEmpty();
        host.Verifier.Requests.Should().BeEmpty();
        host.Spectral.Requests.Should().BeEmpty("a file that cannot be tagged is refused before it is analysed");
    }

    [Fact]
    public async Task A_probe_that_times_out_defers_the_import_and_keeps_the_download()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Probe.Result = new MediaProbeResult(false, null, "ffprobe timed out", TimedOut: true);

        var seed = await host.SeedAsync();

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Deferred);

        var item = await host.Context.QueueItems.AsNoTracking().SingleAsync();
        item.State.Should().Be(QueueItemState.Completed);
        item.NextCheckAt.Should().NotBeNull();
        item.DownloadPath.Should().Be(seed.DownloadPath);
        File.Exists(seed.DownloadPath).Should().BeTrue();

        host.Transcoder.Requests.Should().BeEmpty();
        host.Verifier.Requests.Should().BeEmpty();
        (await host.Context.Blocklist.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Points_the_song_row_at_the_recycle_bin_when_the_old_file_could_not_be_put_back()
    {
        await using var host = await ImportTestHost.CreateAsync();
        var recycled = host.LibraryRoot + "/../recycle/Daft Punk/old.mp3";
        host.Placer.StrandedRecycledPath = recycled;

        var held = host.LibraryRoot + "/Daft Punk/Random Access Memories/08 - Get Lucky.mp3";
        var seed = await host.SeedAsync(options =>
        {
            options.CurrentFilePath = held;
            options.CurrentFileQualityId = 29;
        });

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Failed);

        var file = await host.Context.SongFiles.AsNoTracking().SingleAsync();
        file.Path.Should().Be(recycled, "the old path has nothing at it any more");
    }

    [Fact]
    public async Task The_new_download_path_is_saved_even_when_the_import_then_fails()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.OnVerifyAsync = _ => throw new InvalidOperationException("something unforeseen");

        var seed = await host.SeedAsync(options =>
        {
            options.SourceType = SourceTypes.YouTube;
            options.DownloadName = "dQw4w9WgXcQ.opus";
            options.RemotePath = "dQw4w9WgXcQ";
        });

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Failed);

        // The failure path reloads the item: only a row saved right after the transcode survives it.
        var item = await host.Context.QueueItems.AsNoTracking().SingleAsync();
        item.DownloadPath.Should().Be(Path.Combine(seed.DownloadDirectory, "dQw4w9WgXcQ.m4a"));
    }

    [Fact]
    public async Task The_ogg_rename_is_saved_even_when_the_import_is_cancelled_during_verification()
    {
        await using var host = await ImportTestHost.CreateAsync();
        await host.SetLibraryOutputPolicyAsync("""{"version":2,"youtube":{"codec":"keep","opusContainer":"ogg"}}""");
        host.Verifier.OnVerifyAsync = _ => throw new OperationCanceledException();

        var seed = await host.SeedAsync(options =>
        {
            options.SourceType = SourceTypes.YouTube;
            options.DownloadName = "dQw4w9WgXcQ.opus";
            options.RemotePath = "dQw4w9WgXcQ";
        });

        var import = () => host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);
        await import.Should().ThrowAsync<OperationCanceledException>();

        // The .opus is gone from disk; a row that still names it would strand the .ogg on restart.
        var item = await host.Context.QueueItems.AsNoTracking().SingleAsync();
        item.DownloadPath.Should().Be(Path.Combine(seed.DownloadDirectory, "dQw4w9WgXcQ.ogg"));
    }

    [Fact]
    public async Task A_decided_item_forgets_its_key_deferral_count()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Deferred(keyRejected: true);

        var seed = await host.SeedAsync();

        (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Deferred);
        host.KeyState.TrackedItemCount.Should().Be(1);

        host.Verifier.Result = FakeDownloadVerifier.Passed();

        (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);
        host.KeyState.TrackedItemCount.Should().Be(0);
    }

    [Fact]
    public async Task Imports_an_ape_when_the_lossless_rule_converts_it()
    {
        await using var host = await ImportTestHost.CreateAsync();
        await host.SetLibraryOutputPolicyAsync("""{"version":2,"lossless":{"codec":"flac"}}""");
        host.Probe.Result = new MediaProbeResult(true, Ape(), null);

        // The seeded profiles do not allow APE; a custom profile that does is what this is about.
        await using (var context = host.Database.CreateContext(host.Time))
        {
            var profile = await context.QualityProfiles.SingleAsync(entry => entry.Id == SeedData.LosslessProfileId);
            profile.Items = [.. profile.Items.Select(item => item.QualityIds.Contains(38) ? item with { Allowed = true } : item)];
            await context.SaveChangesAsync();
        }

        var seed = await host.SeedAsync(options =>
        {
            options.DownloadName = "08 - Get Lucky.ape";
            options.QualityProfileId = SeedData.LosslessProfileId;
        });

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Imported, (await host.Context.QueueItems.AsNoTracking().SingleAsync()).Message);
        host.Transcoder.Requests.Should().ContainSingle();
        host.Placer.Requests.Should().ContainSingle().Which.Extension.Should().Be("flac");
    }

    [Fact]
    public async Task A_rejected_acoustid_key_defers_a_bounded_number_of_times_and_then_fails_without_blocklisting()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = Deferred(keyRejected: true);

        var seed = await host.SeedAsync();

        for (var attempt = 1; attempt <= AcoustIdKeyState.MaxKeyDeferrals; attempt++)
        {
            (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None))
                .Should().Be(ImportOutcome.Deferred, $"deferral {attempt} is within the bound");
        }

        (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Failed);

        var item = await host.Context.QueueItems.AsNoTracking().SingleAsync();
        item.State.Should().Be(QueueItemState.Failed);
        item.Message.Should().Contain("AcoustID").And.Contain("client key").And.Contain("Metadata services");

        (await host.Context.Blocklist.CountAsync()).Should().Be(0, "the file was not at fault");
        host.Search.Grabs.Should().BeEmpty();
    }

    private static MediaInfo Ape() => new("ape", "ape", 900, 44_100, 16, 2, 369_000, true, 30_000_000);
}
