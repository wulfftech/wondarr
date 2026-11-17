using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Jobs;
using Wondarr.Core.Metadata.CoverArt;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Tests.Songs;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>
/// The two gaps M-59 left: a deleted song's video files are recycled before the cascade removes the row, and a video
/// whose download is in flight cannot be removed.
/// </summary>
public sealed class MusicVideoSongDeleteTests : IDisposable
{
    private readonly MusicVideoHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Theory]
    [InlineData(MusicVideoStatus.Queued)]
    [InlineData(MusicVideoStatus.Downloading)]
    public async Task Removing_a_video_whose_download_is_in_flight_is_refused_and_changes_nothing(MusicVideoStatus status)
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, status);
        await _harness.AddActiveDownloadAsync(song.Id);

        await using (var context = _harness.NewContext())
        {
            var act = () => _harness.Videos(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

            (await act.Should().ThrowAsync<MusicVideoDownloadInProgressException>()).Which.Message.Should().Be("A download is in progress");
        }

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.CountAsync()).Should().Be(1);
        (await fresh.MusicVideoBlocklist.CountAsync()).Should().Be(0);
        (await fresh.History.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(MusicVideoStatus.Queued)]
    [InlineData(MusicVideoStatus.Downloading)]
    public async Task A_row_left_downloading_by_a_crash_with_no_command_behind_it_can_be_removed(MusicVideoStatus status)
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, status);

        await using (var context = _harness.NewContext())
        {
            (await _harness.Videos(context).DeleteAsync(song.Id, blocklist: false, CancellationToken.None)).Should().NotBeNull();
        }

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_filed_video_cannot_be_removed_while_its_replacement_downloads()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        var video = _harness.Temp.CreateFile("videos/Massive Attack/Massive Attack - Teardrop.mkv", "video");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, path: video);
        await _harness.AddActiveDownloadAsync(song.Id, "CCCCCCCCCCC");

        await using var context = _harness.NewContext();
        var act = () => _harness.Videos(context).DeleteAsync(song.Id, blocklist: true, CancellationToken.None);

        await act.Should().ThrowAsync<MusicVideoDownloadInProgressException>();
        File.Exists(video).Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_a_song_recycles_its_video_and_sidecars_before_the_row_cascades()
    {
        await _harness.InitAsync();
        var (song, files) = await SeedVideoAsync();

        await using (var context = _harness.NewContext())
        {
            (await SongService(context).DeleteAsync(song.Id, CancellationToken.None)).Should().BeTrue();
        }

        files.Should().OnlyContain(path => !File.Exists(path), "the video and its sidecars left the video root");
        RecycledFiles().Should().HaveCount(3);
        File.Exists(_harness.Temp.Full("videos/Massive Attack/other.mkv")).Should().BeTrue("a neighbour is not a sidecar");

        await using var fresh = _harness.NewContext();
        (await fresh.Songs.CountAsync()).Should().Be(0);
        (await fresh.MusicVideos.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_songs_in_bulk_recycles_each_ones_video()
    {
        await _harness.InitAsync();
        var (first, firstFiles) = await SeedVideoAsync("Teardrop");
        var (second, secondFiles) = await SeedVideoAsync("Angel");
        var untouched = await _harness.AddSongAsync("Safe");

        await using (var context = _harness.NewContext())
        {
            var editor = new SongEditorService(context, SongService(context), Substitute.For<ICommandQueue>(), _harness.Videos(context));

            (await editor.DeleteAsync([first.Id, second.Id], CancellationToken.None)).Should().Be(2);
        }

        firstFiles.Concat(secondFiles).Should().OnlyContain(path => !File.Exists(path));
        RecycledFiles().Should().HaveCount(6);

        await using var fresh = _harness.NewContext();
        (await fresh.Songs.Select(song => song.Id).ToListAsync()).Should().Equal(untouched.Id);
    }

    [Fact]
    public async Task A_video_inside_a_reference_library_is_left_where_it_is_and_the_song_is_still_deleted()
    {
        await _harness.InitAsync();
        var (song, files) = await SeedVideoAsync();
        await using (var seed = _harness.NewContext())
        {
            seed.ReferenceLibraries.Add(new ReferenceLibrary { Name = "Mine", RootPath = _harness.Temp.Full("videos/Massive Attack") });
            await seed.SaveChangesAsync();
        }

        await using (var context = _harness.NewContext())
        {
            (await SongService(context).DeleteAsync(song.Id, CancellationToken.None)).Should().BeTrue();
        }

        files.Should().OnlyContain(path => File.Exists(path), "Wondarr never moves anything inside a reference library");
        RecycledFiles().Should().BeEmpty();
        await using var fresh = _harness.NewContext();
        (await fresh.Songs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_video_path_outside_the_folders_wondarr_writes_to_is_left_alone()
    {
        await _harness.InitAsync();
        var elsewhere = _harness.Temp.CreateFile("elsewhere/Teardrop.mkv", "not ours");
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, path: elsewhere);

        await using (var context = _harness.NewContext())
        {
            (await SongService(context).DeleteAsync(song.Id, CancellationToken.None)).Should().BeTrue();
        }

        File.Exists(elsewhere).Should().BeTrue();
        RecycledFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task A_song_with_no_video_deletes_as_before()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");

        await using var context = _harness.NewContext();

        (await SongService(context).DeleteAsync(song.Id, CancellationToken.None)).Should().BeTrue();
    }

    private async Task<(Song Song, List<string> Files)> SeedVideoAsync(string title = "Teardrop")
    {
        var video = _harness.Temp.CreateFile($"videos/Massive Attack/{title}.mkv", "video");
        var files = new List<string>
        {
            video,
            _harness.Temp.CreateFile($"videos/Massive Attack/{title}.nfo", "<musicvideo/>"),
            _harness.Temp.CreateFile($"videos/Massive Attack/{title}-thumb.jpg", "jpeg"),
        };
        _harness.Temp.CreateFile("videos/Massive Attack/other.mkv", "someone else's video");

        var song = await _harness.AddSongAsync(title);
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, path: video);

        return (song, files);
    }

    private SongService SongService(WondarrDbContext context) =>
        new(
            context,
            Substitute.For<IIdentityResolver>(),
            new AlbumPolicyEngine(() => Guid.NewGuid()),
            Substitute.For<IMusicBrainzClient>(),
            Substitute.For<ICoverArtResolver>(),
            SearchOnAddOff.Commands,
            SearchOnAddOff.Options,
            NullLogger<SongService>.Instance,
            _harness.Videos(context));

    /// <summary>What is in the recycle bin, without the marker file the bin writes into its root.</summary>
    private List<string> RecycledFiles() =>
        Directory.Exists(_harness.Temp.Full("bin"))
            ? [.. Directory.GetFiles(_harness.Temp.Full("bin"), "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetFileName(path).StartsWith(".wondarr", StringComparison.Ordinal))]
            : [];
}
