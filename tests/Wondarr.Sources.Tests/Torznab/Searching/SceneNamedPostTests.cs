using FluentAssertions;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Sources.Torznab.Parsing;
using Wondarr.Sources.Torznab.Searching;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Searching;

/// <summary>
/// Real-world usenet music posts (M-42a), end to end from the NZB through the file list, the matcher and
/// the decision engine. The two NZBs under <c>tests/fixtures/nzb</c> are synthetic: they copy the shape of
/// two posts seen on a live indexer (subjects, scene file names, upper-case <c>.PAR2</c>, realistic sizes)
/// with an invented artist and invented message-ids. CI used to see only clean, Wondarr-named posts.
/// </summary>
public sealed class SceneNamedPostTests
{
    private const string FlacRelease = "Velvet.Harbour-Paper.Lanterns-CD-FLAC-2002-FADA";
    private const string Mp3Release = "Velvet.Harbour-Paper.Lanterns-JAPANESE.RETAIL-2002-pH";

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_scene_flac_post_is_clean_and_track_one_is_found()
    {
        var files = Parse("scene-flac-album.nzb");

        NzbFileList.IsClean(files).Should().BeTrue();

        var candidate = Build(FlacRelease, "scene-flac-album.nzb", Wanted("Don’t Wait Up", 1));

        candidate.Should().NotBeNull();
        candidate!.RemotePath.Should().Be("01-velvet_harbour-dont_wait_up.flac");
        candidate.Extension.Should().Be("flac");
        candidate.Availability.FileListKnown.Should().BeTrue();
        candidate.Parsed.TrackNo.Should().Be(1);
        candidate.Parsed.Artist.Should().Be("Velvet Harbour");
        candidate.Release!.FileIndex.Should().Be(7, "the seventh file of the NZB is track one");
    }

    [Fact]
    public void The_scene_mp3_post_with_upper_case_par2_files_is_clean_and_track_one_is_found()
    {
        var files = Parse("scene-mp3-album.nzb");

        files.Where(file => file.IsPar2).Should().HaveCount(4);
        files.Where(file => file.IsPar2).Select(file => file.FileName).Should().Contain(name => name!.EndsWith(".vol00+1.PAR2", StringComparison.Ordinal));
        NzbFileList.IsClean(files).Should().BeTrue("the .PAR2 volumes are Par2 volumes whatever their case");

        var candidate = Build(Mp3Release, "scene-mp3-album.nzb", Wanted("Don’t Wait Up", 1));

        candidate.Should().NotBeNull();
        candidate!.RemotePath.Should().Be("01-velvet_harbour-dont_wait_up-ph.mp3");
        candidate.Extension.Should().Be("mp3");
        candidate.Release!.Song!.ReleaseArtist.Should().Be("Velvet Harbour");
        candidate.Release.Song.ReleaseGroup.Should().Be("pH");
    }

    [Theory]
    [InlineData("scene-flac-album.nzb", FlacRelease, 6)]
    [InlineData("scene-mp3-album.nzb", Mp3Release, 6)]
    public void Every_track_of_a_scene_post_is_found_by_its_own_title(string fixture, string release, int tracks)
    {
        string[] titles = ["Don’t Wait Up", "Salt and Static", "Harbour Lights", "Paper Lanterns", "Low Tide (Reprise)", "Run-Away Train"];
        titles.Should().HaveCount(tracks);

        for (var number = 1; number <= tracks; number++)
        {
            var candidate = Build(release, fixture, Wanted(titles[number - 1], number));

            candidate.Should().NotBeNull($"track {number} is \"{titles[number - 1]}\"");
            candidate!.RemotePath.Should().StartWith($"{number:00}-");
        }
    }

    [Fact]
    public void A_song_that_is_not_on_the_post_finds_no_file_even_with_the_albums_title()
    {
        // "Paper Lanterns" is the album and track four; "Harbour" is neither.
        Build(FlacRelease, "scene-flac-album.nzb", Wanted("Harbour", 3)).Should().BeNull();
        Build(FlacRelease, "scene-flac-album.nzb", Wanted("Low Tide", 5)).Should().BeNull("the reprise is not the song");
    }

    [Fact]
    public void The_decision_engine_accepts_the_scene_post_s_track()
    {
        var candidate = Build(FlacRelease, "scene-flac-album.nzb", Wanted("Don’t Wait Up", 1))!;

        var decision = new DecisionEngine().Evaluate(Context("Don’t Wait Up"), [candidate]).Single();

        decision.Rejections.Should().BeEmpty();
        decision.Accepted.Should().BeTrue();
    }

