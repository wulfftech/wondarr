using FluentAssertions;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Sources;

/// <summary>
/// Golden tests for <see cref="SoulseekFilenameParser"/> against <c>tests/fixtures/filenames.json</c> —
/// real Soulseek paths recorded on 2026-09-29 — and against the recorded slskd search responses, which
/// the parser must survive without throwing.
/// </summary>
public class SoulseekFilenameParserTests
{
    private static readonly FilenamesFixture Fixture = SoulseekFixtures.Load<FilenamesFixture>("filenames.json");

    public static TheoryData<string> RecordedPaths()
    {
        var data = new TheoryData<string>();

        foreach (var testCase in Fixture.Cases)
        {
            data.Add(testCase.Path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RecordedPaths))]
    public void Reads_the_artist_title_album_and_flags_of_a_recorded_path(string path)
    {
        var expected = Fixture.Cases.Single(testCase => testCase.Path == path).Expected;

        var result = SoulseekFilenameParser.Parse(path);

        result.Extension.Should().Be(expected.Extension);
        result.IsAudio.Should().Be(expected.IsAudio);

        // Everything else is best effort for a file we would never import.
        if (!expected.IsAudio)
        {
            return;
        }

        result.Parsed.Artist.Should().Be(expected.Artist);
        result.Parsed.Title.Should().Be(expected.Title);
        result.Parsed.Album.Should().Be(expected.Album);
        result.Parsed.TrackNo.Should().Be(expected.TrackNo);
        VersionFlagNames.ToWireNames(result.Parsed.VersionFlags).Should().Equal(expected.Flags);
        VersionFlagNames.ToWireNames(result.Parsed.PathVersionFlags).Should().Equal(expected.PathFlags);
        result.Parsed.FeaturedArtists.Should().Equal(expected.Featured);
        result.Parsed.HasUnexplainedBrackets.Should().Be(expected.UnexplainedBrackets);

        if (expected.HintsInclude.Count > 0)
        {
            result.Parsed.VersionHints.Should().Contain(expected.HintsInclude);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\\")]
    public void Returns_an_empty_parse_for_a_path_that_names_nothing(string path)
    {
        var result = SoulseekFilenameParser.Parse(path);

        result.Parsed.Artist.Should().BeNull();
        result.Parsed.Title.Should().BeNull();
        result.Parsed.Album.Should().BeNull();
        result.Parsed.TrackNo.Should().BeNull();
        result.Parsed.VersionFlags.Should().Be(VersionFlags.None);
        result.Parsed.PathVersionFlags.Should().Be(VersionFlags.None);
        result.Parsed.FeaturedArtists.Should().BeEmpty();
        result.Extension.Should().BeNull();
        result.IsAudio.Should().BeFalse();
    }

    [Fact]
    public void Reads_a_path_with_forward_slashes_like_the_one_with_backslashes()
    {
        const string Backslashes = @"@@abcde\Music\Daft Punk\Random Access Memories\08 Get Lucky.mp3";

        var result = SoulseekFilenameParser.Parse("@@abcde/Music/Daft Punk/Random Access Memories/08 Get Lucky.mp3");

        result.Extension.Should().Be("mp3");
        result.IsAudio.Should().BeTrue();
        result.Parsed.Artist.Should().Be("Daft Punk");
        result.Parsed.Title.Should().Be("Get Lucky");
        result.Parsed.Album.Should().Be("Random Access Memories");
        result.Parsed.TrackNo.Should().Be(8);
        SoulseekFilenameParser.Parse(Backslashes).Parsed.Should().BeEquivalentTo(result.Parsed);
    }

    [Fact]
    public void Reads_every_file_of_the_recorded_search_responses()
    {
        var files = Directory.GetFiles(
            Path.Combine(SoulseekFixtures.FixtureDirectory, "slskd", "live"),
            "search-responses*.json");

        files.Should().HaveCountGreaterThanOrEqualTo(2);

        var audioFiles = 0;

        foreach (var file in files)
        {
            var responses = SoulseekFixtures.LoadFile<List<SearchResponse>>(file);

            foreach (var searchFile in responses.SelectMany(response => response.Files))
            {
                var result = SoulseekFilenameParser.Parse(searchFile.Filename);

                if (result.IsAudio)
                {
                    result.Parsed.Title.Should().NotBeNullOrWhiteSpace($"{searchFile.Filename} is an audio file");
                    audioFiles++;
                }
            }
        }

        audioFiles.Should().BeGreaterThan(0);
    }

    private sealed record FilenamesFixture(IReadOnlyList<FilenameCase> Cases);

    private sealed record FilenameCase(string Path, ExpectedName Expected);

    private sealed record ExpectedName(
        string? Artist,
        string? Title,
        string? Album,
        int? TrackNo,
        string? Extension,
        bool IsAudio,
        IReadOnlyList<string> Flags,
        IReadOnlyList<string> PathFlags,
        IReadOnlyList<string> Featured,
        bool UnexplainedBrackets,
        IReadOnlyList<string> HintsInclude);

    private sealed record SearchResponse(IReadOnlyList<SearchFile> Files);

    private sealed record SearchFile(string Filename);
}