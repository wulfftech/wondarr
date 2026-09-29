using System.Text.Json;
using Wondarr.Core.Sources;
using Wondarr.Sources.Slskd;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>
/// The mapper over the responses recorded from the live network (<c>tests/fixtures/slskd/live/</c>):
/// every audio file becomes a candidate, nothing else does, and the fields come straight from what
/// the peer and the path said.
/// </summary>
public class SoulseekCandidateMapperTests
{
    private const string Query = "daft punk get lucky";

    private const string FlacPath =
        @"@@qkrmw\Music\Flac Files\Chart\Australian ARIA Charts\Australian ARIA chart Top 100 - 2010-2019\Australian ARIA Chart Top 100 2013\007 - Daft Punk feat. Pharrell Williams - Get Lucky.flac";

    private const string Mp3Path =
        @"@@rvepj\Music\Daft Punk-Random Access Memories (2013) V0\08 - Get Lucky.mp3";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Maps_every_audio_file_and_drops_everything_else()
    {
        var responses = RecordedResponses();

        var expected = responses
            .SelectMany(response => response.Files.Select(file => (response.Username, file.Filename)))
            .Where(pair => SoulseekFilenameParser.Parse(pair.Filename).IsAudio)
            .Select(pair => BlocklistKeys.Soulseek(pair.Username, pair.Filename))
            .Distinct(StringComparer.Ordinal)
            .Count();

        var candidates = SoulseekCandidateMapper.Map(responses, Query);

        expected.Should().BeGreaterThan(0);
        candidates.Should().HaveCount(expected);

        candidates
            .Where(candidate => candidate.Extension is null or "lrc" or "cue" or "jpg" or "log" or "sfk")
            .Should().BeEmpty();
    }

    [Fact]
    public void Does_not_map_locked_files()
    {
        var responses = RecordedResponses();
        var locked = responses.Where(response => response.LockedFiles.Count > 0).ToList();

        locked.Should().NotBeEmpty();
        locked.SelectMany(response => response.LockedFiles).Should().Contain(file => file.Filename.EndsWith(".m4a"));

        var candidates = SoulseekCandidateMapper.Map(responses, Query);

        candidates.Select(candidate => candidate.Provider).Should().NotBeEmpty().And.NotContain("peer109");
    }

    [Fact]
    public void Gives_every_candidate_a_unique_blocklist_key()
    {
        var candidates = SoulseekCandidateMapper.Map(RecordedResponses(), Query);

        candidates.Select(candidate => candidate.BlocklistKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Records_the_query_that_found_the_candidate()
    {
        var candidates = SoulseekCandidateMapper.Map(RecordedResponses(), Query);

        candidates.Should().OnlyContain(candidate => candidate.Query == Query);
    }

    [Fact]
    public void Maps_a_flac_candidate_from_what_the_peer_reported()
    {
        var candidate = SoulseekCandidateMapper.Map(RecordedResponses(), Query)
            .Single(entry => entry.RemotePath == FlacPath);

        candidate.SourceType.Should().Be(SourceTypes.Soulseek);
        candidate.Provider.Should().Be("peer005");
        candidate.BlocklistKey.Should().Be(BlocklistKeys.Soulseek("peer005", FlacPath));
        candidate.DisplayName.Should().Be("007 - Daft Punk feat. Pharrell Williams - Get Lucky.flac");
        candidate.RemotePath.Should().Be(FlacPath);
        candidate.Extension.Should().Be("flac");
        candidate.DurationMs.Should().Be(249_000);
        candidate.BitrateKbps.Should().BeNull();
        candidate.SampleRate.Should().Be(44100);
        candidate.BitDepth.Should().Be(16);
        candidate.IsVariableBitrate.Should().BeNull();
        candidate.SizeBytes.Should().Be(30_038_322);
        candidate.QualityId.Should().Be(36);
        candidate.IsLocked.Should().BeFalse();
        candidate.Availability.FreeUploadSlot.Should().BeTrue();
        candidate.Availability.QueueLength.Should().Be(17);
        candidate.Availability.UploadSpeedBytesPerSecond.Should().Be(1_146_398);
        candidate.Parsed.Title.Should().Be("Get Lucky");
        candidate.Parsed.Artist.Should().Be("Daft Punk");
    }

    [Fact]
    public void Maps_an_mp3_candidate_from_what_the_peer_reported()
    {
        var candidate = SoulseekCandidateMapper.Map(RecordedResponses(), Query)
            .Single(entry => entry.RemotePath == Mp3Path);

        candidate.Provider.Should().Be("peer101");
        candidate.BlocklistKey.Should().Be(BlocklistKeys.Soulseek("peer101", Mp3Path));
        candidate.DisplayName.Should().Be("08 - Get Lucky.mp3");
        candidate.Extension.Should().Be("mp3");
        candidate.DurationMs.Should().Be(369_000);
        candidate.BitrateKbps.Should().Be(239);
        candidate.SampleRate.Should().BeNull();
        candidate.BitDepth.Should().BeNull();
        candidate.IsVariableBitrate.Should().BeNull();
        candidate.SizeBytes.Should().Be(11_207_349);
        candidate.QualityId.Should().Be(30);
        candidate.IsLocked.Should().BeFalse();
        candidate.Availability.FreeUploadSlot.Should().BeTrue();
        candidate.Availability.QueueLength.Should().Be(0);
        candidate.Availability.UploadSpeedBytesPerSecond.Should().Be(292_031);
    }

    private static List<SlskdSearchResponse> RecordedResponses() =>
        JsonSerializer.Deserialize<List<SlskdSearchResponse>>(
            SlskdTestData.ReadFixture(Path.Combine("live", "search-responses.json")),
            SerializerOptions)
        ?? throw new InvalidOperationException("The recorded responses could not be read");
}
