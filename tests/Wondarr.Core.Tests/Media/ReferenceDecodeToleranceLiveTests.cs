using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wondarr.Core.Media;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.AcoustId;
using Wondarr.Core.Verification;
using Xunit;

namespace Wondarr.Core.Tests.Media;

/// <summary>
/// A reference library's files are the user's own and are only read, so a handful of bad frames does not
/// make one unreadable; a download is held to a clean decode. These run the real ffmpeg on damaged MP3s
/// (<see cref="DamagedMp3"/>) and are skipped where it is not installed.
/// </summary>
public sealed class ReferenceDecodeToleranceLiveTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("wondarr-tolerance-").FullName;

    /// <summary>
    /// One damage: whole frames or bodies, and how many, spread 100 frames apart in a file of 30 s and
    /// 1 150 frames.
    /// </summary>
    public static TheoryData<string, bool, int> FewBadFrames => new()
    {
        // The two files the live check left unreadable: one bad frame, and six "Header missing" frames.
        { "one bad frame", false, 1 },
        { "six bad frames", false, 6 },
        { "six frames without a header", true, 6 },
    };

    [FfmpegTheory]
    [MemberData(nameof(FewBadFrames))]
    public async Task A_full_length_file_with_a_few_bad_frames_is_readable_for_a_reference_scan_and_not_for_a_download(
        string name,
        bool wholeFrames,
        int count)
    {
        int[] frames = [.. Enumerable.Range(1, count).Select(index => index * 100)];
        var path = DamagedMp3.Write(_temp, "damaged.mp3", await DamagedMp3.CreateAsync(wholeFrames, frames));
        var probe = DamagedMp3.RealProbe();

        var download = await probe.ProbeAsync(path, CancellationToken.None);
        var reference = await probe.ProbeReferenceAsync(path, CancellationToken.None);

        download.Decodable.Should().BeFalse(name);
        download.Error.Should().NotBeNullOrEmpty();
        download.Tolerated.Should().BeNull();

        reference.Decodable.Should().BeTrue(name);
        reference.Info.Should().NotBeNull();
        reference.Info!.DurationMs.Should().BeInRange(29_900, 30_100);
        reference.Tolerated.Should().NotBeNull();
        reference.Tolerated!.Errors.Should().BeInRange(1, MediaProbe.ReferenceMaxErrorLines);
        reference.Tolerated.DecodedSeconds.Should().BeGreaterThanOrEqualTo(30 * MediaProbe.ReferenceMinDecodedFraction);
        reference.Tolerated.DurationSeconds.Should().BeApproximately(30, 0.1);
    }

    [FfmpegFact]
    public async Task A_file_with_more_than_twenty_error_lines_is_unreadable_for_a_reference_scan()
    {
        // Thirty damaged frames: two or three error lines each.
        int[] frames = [.. Enumerable.Range(0, 30).Select(index => 100 + (index * 30))];
        var path = DamagedMp3.Write(_temp, "ruined.mp3", await DamagedMp3.CreateAsync(wholeFrames: false, frames));
        var probe = DamagedMp3.RealProbe();

        var download = await probe.ProbeAsync(path, CancellationToken.None);
        var reference = await probe.ProbeReferenceAsync(path, CancellationToken.None);

        reference.Decodable.Should().BeFalse();
        reference.Info.Should().BeNull();
        reference.Tolerated.Should().BeNull();
        reference.Error.Should().NotBeNullOrEmpty("the verdict is the strict one, with its reason");
        download.Decodable.Should().BeFalse();
    }

    [FfmpegFact]
    public async Task A_file_cut_short_after_its_first_error_is_unreadable_for_a_reference_scan()
    {
        // One bad frame early on, and then the file ends half-way: the header still says 30 s.
        var damaged = await DamagedMp3.CreateAsync(wholeFrames: false, 100);
        var path = DamagedMp3.Write(_temp, "cut.mp3", DamagedMp3.Truncate(damaged));

        var reference = await DamagedMp3.RealProbe().ProbeReferenceAsync(path, CancellationToken.None);

        reference.Decodable.Should().BeFalse("ffmpeg got through about half of the 30 s the file claims");
        reference.Tolerated.Should().BeNull();
    }

    [FfmpegFact]
    public async Task A_file_with_no_audio_stream_is_unreadable_for_a_reference_scan()
    {
        var path = DamagedMp3.Write(
            _temp,
            "cover.mp3",
            await File.ReadAllBytesAsync(MediaFixtures.File(Path.Combine("covers", "cover-300x300.jpg"))));
        var probe = DamagedMp3.RealProbe();

        var reference = await probe.ProbeReferenceAsync(path, CancellationToken.None);

        reference.Decodable.Should().BeFalse();
        reference.Error.Should().Be("the file has no audio stream");
        (await probe.ProbeAsync(path, CancellationToken.None)).Decodable.Should().BeFalse();
    }

    [FfmpegFact]
    public async Task A_clean_file_is_answered_the_same_for_a_reference_scan_and_a_download()
    {
        var probe = DamagedMp3.RealProbe();
        var path = MediaFixtures.File("tone-320.mp3");

        var download = await probe.ProbeAsync(path, CancellationToken.None);
        var reference = await probe.ProbeReferenceAsync(path, CancellationToken.None);

        reference.Should().Be(download);
        reference.Decodable.Should().BeTrue();
        reference.Tolerated.Should().BeNull();
    }

    [FfmpegFact]
    public async Task The_download_verifier_still_rejects_a_file_with_one_bad_frame()
    {
        var path = DamagedMp3.Write(_temp, "download.mp3", await DamagedMp3.CreateAsync(wholeFrames: false, 300));
        var fingerprinter = Substitute.For<IFingerprinter>();
        var client = Substitute.For<IAcoustIdClient>();

        var verifier = new DownloadVerifier(
            DamagedMp3.RealProbe(),
            fingerprinter,
            client,
            new TestOptionsMonitor<AcoustIdOptions>(new AcoustIdOptions { ClientKey = "test-key-0123456789" }),
            NullLogger<DownloadVerifier>.Instance);

        var result = await verifier.VerifyAsync(
            new VerificationRequest(path, null, "Electric Avenue", ["Eddy Grant"], 30_000, VersionFlags.None, 3000),
            CancellationToken.None);

        result.Outcome.Should().Be(VerificationOutcome.Failed);
        result.Reason.Should().StartWith("Not decodable: ");
        fingerprinter.ReceivedCalls().Should().BeEmpty();
        client.ReceivedCalls().Should().BeEmpty();
    }

    [FfmpegTheory(fpcalc: true)]
    [MemberData(nameof(FewBadFrames))]
    public async Task A_tolerated_file_fingerprints_like_any_other(string name, bool wholeFrames, int count)
    {
        // fpcalc stops at the first bad frame of these; the fingerprinter's second try, through ffmpeg, does not.
        int[] frames = [.. Enumerable.Range(1, count).Select(index => index * 100)];
        var path = DamagedMp3.Write(_temp, "damaged.mp3", await DamagedMp3.CreateAsync(wholeFrames, frames));

        var reference = await DamagedMp3.RealProbe().ProbeReferenceAsync(path, CancellationToken.None);
        reference.Decodable.Should().BeTrue(name);

        var fingerprint = await DamagedMp3.RealFingerprinter().FingerprintAsync(
            path,
            FingerprintWindow.Start,
            reference.Info!.DurationMs,
            CancellationToken.None);

        fingerprint.Success.Should().BeTrue(fingerprint.Error);
        fingerprint.Fingerprint.Should().NotBeNullOrEmpty();
        fingerprint.Window.Should().Be(FingerprintWindow.Start);
        fingerprint.DurationSeconds.Should().Be(30);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder under the OS temp directory is harmless.
        }
    }
}
