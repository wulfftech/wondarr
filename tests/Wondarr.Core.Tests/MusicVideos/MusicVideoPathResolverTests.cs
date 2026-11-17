using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>Where a song's video is filed: both layouts, the fallback, and the marker check (DECISIONS build session 14 #40).</summary>
public sealed class MusicVideoPathResolverTests : IDisposable
{
    private readonly MusicVideoHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task The_folder_layout_renders_the_template_under_the_video_root()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await using var context = _harness.NewContext();

        var placement = await _harness.Resolver(context).ResolveAsync(song.Id, CancellationToken.None);

        placement.CanWrite.Should().BeTrue();
        placement.VideoPath.Should().Be(_harness.Temp.Full("videos/Massive Attack/Massive Attack - Teardrop.mkv"));
        placement.RootPath.Should().Be(_harness.VideoRoot);
        placement.FellBack.Should().BeFalse();
    }

    [Fact]
    public async Task The_template_uses_the_organizers_filename_cleaning()
    {
        await _harness.InitAsync();
        _harness.Options.NamingTemplate = "{Artist}/{Album} ({Year})/{Title}";
        var song = await _harness.AddSongAsync("AC/DC: Live? [Remix]");
        await using (var seed = _harness.NewContext())
        {
            var context = await seed.AlbumContexts.FirstAsync(row => row.SongId == song.Id);
            context.Kind = AlbumContextKind.Album;
            context.AlbumTitle = "Mezzanine";
            context.Date = "1998-04-17";
            await seed.SaveChangesAsync();
        }

        await using var read = _harness.NewContext();
        var placement = await _harness.Resolver(read).ResolveAsync(song.Id, CancellationToken.None);

        placement.CanWrite.Should().BeTrue();
        var relative = Path.GetRelativePath(_harness.VideoRoot, placement.VideoPath!).Replace('\\', '/');
        relative.Should().StartWith("Massive Attack/Mezzanine (1998)/");
        relative.Should().EndWith(".mkv");
        relative.Split('/').Last().Should().NotContainAny("/", ":", "?");
    }

    [Fact]
    public async Task A_template_bracket_is_a_plain_character()
    {
        await _harness.InitAsync();
        _harness.Options.NamingTemplate = "{Artist}/{Title} [video]";
        var song = await _harness.AddSongAsync("Teardrop");
        await using var context = _harness.NewContext();

        var placement = await _harness.Resolver(context).ResolveAsync(song.Id, CancellationToken.None);

        placement.VideoPath.Should().Be(_harness.Temp.Full("videos/Massive Attack/Teardrop [video].mkv"));
    }

    [Fact]
    public async Task Beside_track_names_the_video_after_the_managed_audio_file()
    {
        await _harness.InitAsync();
        _harness.Options.Layout = MusicVideoOptions.BesideTrackLayout;
        _harness.Options.BesideTrackSuffix = "-video";
        var audio = _harness.Temp.CreateFile("music/Massive Attack/Mezzanine/03 - Teardrop.flac");
        var song = await _harness.AddSongAsync(audioPath: audio);
        await using var context = _harness.NewContext();

        var placement = await _harness.Resolver(context).ResolveAsync(song.Id, CancellationToken.None);

        placement.CanWrite.Should().BeTrue();
        placement.VideoPath.Should().Be(_harness.Temp.Full("music/Massive Attack/Mezzanine/03 - Teardrop-video.mkv"));
        placement.RootPath.Should().Be(_harness.MusicRoot);
        placement.FellBack.Should().BeFalse();
    }

    [Fact]
    public async Task Beside_track_falls_back_to_the_video_root_for_a_song_with_only_a_reference_file()
    {
        await _harness.InitAsync();
        _harness.Options.Layout = MusicVideoOptions.BesideTrackLayout;
        var reference = _harness.Temp.CreateFile("reference/Massive Attack - Teardrop.flac");
        var song = await _harness.AddSongAsync(audioPath: reference, sourceType: "reference");
        await using (var seed = _harness.NewContext())
        {
            seed.ReferenceLibraries.Add(new ReferenceLibrary { Name = "Mine", RootPath = _harness.Temp.Full("reference") });
            await seed.SaveChangesAsync();
        }

        await using var context = _harness.NewContext();
        var placement = await _harness.Resolver(context).ResolveAsync(song.Id, CancellationToken.None);

        placement.CanWrite.Should().BeTrue();
        placement.VideoPath.Should().Be(_harness.Temp.Full("videos/Massive Attack/Massive Attack - Teardrop.mkv"));
        placement.FellBack.Should().BeTrue();
    }

    [Fact]
    public async Task Beside_track_falls_back_to_the_video_root_for_a_song_with_no_file()
    {
        await _harness.InitAsync();
        _harness.Options.Layout = MusicVideoOptions.BesideTrackLayout;
        var song = await _harness.AddSongAsync();
        await using var context = _harness.NewContext();

        var placement = await _harness.Resolver(context).ResolveAsync(song.Id, CancellationToken.None);

        placement.VideoPath.Should().StartWith(_harness.VideoRoot);
        placement.FellBack.Should().BeTrue();
    }

    [Fact]
    public async Task Beside_track_never_writes_next_to_a_file_inside_a_reference_library_even_when_it_is_not_marked_as_one()
    {
        await _harness.InitAsync();
        _harness.Options.Layout = MusicVideoOptions.BesideTrackLayout;
        var audio = _harness.Temp.CreateFile("reference/Massive Attack - Teardrop.flac");
        var song = await _harness.AddSongAsync(audioPath: audio);
        await using (var seed = _harness.NewContext())
        {
            seed.ReferenceLibraries.Add(new ReferenceLibrary { Name = "Mine", RootPath = _harness.Temp.Full("reference") });
            await seed.SaveChangesAsync();
        }

        await using var context = _harness.NewContext();
        var placement = await _harness.Resolver(context).ResolveAsync(song.Id, CancellationToken.None);

        // The file is outside every library root, so it is not a managed file: the video root is used.
        placement.VideoPath.Should().StartWith(_harness.VideoRoot);
        placement.FellBack.Should().BeTrue();
    }

    [Fact]
    public async Task A_video_root_without_its_marker_defers_with_a_reason()
    {
        await _harness.InitAsync();
        File.Delete(Path.Combine(_harness.VideoRoot, LibraryRoots.MarkerFileName));
        var song = await _harness.AddSongAsync();
        await using var context = _harness.NewContext();

        var placement = await _harness.Resolver(context).ResolveAsync(song.Id, CancellationToken.None);

        placement.CanWrite.Should().BeFalse();
        placement.DeferredReason.Should().Contain(LibraryRoots.MarkerFileName).And.Contain("unmounted");
    }

    [Fact]
    public async Task A_missing_video_root_defers_with_a_reason()
    {
        await _harness.InitAsync();
        Directory.Delete(_harness.VideoRoot, recursive: true);
        var song = await _harness.AddSongAsync();
        await using var context = _harness.NewContext();

        var placement = await _harness.Resolver(context).ResolveAsync(song.Id, CancellationToken.None);

        placement.CanWrite.Should().BeFalse();
        placement.DeferredReason.Should().Contain("does not exist");
    }

    [Fact]
    public async Task A_library_root_without_its_marker_defers_a_beside_track_video()
    {
        await _harness.InitAsync();
        _harness.Options.Layout = MusicVideoOptions.BesideTrackLayout;
        var audio = _harness.Temp.CreateFile("music/Massive Attack/Teardrop.flac");
        File.Delete(Path.Combine(_harness.MusicRoot, LibraryRoots.MarkerFileName));
        var song = await _harness.AddSongAsync(audioPath: audio);
        await using var context = _harness.NewContext();

        var placement = await _harness.Resolver(context).ResolveAsync(song.Id, CancellationToken.None);

        placement.CanWrite.Should().BeFalse();
        placement.DeferredReason.Should().Contain(LibraryRoots.MarkerFileName);
    }

    [Fact]
    public async Task No_video_root_defers()
    {
        await _harness.InitAsync();
        _harness.Options.RootPath = null;
        var song = await _harness.AddSongAsync();
        await using var context = _harness.NewContext();

        var placement = await _harness.Resolver(context).ResolveAsync(song.Id, CancellationToken.None);

        placement.CanWrite.Should().BeFalse();
        placement.DeferredReason.Should().Contain("root_path");
    }

    [Fact]
    public async Task A_video_root_that_overlaps_a_library_defers_through_the_shared_root_rules()
    {
        await _harness.InitAsync();
        _harness.Options.RootPath = _harness.MusicRoot;
        var song = await _harness.AddSongAsync();
        await using var context = _harness.NewContext();

        var placement = await _harness.Resolver(context).ResolveAsync(song.Id, CancellationToken.None);

        placement.CanWrite.Should().BeFalse();
        placement.DeferredReason.Should().Contain("overlaps the library");
    }

    [Fact]
    public async Task A_template_without_a_file_name_token_defers()
    {
        await _harness.InitAsync();
        _harness.Options.NamingTemplate = "{Artist}/{Album}";
        var song = await _harness.AddSongAsync();
        await using var context = _harness.NewContext();

        var placement = await _harness.Resolver(context).ResolveAsync(song.Id, CancellationToken.None);

        placement.CanWrite.Should().BeFalse();
        placement.DeferredReason.Should().Contain("naming_template");
    }

    [Fact]
    public async Task An_artist_called_dot_dot_and_a_title_with_a_slash_stay_inside_the_root()
    {
        await _harness.InitAsync();
        _harness.Options.NamingTemplate = "{Artist}/{Title}";
        var song = await _harness.AddSongAsync("AC/DC\\..\\x", artist: "../..");
        await using var context = _harness.NewContext();

        var placement = await _harness.Resolver(context).ResolveAsync(song.Id, CancellationToken.None);

        placement.CanWrite.Should().BeTrue(placement.DeferredReason);
        Path.GetFullPath(placement.VideoPath!).Should().StartWith(_harness.VideoRoot + Path.DirectorySeparatorChar);
        Path.GetRelativePath(_harness.VideoRoot, placement.VideoPath!).Split(Path.DirectorySeparatorChar).Should().NotContain("..");
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("-a/b")]
    [InlineData("-..-")]
    [InlineData("-a\\b")]
    public void A_beside_track_suffix_with_a_separator_or_dot_dot_is_refused(string suffix)
    {
        var result = new MusicVideoOptionsValidator().Validate(null, new MusicVideoOptions { BesideTrackSuffix = suffix });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("beside_track_suffix");
    }

    [Fact]
    public async Task A_song_that_is_gone_defers()
    {
        await _harness.InitAsync();
        await using var context = _harness.NewContext();

        var placement = await _harness.Resolver(context).ResolveAsync(9999, CancellationToken.None);

        placement.CanWrite.Should().BeFalse();
        placement.DeferredReason.Should().Contain("no longer exists");
    }
}
