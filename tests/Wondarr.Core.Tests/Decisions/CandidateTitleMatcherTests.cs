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
