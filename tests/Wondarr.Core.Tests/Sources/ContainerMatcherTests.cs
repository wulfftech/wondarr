using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;
using FluentAssertions;
using Xunit;

namespace Wondarr.Core.Tests.Sources;

public class ContainerMatcherTests
{
    private const long Flac = 36;
    private const long Mp3320 = 29;
    private const long Unknown = 1;

    /// <summary>Bytes for a file of this many seconds at this bitrate.</summary>
    private static long Bytes(int seconds, int kbps) => seconds * kbps * 1000L / 8;

    private static readonly string[] DiscoveryTitles =
    [
        "One More Time", "Aerodynamic", "Digital Love", "Harder, Better, Faster, Stronger", "Crescendolls",
        "Nightvision", "Superheroes", "High Life", "Something About Us", "Voyager", "Veridis Quo",
        "Short Circuit", "Face to Face", "Too Long",
    ];

    /// <summary>Discovery as a FLAC torrent: one folder, numbered tracks, a cover and a log.</summary>
    private static List<ContainerFile> FlacAlbum()
    {
        var files = new List<ContainerFile>
        {
            new(0, "Daft Punk - Discovery (2001) [FLAC]/cover.jpg", 400_000),
            new(1, "Daft Punk - Discovery (2001) [FLAC]/Discovery.log", 5_000),
        };

        for (var track = 1; track <= DiscoveryTitles.Length; track++)
        {
            files.Add(new ContainerFile(
                files.Count,
                $"Daft Punk - Discovery (2001) [FLAC]/{track:00} - {DiscoveryTitles[track - 1]}.flac",
                Bytes(240, 900)));
        }

        return files;
    }

    [Fact]
    public void Finds_the_track_by_number_and_title_in_an_album()
    {
        var match = ContainerMatcher.Find(
            FlacAlbum(),
            new ContainerMatchRequest("Harder, Better, Faster, Stronger", VersionFlags.None, 4, 240_000, Flac));

        match.Should().NotBeNull();
        match!.File.Index.Should().Be(5);
        match.TrackNoAgrees.Should().BeTrue();
        match.Extension.Should().Be("flac");
    }

    [Fact]
    public void Finds_the_track_by_title_when_the_track_number_is_unknown_or_different()
    {
        // A deluxe edition numbers its tracks differently from the album context's release.
        var match = ContainerMatcher.Find(
            FlacAlbum(),
            new ContainerMatchRequest("Digital Love", VersionFlags.None, 9, 240_000, Flac));

        match!.File.Path.Should().EndWith("03 - Digital Love.flac");
        match.TrackNoAgrees.Should().BeFalse();
    }

    [Fact]
    public void A_studio_song_never_matches_the_live_bonus_track_of_the_same_title()
    {
        var files = FlacAlbum();
        files.Add(new ContainerFile(files.Count, "Daft Punk - Discovery (2001) [FLAC]/15 - One More Time (Live).flac", Bytes(240, 900)));

        var studio = ContainerMatcher.Find(files, new ContainerMatchRequest("One More Time", VersionFlags.None, null, 240_000, Flac));
        var live = ContainerMatcher.Find(files, new ContainerMatchRequest("One More Time (Live)", VersionFlags.Live, null, 240_000, Flac));

        studio!.File.Path.Should().EndWith("01 - One More Time.flac");
        live!.File.Path.Should().EndWith("15 - One More Time (Live).flac");
    }

    [Fact]
    public void A_file_whose_size_cannot_be_the_song_at_that_quality_is_not_a_match()
    {
        // 240 s of FLAC is never 2 MB; a 30 s preview or a mislabelled MP3 is.
        var files = new List<ContainerFile> { new(0, "Album/01 - Voyager.flac", 2_000_000) };

        ContainerMatcher.Find(files, new ContainerMatchRequest("Voyager", VersionFlags.None, 1, 240_000, Flac))
            .Should().BeNull();
    }

    [Fact]
    public void An_unknown_quality_or_length_skips_the_size_check()
    {
        var files = new List<ContainerFile> { new(0, "Album/01 - Voyager.mp3", 2_000_000) };

        ContainerMatcher.Find(files, new ContainerMatchRequest("Voyager", VersionFlags.None, 1, 240_000, Unknown))
            .Should().NotBeNull();
        ContainerMatcher.Find(files, new ContainerMatchRequest("Voyager", VersionFlags.None, 1, null, Mp3320))
            .Should().NotBeNull();
    }

