using FluentAssertions;
using Wondarr.Sources.Torznab.Clients;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Clients;

/// <summary>Which files of a usenet post the trimmer deletes before the job starts.</summary>
public sealed class NzbTrimmerTests
{
    private const string Stem = "norah_jones-come_away_with_me-cd-flac-2002-fada";
    private const string Wanted = "01-norah_jones-dont_know_why.flac";

    private static readonly string[] Tracks =
    [
        Wanted,
        "02-norah_jones-seven_years.flac",
        "03-norah_jones-cold_cold_heart.flac",
        "04-norah_jones-feelin_the_same_way.flac",
        "05-norah_jones-come_away_with_me.flac",
        "06-norah_jones-shoot_the_moon.flac",
        "07-norah_jones-turn_me_on.flac",
        "08-norah_jones-lonestar.flac",
        "09-norah_jones-i_ve_got_to_see_you_again.flac",
        "10-norah_jones-painter_song.flac",
        "11-norah_jones-one_flight_down.flac",
        "12-norah_jones-nightingale.flac",
        "13-norah_jones-the_long_day_is_over.flac",
        "14-norah_jones-the_nearness_of_you.flac",
    ];

    [Fact]
    public void A_trimmed_post_loses_its_par2_set_and_sfv_and_keeps_the_wanted_track_and_small_sidecars()
    {
        var files = FadaPost();
        files.Should().HaveCount(24);

        var deleted = NzbTrimmer.FilesToDelete(files, name => name == Wanted);

        KeptNames(files, deleted).Should().BeEquivalentTo([Wanted, $"00-{Stem}.nfo", $"00-{Stem}.jpg"]);
        deleted.Should().HaveCount(21).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_post_that_loses_nothing_keeps_its_par2_set_and_its_repair_power()
    {
        NzbTrimmer.FilesToDelete(FadaPost(), _ => true).Should().BeEmpty();
    }

    [Fact]
    public void Upper_case_par2_and_sfv_extensions_go_too()
    {
        var files = FadaPost(par2: "PAR2", sfv: "SFV");

        var deleted = NzbTrimmer.FilesToDelete(files, name => name == Wanted);

        KeptNames(files, deleted).Should().BeEquivalentTo([Wanted, $"00-{Stem}.nfo", $"00-{Stem}.jpg"]);
    }

    [Fact]
    public void Every_small_sidecar_stays()
    {
        UsenetFileInfo[] files =
        [
            new("1", "Album.flac", 30_000_000),
            new("2", "Other.flac", 30_000_000),
            new("3", "Album.par2", 40_000),
            new("4", "Album.sfv", 1_000),
            new("5", "Album.nfo", 2_000),
            new("6", "Album.jpg", 200_000),
            new("7", "Album.png", 200_000),
            new("8", "Album.cue", 2_000),
            new("9", "Album.log", 2_000),
            new("10", "Album.m3u", 2_000),
            new("11", "Album.txt", 2_000),
        ];

        // Repair data first, so a delete that fails part-way never leaves Par2 beside missing tracks.
        NzbTrimmer.FilesToDelete(files, name => name == "Album.flac").Should().Equal("3", "4", "2");
    }

    [Fact]
    public void A_post_with_nothing_wanted_in_it_downloads_whole_with_its_par2_set()
    {
        NzbTrimmer.FilesToDelete(FadaPost(), _ => false).Should().BeEmpty();
    }

    [Fact]
    public void A_post_with_an_archive_in_it_is_never_trimmed()
    {
        UsenetFileInfo[] files =
        [
            new("1", "Album.part01.rar", 50_000_000),
            new("2", "Album.part02.rar", 50_000_000),
            new("3", "Album.par2", 40_000),
            new("4", "Album.sfv", 1_000),
        ];

        NzbTrimmer.FilesToDelete(files, _ => false).Should().BeEmpty();
    }

    /// <summary>The post that failed in SABnzbd on 2026-10-15: 24 files, a Par2 set describing all 14 tracks.</summary>
    private static UsenetFileInfo[] FadaPost(string par2 = "par2", string sfv = "sfv") =>
    [
        new("f01", $"00-{Stem}.jpg", 300_000),
        new("f02", $"00-{Stem}.nfo", 4_000),
        new("f03", $"00-{Stem}.{sfv}", 1_500),
        new("f04", $"{Stem}.{par2}", 40_000),
        .. Enumerable.Range(0, 6).Select(index => new UsenetFileInfo($"v{index}", $"{Stem}.vol{index * 4:000}+{index + 1:00}.{par2}", 1_000_000)),
        .. Tracks.Select((track, index) => new UsenetFileInfo($"t{index + 1:00}", track, 25_000_000)),
    ];

    private static IEnumerable<string> KeptNames(UsenetFileInfo[] files, IReadOnlyList<string> deleted) =>
        files.Where(file => !deleted.Contains(file.FileId)).Select(file => file.FileName);
}
