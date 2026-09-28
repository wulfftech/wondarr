using System.Text.Json;
using FluentAssertions;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Decisions;

/// <summary>
/// Plays every case of <c>tests/fixtures/decisions.json</c> through the engine: the accepted ids in
/// exact order, the rejection reasons per rejected id, and the optional score and good-enough
/// expectations. The fixture is authoritative — a disagreement is a bug in the engine, not the file.
/// </summary>
public class DecisionEngineGoldenTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "decisions.json");

    private static readonly JsonSerializerOptions FixtureOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly GoldenFile Golden = Load();

    private static readonly char[] PathSeparators = ['\\', '/'];

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
        var candidates = testCase.Candidates.Select(BuildCandidate).ToList();

        // Two candidates of one case may share a blocklist key (the same file at two sizes), so the
        // id is mapped through the candidate object, not through its key.
        var keyById = new Dictionary<string, string>(StringComparer.Ordinal);
        var idByCandidate = new Dictionary<Candidate, string>(ReferenceEqualityComparer.Instance);

        for (var position = 0; position < candidates.Count; position++)
        {
            keyById[testCase.Candidates[position].Id] = candidates[position].BlocklistKey;
            idByCandidate[candidates[position]] = testCase.Candidates[position].Id;
        }

        var blocklisted = (testCase.Blocklisted ?? []).Select(id => keyById[id]).ToHashSet(StringComparer.Ordinal);
        var expectedRejections = testCase.Expected.Rejections ?? new Dictionary<string, List<string>>();

        var context = new DecisionContext
        {
            SongTitle = testCase.Song.Title,
            MainArtists = testCase.Song.MainArtists,
            SongDurationMs = testCase.Song.DurationMs,
            SongFlags = ToFlags(testCase.Song.Flags),
            AlbumTitle = testCase.Song.AlbumTitle,
            TrackNo = testCase.Song.TrackNo,
            Profile = ProfileFor(testCase.Profile),
            Qualities = SeedData.Qualities.ToDictionary(quality => quality.Id, quality => quality),
            CurrentFileQualityId = testCase.Song.CurrentQualityId,
            IsManualGrab = testCase.Manual,
            SourceTier = 1,
            IsBlocklisted = key => blocklisted.Contains(key),
            IgnoredUsers = (testCase.IgnoredUsers ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
            Reputation = (testCase.Reputation ?? [])
                .ToDictionary(
                    entry => entry.Key,
                    entry => new UserReputation(entry.Value.Successes, entry.Value.Failures, entry.Value.FailuresLast24H),
                    StringComparer.OrdinalIgnoreCase),
        };

        var decisions = new DecisionEngine().Evaluate(context, candidates);
        var accepted = decisions.Where(decision => decision.Accepted).Select(decision => idByCandidate[decision.Candidate]);
        var rejectedIds = decisions.Where(decision => !decision.Accepted).Select(decision => idByCandidate[decision.Candidate]);

        accepted.Should().Equal(testCase.Expected.Accepted, $"{name}: the accepted candidates, best first");
        rejectedIds.Should().BeEquivalentTo(expectedRejections.Keys, $"{name}: every rejected candidate is accounted for");

        foreach (var (id, reasons) in expectedRejections)
        {
            var decision = Decision(decisions, idByCandidate, id);

            decision.Rejections
                .Select(rejection => rejection.Reason.ToWireName())
                .Should()
                .BeEquivalentTo(reasons, $"{name}: the reasons {id} was rejected for");

            decision.Rejections.Should().OnlyContain(rejection => !string.IsNullOrWhiteSpace(rejection.Message));
        }

        foreach (var (id, minimum) in testCase.Expected.ScoreAtLeast ?? new Dictionary<string, int>())
        {
            Decision(decisions, idByCandidate, id).Score.Total.Should().BeGreaterThanOrEqualTo(minimum, $"{name}: {id}");
        }

        foreach (var (id, maximum) in testCase.Expected.ScoreAtMost ?? new Dictionary<string, int>())
        {
            Decision(decisions, idByCandidate, id).Score.Total.Should().BeLessThanOrEqualTo(maximum, $"{name}: {id}");
        }

        var engine = new DecisionEngine();

        foreach (var (id, expected) in testCase.Expected.GoodEnough ?? new Dictionary<string, bool>())
        {
            engine.IsGoodEnough(Decision(decisions, idByCandidate, id)).Should().Be(expected, $"{name}: {id}");
        }
    }

    private static CandidateDecision Decision(
        IReadOnlyList<CandidateDecision> decisions,
        Dictionary<Candidate, string> idByCandidate,
        string id) =>
        decisions.Single(decision => idByCandidate[decision.Candidate] == id);

    private static Candidate BuildCandidate(CandidateSpec spec)
    {
        var parsed = spec.Parsed;

        return new Candidate
        {
            SourceType = SourceTypes.Soulseek,
            BlocklistKey = BlocklistKeys.Soulseek(spec.Provider, spec.RemotePath),
            DisplayName = LastSegment(spec.RemotePath),
            RemotePath = spec.RemotePath,
            Provider = spec.Provider,
            QualityId = spec.QualityId,
            Extension = spec.Extension,
            DurationMs = spec.DurationMs,
            BitrateKbps = spec.BitrateKbps,
            IsVariableBitrate = spec.IsVariableBitrate,
            SampleRate = spec.SampleRate,
            BitDepth = spec.BitDepth,
            SizeBytes = spec.SizeBytes,
            IsLocked = spec.IsLocked,
            Availability = new CandidateAvailability(
                spec.Availability.FreeUploadSlot,
                spec.Availability.QueueLength,
                spec.Availability.UploadSpeedBytesPerSecond),
            Parsed = new ParsedName(
                parsed.Artist,
                parsed.Title,
                parsed.Album,
                parsed.TrackNo,
                ToFlags(parsed.Flags),
                parsed.Hints,
                ToFlags(parsed.PathFlags),
                parsed.Featured,
                parsed.UnexplainedBrackets),
        };
    }

    private static QualityProfile ProfileFor(string name) => name switch
    {
        "standard" => SeedData.QualityProfiles.Single(profile => profile.Id == SeedData.StandardProfileId),
        "lossless" => SeedData.QualityProfiles.Single(profile => profile.Id == SeedData.LosslessProfileId),
        _ => throw new InvalidOperationException($"Unknown profile '{name}' in the decisions fixture."),
    };

    private static VersionFlags ToFlags(IEnumerable<string> names)
    {
        var flags = VersionFlags.None;

        foreach (var name in names)
        {
            VersionFlagNames.TryParse(name, out var parsed).Should().BeTrue($"'{name}' is a version flag wire name");
            flags |= parsed;
        }

        return flags;
    }

    private static string LastSegment(string path)
    {
        var segments = path.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);

        return segments.Length == 0 ? path : segments[^1];
    }

    private static GoldenFile Load()
    {
        using var stream = File.OpenRead(FixturePath);

        return JsonSerializer.Deserialize<GoldenFile>(stream, FixtureOptions)
            ?? throw new InvalidOperationException("The decisions fixture is empty.");
    }

    /// <summary>The shape of <c>tests/fixtures/decisions.json</c>, and nothing more.</summary>
    private sealed record GoldenFile(List<GoldenCase> Cases);

    private sealed record GoldenCase(
        string Name,
        SongSpec Song,
        string Profile,
        List<CandidateSpec> Candidates,
        ExpectedSpec Expected,
        List<string>? Blocklisted = null,
        List<string>? IgnoredUsers = null,
        Dictionary<string, ReputationSpec>? Reputation = null,
        bool Manual = false);

    private sealed record SongSpec(
        string Title,
        List<string> MainArtists,
        int? DurationMs,
        List<string> Flags,
        string? AlbumTitle,
        int? TrackNo,
        long? CurrentQualityId = null);

    private sealed record CandidateSpec(
        string Id,
        string Provider,
        string RemotePath,
        string? Extension,
        ParsedSpec Parsed,
        AvailabilitySpec Availability,
        int? DurationMs = null,
        int? BitrateKbps = null,
        bool? IsVariableBitrate = null,
        int? SampleRate = null,
        int? BitDepth = null,
        long? SizeBytes = null,
        long QualityId = 1,
        bool IsLocked = false);

    private sealed record ParsedSpec(
        string? Artist,
        string? Title,
        string? Album,
        int? TrackNo,
        List<string> Flags,
        List<string> PathFlags,
        List<string> Hints,
        List<string> Featured,
        bool UnexplainedBrackets);

    private sealed record AvailabilitySpec(bool? FreeUploadSlot, int? QueueLength, long? UploadSpeedBytesPerSecond);

    private sealed record ReputationSpec(int Successes, int Failures, int FailuresLast24H);

    private sealed record ExpectedSpec(
        List<string> Accepted,
        Dictionary<string, List<string>>? Rejections = null,
        Dictionary<string, int>? ScoreAtLeast = null,
        Dictionary<string, int>? ScoreAtMost = null,
        Dictionary<string, bool>? GoodEnough = null);
}