    [Fact]
    public void The_mp3_scene_post_s_track_passes_the_name_rules()
    {
        var candidate = Build(Mp3Release, "scene-mp3-album.nzb", Wanted("Don’t Wait Up", 1))!;

        var decision = new DecisionEngine().Evaluate(Context("Don’t Wait Up"), [candidate]).Single();

        // The profile may not take this bitrate, but nothing about the file's name is wrong.
        decision.Rejections.Select(rejection => rejection.Reason).Should().NotContain(
            [RejectionReason.TitleMismatch, RejectionReason.ArtistMismatch, RejectionReason.VersionMismatch, RejectionReason.NotAudio, RejectionReason.SizeSanity]);
    }

    [Fact]
    public void A_rar_post_is_an_unknown_file_list_candidate_the_engine_does_not_turn_away_as_not_audio()
    {
        var files = Parse("rar-album.nzb");
        var postSize = files.Sum(file => file.Size);

        NzbFileList.IsClean(files).Should().BeFalse();

        var candidate = Build("Aphex.Twin-Selected.Ambient.Works.85-92-CD-FLAC-1992-GRP", "rar-album.nzb", Wanted("Xtal", null))!;

        candidate.Extension.Should().BeNull();
        candidate.Release!.FileIndex.Should().BeNull();
        candidate.Availability.FileListKnown.Should().BeFalse();

        var context = Context("Xtal", "Aphex Twin") with { MaxContainerSizeBytes = postSize + 1 };
        var accepted = new DecisionEngine().Evaluate(context, [candidate]).Single();

        accepted.Rejections.Select(rejection => rejection.Reason).Should().NotContain(RejectionReason.NotAudio);
        accepted.Accepted.Should().BeTrue();

        // It downloads whole, so the limit holds for it.
        var tooLarge = new DecisionEngine().Evaluate(context with { MaxContainerSizeBytes = postSize - 1 }, [candidate]).Single();

        tooLarge.Rejections.Select(rejection => rejection.Reason).Should().Contain(RejectionReason.ContainerTooLarge);
    }

    [Fact]
    public void A_clean_scene_post_over_the_container_limit_is_not_too_large()
    {
        var files = Parse("scene-flac-album.nzb");
        var candidate = Build(FlacRelease, "scene-flac-album.nzb", Wanted("Don’t Wait Up", 1))!;
        var context = Context("Don’t Wait Up") with { MaxContainerSizeBytes = files.Sum(file => file.Size) / 2 };

        candidate.Release!.Size.Should().BeGreaterThan(context.MaxContainerSizeBytes);
        new DecisionEngine().Evaluate(context, [candidate]).Single().Rejections
            .Should().NotContain(rejection => rejection.Reason == RejectionReason.ContainerTooLarge);
    }

    private static ContainerMatchRequest Wanted(string title, int? trackNo) =>
        new(title, VersionFlags.None, trackNo, 195_000, 1);

    private static DecisionContext Context(string title, string artist = "Velvet Harbour") => new()
    {
        SongTitle = title,
        MainArtists = [artist],
        SongDurationMs = 195_000,
        Profile = SeedData.QualityProfiles.Single(profile => profile.Id == SeedData.LosslessProfileId),
        Qualities = SeedData.Qualities.ToDictionary(quality => quality.Id, quality => quality),
    };

    /// <summary>The candidate the search builds from one NZB: parse, clean or not, audio files only, then the matcher.</summary>
    private static Candidate? Build(string title, string fixture, ContainerMatchRequest wanted)
    {
        var files = Parse(fixture);
        var postSize = files.Sum(file => file.Size);
        var release = new IndexerRelease(
            title,
            "guid-1",
            "https://usenet.example/get/1",
            null,
            null,
            postSize,
            null,
            null,
            null,
            12,
            [3010],
            null,
            null,
            DownloadProtocol.Usenet,
            1,
            "Indexer",
            null);

        var listing = NzbFileList.IsClean(files)
            ? new ContainerListing(
                release,
                [.. files.Where(file => file.IsAudio && file.FileName is not null).Select(file => new ContainerFile(file.Index, file.FileName!, file.Size))],
                null,
                postSize)
            : new ContainerListing(release, null, null, postSize);

        return ContainerCandidates.Build(DownloadProtocol.Usenet, listing, wanted, "velvet harbour paper lanterns", Now);
    }

    private static IReadOnlyList<NzbFile> Parse(string fixture)
    {
        using var stream = File.OpenRead(FixturePath(fixture));

        return NzbFileList.Parse(stream);
    }

    private static string FixturePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests", "fixtures", "nzb", name)))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "tests", "fixtures", "nzb", name);
    }
}