    [Fact]
    public void Two_files_that_match_equally_well_are_no_match()
    {
        var files = new List<ContainerFile>
        {
            new(0, "Album/CD1/Intro.flac", Bytes(60, 900)),
            new(1, "Album/CD2/Intro.flac", Bytes(60, 900)),
        };

        ContainerMatcher.Find(files, new ContainerMatchRequest("Intro", VersionFlags.None, null, 60_000, Flac))
            .Should().BeNull();
    }

    [Fact]
    public void The_track_number_breaks_a_tie_between_two_equal_titles()
    {
        var files = new List<ContainerFile>
        {
            new(0, "Album/CD1/01 - Intro.flac", Bytes(60, 900)),
            new(1, "Album/CD2/05 - Intro.flac", Bytes(60, 900)),
        };

        ContainerMatcher.Find(files, new ContainerMatchRequest("Intro", VersionFlags.None, 5, 60_000, Flac))!
            .File.Index.Should().Be(1);
    }

    [Fact]
    public void A_single_file_torrent_of_the_song_matches()
    {
        var files = new List<ContainerFile> { new(0, "Daft Punk - Get Lucky (feat. Pharrell Williams).mp3", Bytes(248, 320)) };

        ContainerMatcher.Find(files, new ContainerMatchRequest("Get Lucky", VersionFlags.None, null, 248_000, Mp3320))
            .Should().NotBeNull();
    }

    [Fact]
    public void Non_audio_files_and_other_titles_never_match()
    {
        var files = new List<ContainerFile>
        {
            new(0, "Album/Voyager.txt", 1_000),
            new(1, "Album/01 - Something Else.flac", Bytes(240, 900)),
        };

        ContainerMatcher.Find(files, new ContainerMatchRequest("Voyager", VersionFlags.None, 1, 240_000, Flac))
            .Should().BeNull();
    }

    [Fact]
    public void A_release_that_claims_flac_takes_the_flac_copy_when_other_formats_ride_along()
    {
        // archive.org's torrents carry every derivative of an item: the original FLAC beside a VBR MP3
        // that also fits the lossless size window.
        ContainerFile[] files =
        [
            new(0, "nine_inch_nails_the_slip/03_letting_you.flac", 30_841_400),
            new(1, "nine_inch_nails_the_slip/03_letting_you_vbr.mp3", 6_929_633),
            new(2, "nine_inch_nails_the_slip/03_letting_you.ogg", 2_884_016),
        ];
        var flac = SeedData.Qualities.Single(quality => quality.Name == "FLAC").Id;

        var match = ContainerMatcher.Find(files, new ContainerMatchRequest("Letting You", VersionFlags.None, 3, 253_000, flac));

        match.Should().NotBeNull();
        match!.File.Index.Should().Be(0);
    }

    /// <summary>
    /// archive.org's <c>nine-inch-nails-the-slip_202607</c> as a "[FLAC]" release of 24-bit/96 kHz files.
    /// The file list is a faithful approximation, not the real torrent (it is not in the fixtures): the
    /// track names are the album's, the lengths are close to the real ones, and each size is the length
    /// at about 3,800 kbps (24-bit/96 kHz FLAC), which is over the 16-bit bound of 2,700 kbps.
    /// </summary>
    private static readonly (string File, string Title, int Seconds)[] SlipTracks =
    [
        ("03_letting_you", "Letting You", 253),
        ("04_discipline", "Discipline", 256),
        ("05_echoplex", "Echoplex", 226),
        ("06_head_down", "Head Down", 296),
        ("07_lights_in_the_sky", "Lights in the Sky", 223),
        ("08_corona_radiata", "Corona Radiata", 321),
        ("09_the_four_of_us_are_dying", "The Four of Us Are Dying", 313),
        ("10_demon_seed", "Demon Seed", 233),
    ];

    private static List<ContainerFile> SlipRelease()
    {
        var files = new List<ContainerFile> { new(0, "nine-inch-nails-the-slip_202607/cover.jpg", 600_000) };

        foreach (var (file, _, seconds) in SlipTracks)
        {
            files.Add(new ContainerFile(files.Count, $"nine-inch-nails-the-slip_202607/{file}.flac", Bytes(seconds, 3_800)));
        }

        return files;
    }

