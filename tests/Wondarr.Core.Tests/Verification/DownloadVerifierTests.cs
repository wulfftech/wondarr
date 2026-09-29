using System.Text.Json;
using Wondarr.Core.Media;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.AcoustId;
using Wondarr.Core.Verification;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Wondarr.Core.Tests.Verification;

/// <summary>
/// The verification rules of MATCHING_ENGINE.md §6.5: every branch of the probe, duration, fingerprint
/// and AcoustID steps, so that "zero wrong-recording imports" holds when the file name lied.
/// </summary>
public sealed class DownloadVerifierTests
{
    private const string GetLuckyId = "833f00e1-781f-4edd-90e4-e52712618862";
    private const string DuplicateId = "b7c6d5e4-f3a2-4b1c-9d8e-7f6a5b4c3d2e";
    private const string LiveTakeId = "0f5c8b1e-6a3f-4c0a-8d8e-2b6f1a9c7d33";

    private static readonly MediaInfo Mp3 = new(
        Codec: "mp3",
        Container: "mp3",
        BitrateKbps: 320,
        SampleRate: 44100,
        BitDepth: null,
        Channels: 2,
        DurationMs: 369000,
        IsLossless: false,
        SizeBytes: 9_000_000);

    [Fact]
    public async Task Fails_when_the_file_does_not_decode()
    {
        var (verifier, _, fingerprinter, client) = Create(probe: new MediaProbeResult(false, null, "Invalid data found"));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Failed);
        result.Reason.Should().Be("Not decodable: Invalid data found");
        result.MeasuredQualityId.Should().BeNull();
        fingerprinter.Windows.Should().BeEmpty();
        client.Lookups.Should().BeEmpty();
    }

    [Fact]
    public async Task Fails_when_the_duration_is_off()
    {
        var (verifier, _, fingerprinter, _) = Create(probe: Probed(Mp3 with { DurationMs = 248000 }));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Failed);
        result.Reason.Should().Be("Duration 248 s is 121 s off the song's 369 s (tolerance 3 s)");
        result.Media.Should().NotBeNull();
        fingerprinter.Windows.Should().BeEmpty();
    }

    [Fact]
    public async Task Fails_when_fpcalc_produces_no_fingerprint()
    {
        var (verifier, _, _, client) = Create(
            fingerprinter: new FakeFingerprinter(
                new FingerprintResult(false, null, 369, FingerprintWindow.Start, "fpcalc exited 1")));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Failed);
        result.Reason.Should().Be("Fingerprint failed: fpcalc exited 1");
        client.Lookups.Should().BeEmpty();
    }

    [Fact]
    public async Task Passes_unverified_when_acoustid_is_not_configured()
    {
        var (verifier, _, _, _) = Create(lookup: new AcoustIdLookupResult(AcoustIdStatus.NotConfigured, [], null));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Passed);
        result.FingerprintVerified.Should().BeFalse();
        result.Reason.Should().Be("AcoustID not configured; verified by probe and duration only");
    }

    [Theory]
    [InlineData(AcoustIdStatus.RateLimited)]
    [InlineData(AcoustIdStatus.Unavailable)]
    public async Task Defers_when_acoustid_cannot_answer(AcoustIdStatus status)
    {
        var (verifier, _, _, _) = Create(lookup: new AcoustIdLookupResult(status, [], null));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Deferred);
        result.Reason.Should().Be("AcoustID unavailable; will retry");
    }

    [Fact]
    public async Task Defers_when_acoustid_rejects_the_client_key()
    {
        var (verifier, _, _, _) = Create(lookup: new AcoustIdLookupResult(AcoustIdStatus.InvalidKey, [], "invalid API key"));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Deferred);
        result.Reason.Should().Be("AcoustID rejected the client key");
    }

    [Fact]
    public async Task Fails_when_acoustid_rejects_the_fingerprint()
    {
        var (verifier, _, _, _) = Create(
            lookup: new AcoustIdLookupResult(AcoustIdStatus.InvalidFingerprint, [], "invalid fingerprint"));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Failed);
        result.Reason.Should().Be("invalid fingerprint");
    }

    [Fact]
    public async Task Passes_when_the_songs_recording_is_the_one_that_matched()
    {
        var (verifier, _, _, _) = Create(lookup: Fixture("lookup-match.json"));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Passed);
        result.FingerprintVerified.Should().BeTrue();
        result.MatchedRecordingId.Should().Be(GetLuckyId);
        result.LearnedMbRecordingId.Should().BeNull();
        result.AcoustId.Should().Be("9ff43b6a-4f16-427c-93c2-92307ca505e0");
        result.FingerprintScore.Should().Be(0.962);
        result.MeasuredQualityId.Should().Be(MeasuredQuality.FromMediaInfo(Mp3));
        result.Media.Should().Be(Mp3);
    }

    [Fact]
    public async Task Needs_review_for_a_score_below_the_accept_threshold()
    {
        var (verifier, _, _, _) = Create(lookup: Fixture("lookup-low-score.json"));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.NeedsReview);
        result.Reason.Should().Be("Fingerprint score 0.61 is below 0.7");
        result.FingerprintVerified.Should().BeFalse();
    }

    [Fact]
    public async Task Fails_for_a_score_below_the_accept_threshold_when_strict()
    {
        var (verifier, _, _, _) = Create(lookup: Fixture("lookup-low-score.json"), strict: true);

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Failed);
        result.Reason.Should().Be("Fingerprint score 0.61 is below 0.7");
    }

    [Fact]
    public async Task Fails_when_the_fingerprint_belongs_to_another_recording()
    {
        var (verifier, _, fingerprinter, client) = Create(lookup: Fixture("lookup-other-recording.json"));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Failed);
        result.Reason.Should().Be(
            $"Fingerprint belongs to a different recording: Get Lucky (live) ({LiveTakeId}), score 0.95");

        // The start of the file named a different recording, so the middle of the file was tried once.
        fingerprinter.Windows.Should().Equal(FingerprintWindow.Start, FingerprintWindow.Middle);
        client.Lookups.Should().HaveCount(2);
    }

    [Fact]
    public async Task Rejects_a_live_recording_when_the_song_is_the_studio_take()
    {
        // Deezer-only song: nothing but the title, the artist, the flags and the length can decide.
        var (verifier, _, _, _) = Create(lookup: Fixture("lookup-other-recording.json"));

        var result = await verifier.VerifyAsync(
            Request(mbid: null, title: "Get Lucky"),
            CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Failed);
        result.Reason.Should().Contain("Get Lucky (live)");
        result.FingerprintVerified.Should().BeFalse();
    }

    [Fact]
    public async Task Passes_for_a_same_title_duplicate_without_changing_the_mbid()
    {
        var (verifier, _, _, _) = Create(lookup: Fixture("lookup-same-title-duplicate.json"));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Passed);
        result.FingerprintVerified.Should().BeTrue();
        result.MatchedRecordingId.Should().Be(DuplicateId);
        result.MatchedRecordingId.Should().NotBe(GetLuckyId);
        result.LearnedMbRecordingId.Should().BeNull();
        result.Reason.Should().Be($"Matched a same-titled recording {DuplicateId} (MusicBrainz duplicate)");
    }

    [Fact]
    public async Task Learns_the_mbid_for_a_deezer_only_song()
    {
        var (verifier, _, _, _) = Create(lookup: Fixture("lookup-same-title-duplicate.json"));

        var result = await verifier.VerifyAsync(Request(mbid: null), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Passed);
        result.FingerprintVerified.Should().BeTrue();
        result.LearnedMbRecordingId.Should().Be(DuplicateId);
        result.MatchedRecordingId.Should().BeNull();
    }

    [Fact]
    public async Task Passes_unverified_when_acoustid_does_not_know_the_fingerprint()
    {
        var (verifier, _, fingerprinter, _) = Create(lookup: Fixture("lookup-empty.json"));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Passed);
        result.FingerprintVerified.Should().BeFalse();
        result.Reason.Should().Be("Not in AcoustID; verified by probe and duration only");

        // Nothing was named at all, so there is nothing the middle window could correct.
        fingerprinter.Windows.Should().Equal(FingerprintWindow.Start);
    }

    [Fact]
    public async Task Passes_unverified_when_acoustid_knows_the_fingerprint_but_no_recording()
    {
        var (verifier, _, fingerprinter, _) = Create(lookup: Fixture("lookup-no-recordings.json"));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Passed);
        result.FingerprintVerified.Should().BeFalse();
        result.Reason.Should().Be("Not in AcoustID; verified by probe and duration only");
        fingerprinter.Windows.Should().Equal(FingerprintWindow.Start);
    }

    [Fact]
    public async Task Keeps_the_first_verdict_when_the_middle_window_is_no_better()
    {
        var (verifier, _, _, _) = Create(
            lookup: Fixture("lookup-other-recording.json"),
            secondLookup: Fixture("lookup-empty.json"));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        // An unknown middle fingerprint is not evidence that the start of the file was misread.
        result.Outcome.Should().Be(VerificationOutcome.Failed);
        result.Reason.Should().Contain("Get Lucky (live)");
    }

    [Fact]
    public async Task Keeps_the_middle_windows_verdict_when_it_matches()
    {
        var (verifier, _, fingerprinter, _) = Create(
            lookup: Fixture("lookup-other-recording.json"),
            secondLookup: Fixture("lookup-match.json"));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Passed);
        result.FingerprintVerified.Should().BeTrue();
        result.MatchedRecordingId.Should().Be(GetLuckyId);
        fingerprinter.Windows.Should().Equal(FingerprintWindow.Start, FingerprintWindow.Middle);
    }

    [Fact]
    public async Task The_middle_window_only_upgrades_a_verdict_to_a_verified_pass()
    {
        // The start names a live take (Failed); the middle gets a low score on the wanted MBID, which is
        // not a confirmation of anything and must not soften a positively identified wrong recording.
        var (verifier, _, fingerprinter, _) = Create(
            lookup: Fixture("lookup-other-recording.json"),
            secondLookup: Fixture("lookup-low-score.json"));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Failed);
        result.Reason.Should().Contain("Get Lucky (live)");
        result.FingerprintVerified.Should().BeFalse();
        fingerprinter.Windows.Should().Equal(FingerprintWindow.Start, FingerprintWindow.Middle);
    }

    [Fact]
    public async Task A_deezer_only_song_without_a_duration_is_held_against_the_probed_length()
    {
        // Deezer gave no length; the file's own duration is what the candidate recording is judged by.
        var (verifier, _, _, _) = Create(lookup: Fixture("lookup-same-title-duplicate.json"));

        var result = await verifier.VerifyAsync(Request(mbid: null, durationMs: null), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Passed);
        result.FingerprintVerified.Should().BeTrue();
        result.LearnedMbRecordingId.Should().Be(DuplicateId);
    }

    [Fact]
    public async Task A_deezer_only_song_without_a_duration_still_rejects_a_recording_of_another_length()
    {
        // The probed file is 240 s, the same-titled candidate is 369 s: not the same recording.
        var (verifier, _, _, _) = Create(
            probe: Probed(Mp3 with { DurationMs = 240_000 }),
            lookup: Fixture("lookup-same-title-duplicate.json"));

        var result = await verifier.VerifyAsync(Request(mbid: null, durationMs: null), CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Failed);
    }

    private static VerificationRequest Request(
        string? mbid = GetLuckyId,
        string title = "Get Lucky",
        int? durationMs = 369000) =>
        new("/downloads/Get Lucky.mp3", mbid, title, ["Daft Punk"], durationMs, VersionFlags.None, DurationToleranceMs: 3000);

    private static MediaProbeResult Probed(MediaInfo info) => new(true, info, null);

    private static FingerprintResult Print(FingerprintWindow window, string fingerprint) =>
        new(true, fingerprint, 369, window, null);

    private static (
        DownloadVerifier Verifier,
        FakeMediaProbe Probe,
        FakeFingerprinter Fingerprinter,
        FakeAcoustIdClient Client) Create(
        MediaProbeResult? probe = null,
        FakeFingerprinter? fingerprinter = null,
        AcoustIdLookupResult? lookup = null,
        AcoustIdLookupResult? secondLookup = null,
        bool strict = false)
    {
        var fakeProbe = new FakeMediaProbe(probe ?? Probed(Mp3));
        var fakeFingerprinter = fingerprinter ?? new FakeFingerprinter(
            Print(FingerprintWindow.Start, "FINGERPRINT-START"),
            Print(FingerprintWindow.Middle, "FINGERPRINT-MIDDLE"));

        var fakeClient = new FakeAcoustIdClient(
            lookup ?? Fixture("lookup-empty.json"),
            secondLookup ?? Fixture("lookup-empty.json"));

        var options = new AcoustIdOptions { ClientKey = "test-key-0123456789", Strict = strict };

        var verifier = new DownloadVerifier(
            fakeProbe,
            fakeFingerprinter,
            fakeClient,
            new StaticOptionsMonitor<AcoustIdOptions>(options),
            NullLogger<DownloadVerifier>.Instance);

        return (verifier, fakeProbe, fakeFingerprinter, fakeClient);
    }

    /// <summary>Reads one recorded AcoustID response into the shape the client would have produced.</summary>
    private static AcoustIdLookupResult Fixture(string name)
    {
        var path = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "acoustid", name);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var results = new List<AcoustIdResult>();

        foreach (var result in document.RootElement.GetProperty("results").EnumerateArray())
        {
            var recordings = new List<AcoustIdRecording>();

            if (result.TryGetProperty("recordings", out var elements))
            {
                foreach (var recording in elements.EnumerateArray())
                {
                    recordings.Add(new AcoustIdRecording(
                        recording.GetProperty("id").GetString()!,
                        recording.TryGetProperty("title", out var title) ? title.GetString() : null,
                        recording.TryGetProperty("duration", out var duration) ? duration.GetDouble() : null,
                        [.. recording.GetProperty("artists").EnumerateArray()
                            .Select(artist => artist.GetProperty("name").GetString()!)]));
                }
            }

            results.Add(new AcoustIdResult(
                result.GetProperty("id").GetString()!,
                result.GetProperty("score").GetDouble(),
                recordings));
        }

        return new AcoustIdLookupResult(AcoustIdStatus.Ok, results, null);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"Could not find tests/fixtures above {AppContext.BaseDirectory}");
    }

    /// <summary>A probe that answers with what the test queued.</summary>
    private sealed class FakeMediaProbe : IMediaProbe
    {
        private readonly MediaProbeResult _result;

        public FakeMediaProbe(MediaProbeResult result) => _result = result;

        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(_result);
    }

    /// <summary>A fingerprinter that hands out the queued fingerprints and records which window was asked for.</summary>
    private sealed class FakeFingerprinter : IFingerprinter
    {
        private readonly Queue<FingerprintResult> _results;

        public FakeFingerprinter(params FingerprintResult[] results) => _results = new Queue<FingerprintResult>(results);

        /// <summary>The windows asked for, in order.</summary>
        public List<FingerprintWindow> Windows { get; } = [];

        public Task<FingerprintResult> FingerprintAsync(
            string path,
            FingerprintWindow window,
            int trackDurationMs,
            CancellationToken cancellationToken)
        {
            Windows.Add(window);

            return Task.FromResult(_results.Count > 0
                ? _results.Dequeue()
                : new FingerprintResult(false, null, trackDurationMs / 1000, window, "nothing was queued"));
        }
    }

    /// <summary>An AcoustID client that hands out the queued lookups and records the fingerprints it was given.</summary>
    private sealed class FakeAcoustIdClient : IAcoustIdClient
    {
        private readonly Queue<AcoustIdLookupResult> _lookups;

        public FakeAcoustIdClient(params AcoustIdLookupResult[] lookups) => _lookups = new Queue<AcoustIdLookupResult>(lookups);

        /// <summary>The fingerprints asked about, in order.</summary>
        public List<string> Lookups { get; } = [];

        public Task<AcoustIdLookupResult> LookupAsync(
            string fingerprint,
            int durationSeconds,
            CancellationToken cancellationToken)
        {
            Lookups.Add(fingerprint);

            return Task.FromResult(_lookups.Count > 0
                ? _lookups.Dequeue()
                : new AcoustIdLookupResult(AcoustIdStatus.NotConfigured, [], null));
        }
    }

    /// <summary>An <see cref="IOptionsMonitor{T}"/> over one instance.</summary>
    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}