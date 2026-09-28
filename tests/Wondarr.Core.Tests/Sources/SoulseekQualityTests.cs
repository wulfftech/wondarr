using FluentAssertions;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Sources;

/// <summary>
/// Golden tests for <see cref="SoulseekQuality.Infer"/> against <c>tests/fixtures/soulseek-quality.json</c>:
/// slskd's attributes in, a quality id from the seed out.
/// </summary>
public class SoulseekQualityTests
{
    private static readonly QualityFixture Fixture = SoulseekFixtures.Load<QualityFixture>("soulseek-quality.json");

    public static TheoryData<int, string> RecordedResults()
    {
        var data = new TheoryData<int, string>();

        for (var index = 0; index < Fixture.Cases.Count; index++)
        {
            data.Add(index, Fixture.Cases[index].Note);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RecordedResults))]
    public void Infers_the_quality_of_a_recorded_result(int index, string note)
    {
        var testCase = Fixture.Cases[index];

        var quality = SoulseekQuality.Infer(
            testCase.Extension,
            testCase.BitRate,
            testCase.IsVariableBitRate,
            testCase.SampleRate,
            testCase.BitDepth,
            testCase.LengthSeconds,
            testCase.SizeBytes);

        quality.Should().Be(testCase.ExpectedQualityId, note);
    }

    [Fact]
    public void Returns_unknown_for_an_extension_it_does_not_know()
    {
        SoulseekQuality.Infer("mp4", 320, null, 44100, null, 300, 30000000).Should().Be(1);
        SoulseekQuality.Infer(".aiff", null, null, 44100, 16, 300, 52920000).Should().Be(43);
    }

    private sealed record QualityFixture(IReadOnlyList<QualityCase> Cases);

    private sealed record QualityCase(
        string? Extension,
        int? BitRate,
        bool? IsVariableBitRate,
        int? SampleRate,
        int? BitDepth,
        int? LengthSeconds,
        long? SizeBytes,
        long ExpectedQualityId,
        string Note);
}