    [Fact]
    public void A_bare_flac_release_finds_every_song_of_a_24_bit_album()
    {
        var files = SlipRelease();

        foreach (var (file, title, seconds) in SlipTracks)
        {
            var match = ContainerMatcher.Find(files, new ContainerMatchRequest(title, VersionFlags.None, null, seconds * 1000, Flac));

            match.Should().NotBeNull($"\"{title}\" is in a release that says only [FLAC]");
            match!.File.Path.Should().EndWith($"{file}.flac");
        }
    }

    [Fact]
    public void A_release_that_states_16_bit_keeps_the_16_bit_size_bound()
    {
        var files = SlipRelease();

        // 253 s at 3,800 kbps is no 16-bit FLAC: the release said so itself.
        ContainerMatcher.Find(files, new ContainerMatchRequest("Letting You", VersionFlags.None, null, 253_000, Flac, SixteenBitStated: true))
            .Should().BeNull();
    }

    [Fact]
    public void A_release_that_states_24_bit_takes_the_24_bit_bound()
    {
        var files = SlipRelease();
        var flac24 = SeedData.Qualities.Single(quality => quality.Name == "FLAC 24-bit").Id;

        ContainerMatcher.Find(files, new ContainerMatchRequest("Letting You", VersionFlags.None, null, 253_000, flac24, SixteenBitStated: false))
            .Should().NotBeNull();
        ContainerMatcher.Find(files, new ContainerMatchRequest("Letting You", VersionFlags.None, null, 253_000, flac24))
            .Should().NotBeNull();
    }

    [Fact]
    public void The_lower_size_bound_does_not_move_for_a_bare_flac_release()
    {
        // 253 s of FLAC is never 2 MB, whatever the bit depth.
        var files = new List<ContainerFile> { new(0, "Slip/03_letting_you.flac", 2_000_000) };

        ContainerMatcher.Find(files, new ContainerMatchRequest("Letting You", VersionFlags.None, null, 253_000, Flac))
            .Should().BeNull();
    }

    [Fact]
    public void A_bare_flac_release_still_refuses_a_file_far_past_the_hi_res_bound()
    {
        // 253 s at 12,000 kbps is not FLAC of any depth up to 24-bit/96 kHz.
        var files = new List<ContainerFile> { new(0, "Slip/03_letting_you.flac", Bytes(253, 12_000)) };

        ContainerMatcher.Find(files, new ContainerMatchRequest("Letting You", VersionFlags.None, null, 253_000, Flac))
            .Should().BeNull();
    }

    [Fact]
    public void Two_copies_in_a_bare_flac_release_resolve_to_the_one_inside_the_16_bit_window()
    {
        // A release that carries a CD-quality FLAC and a hi-res FLAC of the same song: a bare "[FLAC]" lets
        // both through, and the tie goes to the file that fits 16-bit.
        var files = new List<ContainerFile>
        {
            new(0, "Slip [FLAC 24-96]/03 - Letting You.flac", Bytes(253, 3_800)),
            new(1, "Slip [FLAC]/03 - Letting You.flac", Bytes(253, 900)),
        };

        var match = ContainerMatcher.Find(files, new ContainerMatchRequest("Letting You", VersionFlags.None, null, 253_000, Flac));

        match.Should().NotBeNull();
        match!.File.Index.Should().Be(1);
    }

    [Fact]
    public void Two_hi_res_copies_in_a_bare_flac_release_are_still_a_tie()
    {
        var files = new List<ContainerFile>
        {
            new(0, "Slip [FLAC A]/03 - Letting You.flac", Bytes(253, 3_800)),
            new(1, "Slip [FLAC B]/03 - Letting You.flac", Bytes(253, 4_000)),
        };

        ContainerMatcher.Find(files, new ContainerMatchRequest("Letting You", VersionFlags.None, null, 253_000, Flac))
            .Should().BeNull();
    }

    [Fact]
    public void Two_16_bit_copies_in_a_bare_flac_release_are_still_a_tie()
    {
        var files = new List<ContainerFile>
        {
            new(0, "Slip [FLAC A]/03 - Letting You.flac", Bytes(253, 900)),
            new(1, "Slip [FLAC B]/03 - Letting You.flac", Bytes(253, 950)),
        };

        ContainerMatcher.Find(files, new ContainerMatchRequest("Letting You", VersionFlags.None, null, 253_000, Flac))
            .Should().BeNull();
    }
}
