using FluentAssertions;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.Organizer;

/// <summary>
/// The one sidecar rule (<see cref="SongFileRules"/>): which files travel with an audio file. The lyrics
/// sidecars behave as they always did; a beside-track music video travels with its <c>.nfo</c> and thumbnail,
/// under whatever suffix it already has.
/// </summary>
public sealed class SongFileRulesVideoTests : IDisposable
{
    private readonly TempRoot _temp = new();
    private static readonly MusicVideoOptions Beside = new() { Layout = MusicVideoOptions.BesideTrackLayout };

    private readonly IDiskOperations _disk = new DiskOperations();

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("music/A/01 - Song.flac", "music/A/01 - Song-video.mkv", "-video")]
    [InlineData("music/A/01 - Song.flac", "music/A/01 - Song.clip.MKV", ".clip")]
    [InlineData("music/A/01 - Song.flac", "music/A/01 - Song (2)-video.mkv", " (2)-video")]
    public void A_video_in_the_audio_folder_named_after_it_is_beside_it_and_the_remainder_is_its_suffix(
        string audio,
        string video,
        string remainder) =>
        SongFileRules.BesideRemainder(_temp.Full(audio), _temp.Full(video)).Should().Be(remainder);

    [Theory]
    [InlineData("music/A/01 - Song.flac", "music/B/01 - Song-video.mkv")]
    [InlineData("music/A/01 - Song.flac", "music/A/02 - Other-video.mkv")]
    [InlineData("music/A/01 - Song.flac", "music/A/01 - Song-video.mp4")]
    [InlineData("music/A/01 - Song.flac", "music/A/01 - Song.mkv")]
    [InlineData("music/A/01 - Song.flac", "music/A/01 - Son.mkv")]
    public void Anything_else_is_not_beside_the_audio_file(string audio, string video) =>
        SongFileRules.BesideRemainder(_temp.Full(audio), _temp.Full(video)).Should().BeNull();

    [Fact]
    public void A_row_with_no_path_is_not_beside_anything() =>
        SongFileRules.BesideRemainder(_temp.Full("music/A/01 - Song.flac"), null).Should().BeNull();

    [Fact]
    public void The_video_moves_first_and_its_sidecars_follow_under_the_new_name_and_the_same_suffix()
    {
        var audio = _temp.Full("music/A/01 - Song.flac");
        var video = SeedVideo("music/A/01 - Song", "-clip");
        var target = _temp.Full("music/B/09 - Renamed.flac");

        var moves = SongFileRules.VideoMoves(_disk, Beside, audio, target, video);

        moves.Select(move => (move.From, move.To, move.TargetTaken, move.IsVideo)).Should().Equal(
            (video, _temp.Full("music/B/09 - Renamed-clip.mkv"), false, true),
            (_temp.Full("music/A/01 - Song-clip.nfo"), _temp.Full("music/B/09 - Renamed-clip.nfo"), false, false),
            (_temp.Full("music/A/01 - Song-clip-thumb.jpg"), _temp.Full("music/B/09 - Renamed-clip-thumb.jpg"), false, false));
    }

    [Fact]
    public void A_sidecar_that_is_not_there_is_not_moved()
    {
        var audio = _temp.Full("music/A/01 - Song.flac");
        var video = SeedVideo("music/A/01 - Song", "-video");
        File.Delete(_temp.Full("music/A/01 - Song-video.nfo"));

        var moves = SongFileRules.VideoMoves(_disk, Beside, audio, _temp.Full("music/B/01 - Song.flac"), video);

        moves.Select(move => move.From).Should().Equal(video, _temp.Full("music/A/01 - Song-video-thumb.jpg"));
    }

    [Fact]
    public void A_changed_suffix_setting_does_not_strand_a_video_that_already_exists()
    {
        // The setting says "-video" now; the file was made under "-old". The remainder is read off the file.
        var audio = _temp.Full("music/A/01 - Song.flac");
        var video = SeedVideo("music/A/01 - Song", "-old");

        var moves = SongFileRules.VideoMoves(_disk, Beside, audio, _temp.Full("music/B/01 - Song.flac"), video);

        moves.Should().HaveCount(3);
        moves[0].To.Should().Be(_temp.Full("music/B/01 - Song-old.mkv"));
    }

    [Fact]
    public void A_video_whose_new_name_is_taken_is_reported_and_takes_its_sidecars_nowhere()
    {
        var audio = _temp.Full("music/A/01 - Song.flac");
        var video = SeedVideo("music/A/01 - Song", "-video");
        _temp.CreateFile("music/B/01 - Song-video.mkv", "somebody else's");

        var moves = SongFileRules.VideoMoves(_disk, Beside, audio, _temp.Full("music/B/01 - Song.flac"), video);

        moves.Should().ContainSingle().Which.TargetTaken.Should().BeTrue();
        SongFileRules.ApplyVideoMoves(_disk, moves).Should().BeNull();
        File.ReadAllText(_temp.Full("music/B/01 - Song-video.mkv")).Should().Be("somebody else's");
        File.Exists(video).Should().BeTrue();
        File.Exists(_temp.Full("music/A/01 - Song-video.nfo")).Should().BeTrue();
    }

    [Fact]
    public void A_sidecar_whose_new_name_is_taken_is_left_and_the_rest_still_move()
    {
        var audio = _temp.Full("music/A/01 - Song.flac");
        var video = SeedVideo("music/A/01 - Song", "-video");
        _temp.CreateFile("music/B/01 - Song-video.nfo", "somebody else's");

        var moves = SongFileRules.VideoMoves(_disk, Beside, audio, _temp.Full("music/B/01 - Song.flac"), video);
        var now = SongFileRules.ApplyVideoMoves(_disk, moves);

        now.Should().Be(_temp.Full("music/B/01 - Song-video.mkv"));
        File.ReadAllText(_temp.Full("music/B/01 - Song-video.nfo")).Should().Be("somebody else's");
        File.Exists(_temp.Full("music/A/01 - Song-video.nfo")).Should().BeTrue("the user's file won, so ours stays");
        File.Exists(_temp.Full("music/B/01 - Song-video-thumb.jpg")).Should().BeTrue();
    }

    [Fact]
    public void The_folder_layout_never_moves_a_video_with_its_audio()
    {
        var audio = _temp.Full("music/A/01 - Song.flac");
        var video = SeedVideo("music/A/01 - Song", "-video");
        var target = _temp.Full("music/B/01 - Song.flac");

        SongFileRules.VideoMoves(_disk, new MusicVideoOptions { Layout = MusicVideoOptions.FolderLayout }, audio, target, video).Should().BeEmpty();
        SongFileRules.VideoMoves(_disk, null, audio, target, video).Should().BeEmpty();
    }

    [Fact]
    public void A_file_taken_into_staging_is_finished_whatever_the_layout_is_now()
    {
        var audio = _temp.Full("music/A/01 - Song.flac");
        var video = SeedVideo("music/A/01 - Song", "-video");

        SongFileRules.VideoMoves(_disk, null, audio, _temp.Full("music/B/01 - Song.flac"), video, named => named).Should().HaveCount(3);
    }

    [Fact]
    public void A_sibling_audio_files_video_is_never_touched()
    {
        // "01 - Song (2)" is another song's file; its video is not the row's, so it is not in the row's group.
        var audio = _temp.Full("music/A/01 - Song.flac");
        var video = SeedVideo("music/A/01 - Song", "-video");
        var sibling = SeedVideo("music/A/01 - Song (2)", "-video");

        var moves = SongFileRules.VideoMoves(_disk, Beside, audio, _temp.Full("music/B/01 - Song.flac"), video);

        moves.Select(move => move.From).Should().NotContain(sibling);
        moves.Should().OnlyContain(move => !move.From.Contains("(2)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_video_already_named_after_a_longer_new_name_is_not_moved_again()
    {
        var oldAudio = _temp.Full("music/A/Song.flac");
        var newAudio = _temp.Full("music/A/Song (Live).flac");

        SongFileRules.AlreadyBesideNewName(oldAudio, newAudio, _temp.Full("music/A/Song (Live)-video.mkv")).Should().BeTrue();
        SongFileRules.AlreadyBesideNewName(oldAudio, newAudio, _temp.Full("music/A/Song-video.mkv")).Should().BeFalse();

        // A shortened name: the video beside both names is the old file's and does move.
        SongFileRules.AlreadyBesideNewName(newAudio, oldAudio, _temp.Full("music/A/Song (Live)-video.mkv")).Should().BeFalse();
    }

    [Fact]
    public void A_new_name_with_the_same_stem_moves_nothing()
    {
        var audio = _temp.Full("music/A/01 - Song.flac");
        var video = SeedVideo("music/A/01 - Song", "-video");

        SongFileRules.VideoMoves(_disk, Beside, audio, _temp.Full("music/A/01 - Song.mp3"), video).Should().BeEmpty();
    }

    [Fact]
    public void A_video_that_is_not_beside_the_audio_file_does_not_move()
    {
        var audio = _temp.Full("music/A/01 - Song.flac");
        var video = SeedVideo("videos/Artist/Artist - Song", string.Empty);

        SongFileRules.VideoMoves(_disk, Beside, audio, _temp.Full("music/B/01 - Song.flac"), video).Should().BeEmpty();
    }

    [Fact]
    public void A_video_whose_file_is_missing_moves_nothing_even_when_its_sidecars_exist()
    {
        var audio = _temp.Full("music/A/01 - Song.flac");
        var video = SeedVideo("music/A/01 - Song", "-video");
        File.Delete(video);

        SongFileRules.VideoMoves(_disk, Beside, audio, _temp.Full("music/B/01 - Song.flac"), video).Should().BeEmpty();
    }

    [Fact]
    public void The_lyrics_sidecars_are_unchanged()
    {
        var audio = _temp.CreateFile("music/A/01 - Song.flac");
        _temp.CreateFile("music/A/01 - Song.lrc");
        _temp.CreateFile("music/A/01 - Song.txt");
        _temp.CreateFile("music/B/01 - Song.txt", "taken");

        SongFileRules.SidecarExtensions.Should().Equal(".lrc", ".txt");
        SongFileRules.SidecarsOf(_disk, audio).Should().Equal(_temp.Full("music/A/01 - Song.lrc"), _temp.Full("music/A/01 - Song.txt"));

        SongFileRules.SidecarMoves(_disk, audio, _temp.Full("music/B/01 - Song.flac"))
            .Should()
            .Equal(
                (_temp.Full("music/A/01 - Song.lrc"), _temp.Full("music/B/01 - Song.lrc"), false),
                (_temp.Full("music/A/01 - Song.txt"), _temp.Full("music/B/01 - Song.txt"), true));
    }

    [Fact]
    public void A_folder_holding_a_video_is_not_one_a_move_emptied_and_the_video_is_not_audio()
    {
        _temp.CreateFile("music/A/cover.jpg");
        _temp.CreateFile("music/A/01 - Song-video.mkv");

        SongFileRules.AudioExtensions.Should().NotContain([".mkv", ".nfo", ".jpg"]);
        SongFileRules.HoldsOnlyCovers(_disk, _temp.Full("music/A"), out _).Should().BeFalse("a video is never recycled as a leftover cover");
    }

    private string SeedVideo(string stem, string suffix)
    {
        foreach (var extension in new[] { ".mkv", ".nfo", "-thumb.jpg" })
        {
            _temp.CreateFile(stem + suffix + extension);
        }

        return _temp.Full(stem + suffix + ".mkv");
    }
}
