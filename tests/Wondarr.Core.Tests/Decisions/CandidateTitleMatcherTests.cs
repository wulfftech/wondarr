using FluentAssertions;
using Wondarr.Core.Decisions;
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

    private static Candidate Candidate(string path, string? parsedTitle) => new()
    {
        SourceType = SourceTypes.Soulseek,
        BlocklistKey = path,
        DisplayName = path,
        RemotePath = path,
        Parsed = ParsedName.Empty with { Title = parsedTitle },
    };
}
