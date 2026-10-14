using FluentAssertions;
using Wondarr.Core.Decisions;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Decisions;

public class CandidateTitleMatcherTests
{
    [Theory]
    [InlineData(@"@@user\Music\Nine Days\The Madding Crowd\02 Story of a Girl.flac", "Story of a Girl")]
    [InlineData("Music/Nine Days/The Madding Crowd/02 Story of a Girl.flac", "Story of a Girl")]
    [InlineData(@"@@u\Album\04 - Nine Days - Story of a Girl.mp3", "Story of a Girl")]
    [InlineData(@"@@u\Album\03. Story of a Girl.mp3", "Story of a Girl")]
    [InlineData("Nine Days - Story of a Girl.flac", "Story of a Girl")]
    [InlineData(@"@@u\Album\Story of a Girl.m4a", "Story of a Girl")]
    [InlineData(@"@@u\Album\Story_of_a_Girl.flac", "Story of a Girl")]
    [InlineData(@"@@u\Album\02 Absolutely (Story of a Girl).flac", "Absolutely (Story of a Girl)")]
    [InlineData(@"@@u\Album\14 - Nine Days - Absolutely (Story of a Girl) (acoustic).mp3", "Absolutely (Story of a Girl) (acoustic)")]
    public void Extracts_the_title_from_the_file_name_only(string path, string expected)
    {
        CandidateTitleMatcher.ExtractTitle(path).Should().Be(expected);
    }

    [Fact]
    public void Folder_names_never_supply_the_title()
    {
        CandidateTitleMatcher.ExtractTitle(@"@@u\Various Artists-The Story Of Cadet Records\2. Dorothy Ashby\04 Lonely Girl.flac")
            .Should().Be("Lonely Girl");
    }

    [Fact]
    public void A_path_with_no_file_name_has_no_title()
    {
        CandidateTitleMatcher.ExtractTitle(@"\\").Should().BeNull();
    }

    [Theory]
    [InlineData("Story of a Girl", @"@@u\Nine Days\Album\02 Story of a Girl.flac", null, 1.0)]
    [InlineData("Story of a Girl", @"@@u\Nine Days\Album\02 Story Of A Girl.flac", "Story Of A Girl", 1.0)]
    // MusicBrainz lists both titles for one song: a whole parenthesised part is a match, either way round.
    [InlineData("Story of a Girl", @"@@u\Nine Days\Album\02 Absolutely (Story of a Girl).flac", "Absolutely", 1.0)]
    [InlineData("Absolutely (Story of a Girl)", @"@@u\Nine Days\Album\02 Story of a Girl.flac", "Story of a Girl", 1.0)]
    [InlineData("Story of a Girl", @"@@u\Nine Days\Album\02 Story of a Girl (Live at Home).flac", "Story of a Girl", 1.0)]
    public void Matches_the_same_song_under_either_title(string song, string path, string? parsedTitle, double expected)
    {
        CandidateTitleMatcher.Similarity(song, Candidate(path, parsedTitle)).Should().BeApproximately(expected, 0.001);
    }

    [Theory]
    [InlineData("Hurt", "yt0Hurt00001", "Johnny Cash - Hurt", 1.0)]
    [InlineData("Hurt", "yt0Hurt00001", "Johnny Cash - Hurt (Official Video)", 1.0)]
    [InlineData("Hey Jude", @"@@u\Beatles\Hey Jude - The Beatles.mp3", "The Beatles", 1.0)]
    [InlineData("Kashmir", @"@@u\Led Zeppelin\Album\01-led_zeppelin-kashmir.mp3", "led zeppelin-kashmir", 1.0)]
    [InlineData("21 Guns", @"@@u\Green Day\Album\05 - 21 Guns.mp3", "Guns", 1.0)]
    [InlineData("3 AM", @"@@u\Matchbox\Album\03 - 3 AM.mp3", "AM", 1.0)]
    [InlineData("Bohemian Rhapsody", @"@@u\Queen\Album\11 Bohemian Rhapsody - Remastered 2011.flac", "Bohemian Rhapsody", 1.0)]
    // Every word of the shorter title is in the longer one, but scattered: 0.9.
    [InlineData("Symphony No. 5 in C minor", @"@@u\B\S5\01 Symphony 5.flac", "Symphony 5", 0.9)]
    [InlineData("Symphony No. 5 in C minor, Op. 67: I. Allegro con brio", @"@@u\B\S5\01 Symphony No.5 - I. Allegro con brio.flac", "I. Allegro con brio", 1.0)]
    public void Matches_a_title_inside_a_longer_one(string song, string path, string? parsedTitle, double expected)
    {
        CandidateTitleMatcher.Similarity(song, Candidate(path, parsedTitle)).Should().BeApproximately(expected, 0.001);
    }

    [Theory]
    [InlineData("Story of a Girl", true)]
    [InlineData("Café Tacvba", true)]
    [InlineData("残酷な天使のテーゼ", false)]
    [InlineData("사랑이 온거야", false)]
    [InlineData("1999", null)]
    public void Tells_the_script_of_a_title(string title, bool? latin)
    {
        CandidateTitleMatcher.IsLatinScript(title).Should().Be(latin);
    }

    [Fact]
    public void Scores_an_unrelated_title_low()
    {
        CandidateTitleMatcher.Similarity("Story of a Girl", Candidate(@"@@u\X\Y\04 Lonely Girl.flac", "Lonely Girl"))
            .Should().BeLessThan(DecisionEngine.TitleFloor);
    }

