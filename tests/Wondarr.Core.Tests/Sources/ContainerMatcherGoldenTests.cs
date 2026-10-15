using System.Text.Json;
using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Sources;

/// <summary>
/// Plays every case of <c>tests/fixtures/container-matches.json</c> through <see cref="ContainerMatcher"/>:
/// scene-style file names (<c>NN-artist_name-track_title[-group].ext</c>) that the ordinary file name
/// parser cannot split, and the files that must not be taken for the song. The fixture is authoritative.
/// </summary>
public class ContainerMatcherGoldenTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "container-matches.json");

    private static readonly JsonSerializerOptions FixtureOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly GoldenFile Golden = Load();

    public static TheoryData<int, string> Cases()
    {
        var data = new TheoryData<int, string>();

        for (var index = 0; index < Golden.Cases.Count; index++)
        {
            data.Add(index, Golden.Cases[index].Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_the_golden_case(int index, string name)
    {
        var testCase = Golden.Cases[index];
        var quality = SeedData.Qualities.Single(row => string.Equals(row.Name, testCase.Release.Quality, StringComparison.Ordinal));
        var files = testCase.Files
            .Select((file, position) => new ContainerFile(position, file.Path, file.Size ?? file.Seconds!.Value * file.Kbps!.Value * 1000L / 8))
            .ToList();
        var request = new ContainerMatchRequest(
            testCase.Song.Title,
            VersionFlagNames.FromNames(testCase.Song.Flags),
            testCase.Song.TrackNo,
            testCase.Song.DurationMs,
            quality.Id,
            testCase.Release.SixteenBitStated,
            testCase.Release.Artist,
            testCase.Release.Group);

        var match = ContainerMatcher.Find(files, request);

        if (testCase.Expected is null)
        {
            match.Should().BeNull(name);

            return;
        }

        match.Should().NotBeNull(name);
        match!.File.Path.Should().Be(testCase.Expected, name);

        if (testCase.ExpectedTitle is not null)
        {
            match.Parsed.Title.Should().Be(testCase.ExpectedTitle, name);
        }
    }

    [Fact]
    public void The_fixture_holds_cases_that_find_a_file_and_cases_that_find_none()
    {
        Golden.Cases.Should().Contain(testCase => testCase.Expected != null);
        Golden.Cases.Should().Contain(testCase => testCase.Expected == null);
    }

    private static GoldenFile Load()
    {
        using var stream = File.OpenRead(FixturePath);

        return JsonSerializer.Deserialize<GoldenFile>(stream, FixtureOptions)
            ?? throw new InvalidOperationException("container-matches.json is empty.");
    }

    private sealed record GoldenFile(List<CaseSpec> Cases);

    private sealed record CaseSpec(
        string Name,
        SongSpec Song,
        ReleaseSpec Release,
        List<FileSpec> Files,
        string? Expected,
        string? ExpectedTitle = null);

    private sealed record SongSpec(string Title, List<string> Flags, int? TrackNo, int? DurationMs);

    private sealed record ReleaseSpec(string Quality, bool SixteenBitStated, string? Artist, string? Group);

    private sealed record FileSpec(string Path, long? Size = null, int? Seconds = null, int? Kbps = null);
}