    [Fact]
    public void Reads_the_file_name_when_the_parser_found_no_title()
    {
        CandidateTitleMatcher.Similarity("Story of a Girl", Candidate(@"@@u\Nine Days\Album\02 Story of a Girl.flac", null))
            .Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void A_container_with_no_title_of_its_own_has_no_similarity()
    {
        var release = Candidate("Nine Days - The Madding Crowd [FLAC]", null) with { Container = CandidateContainer.AlbumContainer };

        CandidateTitleMatcher.Similarity("Story of a Girl", release).Should().BeNull();
    }

    [Theory]
    [InlineData("Story of a Girl", @"@@u\Va\Now 48\12 Story of a Girl.flac", null, true)]
    [InlineData("Story of a Girl", @"@@u\Va\Now 48\12 Story Of A Girl.flac", "Story Of A Girl", true)]
    [InlineData("Story of a Girl", @"@@u\Va\90s\07 Nine Days - Story of a Girl.flac", "Nine Days - Story of a Girl", true)]
    [InlineData("Story of a Girl", @"@@u\Va\X\04 Lonely Girl.flac", "Lonely Girl", false)]
    [InlineData("Story of a Girl", @"@@u\Va\X\05 Story of a Dream.flac", "Story of a Dream", false)]
    // Holding the title is not being the title: a longer file title is not the song.
    [InlineData("Story of a Girl", @"@@u\Va\X\05 Story of a Girl Part 2.flac", "Story of a Girl Part 2", false)]
    // The bracketed part of a title on its own is a partial match; only the whole title and the title without hints count.
    [InlineData("Absolutely (Story of a Girl)", @"@@u\Va\X\05 Story of a Girl.flac", "Story of a Girl", false)]
    [InlineData("Absolutely (Story of a Girl)", @"@@u\Va\X\05 Absolutely (Story of a Girl).flac", null, true)]
    [InlineData("Story of a Girl (Remastered)", @"@@u\Va\X\05 Story of a Girl.flac", null, true)]
    // A title that starts with a number is the title, with the track number as well as without it.
    [InlineData("21 Guns", @"@@u\Va\X\21 Guns.flac", null, true)]
    [InlineData("21 Guns", @"@@u\Va\X\05 - 21 Guns.flac", null, true)]
    // A soft marker (remaster, explicit, clean) in the file title is not a different song.
    [InlineData("Story of a Girl", @"@@u\Va\X\12 Story of a Girl (Remastered).flac", null, true)]
    [InlineData("Story of a Girl", @"@@u\Va\X\12 Story of a Girl [Explicit].flac", null, true)]
    [InlineData("Story of a Girl", @"@@u\Va\X\12 Story of a Girl (Remastered 2011).flac", null, true)]
    // A hard marker the song does not have is.
    [InlineData("Story of a Girl", @"@@u\Va\X\12 Story of a Girl (Live).flac", null, false)]
    [InlineData("Story of a Girl", @"@@u\Va\X\12 Story of a Girl (Acoustic).flac", null, false)]
    public void Tells_a_full_title_match_from_a_partial_one(string song, string path, string? parsedTitle, bool expected)
    {
        var candidate = Candidate(path, parsedTitle);

        CandidateTitleMatcher.IsFullTitleMatch(song, candidate, VersionFlags.None)
            .Should().Be(expected);
    }

    [Fact]
    public void A_version_marker_counts_towards_the_full_title_only_when_the_song_has_it()
    {
        var candidate = Candidate(@"@@u\Va\X\12 Story of a Girl (Radio Edit).flac", "Story of a Girl") with
        {
            Parsed = ParsedName.Empty with { Title = "Story of a Girl", VersionFlags = VersionFlags.RadioEdit },
        };

        // The song is the radio edit, with or without the marker in its title: the file is the song.
        CandidateTitleMatcher.IsFullTitleMatch("Story of a Girl (Radio Edit)", candidate, VersionFlags.RadioEdit)
            .Should().BeTrue();
        CandidateTitleMatcher.IsFullTitleMatch("Story of a Girl", candidate, VersionFlags.RadioEdit)
            .Should().BeTrue();

        // The song is not: the radio edit is another song, the parser's title and flags notwithstanding.
        var withoutTitle = Candidate(@"@@u\Va\X\12 Story of a Girl (Radio Edit).flac", null);

        CandidateTitleMatcher.IsFullTitleMatch("Story of a Girl", withoutTitle, VersionFlags.None).Should().BeFalse();
        CandidateTitleMatcher.IsFullTitleMatch("Story of a Girl (Radio Edit)", withoutTitle, VersionFlags.RadioEdit).Should().BeTrue();
    }

    [Fact]
    public void A_container_with_no_title_of_its_own_has_no_full_title_match()
    {
        var release = Candidate("Nine Days - The Madding Crowd [FLAC]", null) with { Container = CandidateContainer.AlbumContainer };

        CandidateTitleMatcher.IsFullTitleMatch("Story of a Girl", release, VersionFlags.None)
            .Should().BeFalse();
    }

    private static Candidate Candidate(string path, string? parsedTitle) => new()
    {
        SourceType = SourceTypes.Soulseek,
        BlocklistKey = path,
        DisplayName = path,
        RemotePath = path,
        Parsed = ParsedName.Empty with { Title = parsedTitle },
    };
}
