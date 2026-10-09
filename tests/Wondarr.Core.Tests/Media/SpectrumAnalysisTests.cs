using FluentAssertions;
using Wondarr.Core.Media;
using Xunit;
using Xunit.Abstractions;

namespace Wondarr.Core.Tests.Media;

/// <summary>
/// The spectral check on generated signals. A band-limited noise is built as one period of 8192
/// samples — a sum of cosines at every FFT bin up to the cut-off, each with a seeded random phase —
/// and tiled for 30 s, which gives an exact brick-wall low-pass without needing a whole-signal FFT.
/// </summary>
public sealed class SpectrumAnalysisTests
{
    private const int SampleRate = 44_100;
    private const int Period = 8192;
    private const int ThirtySeconds = 30 * SampleRate;

    private readonly ITestOutputHelper _output;

    public SpectrumAnalysisTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Full_band_noise_is_genuine()
    {
        var verdict = SpectrumAnalysis.Analyse(LowPassedNoise(cutoffHz: 22_000), SampleRate);

        _output.WriteLine(verdict.ToString());
        verdict.Outcome.Should().Be(SpectralOutcome.Genuine);
        verdict.AnalysedFrames.Should().BeGreaterThan(SpectrumAnalysis.MinimumFrames);
    }

    [Fact]
    public void Noise_low_passed_at_16_kHz_is_lossy_with_the_cutoff_found()
    {
        // 30 dB of residue above the cut-off: the drop is a cliff (25 dB or more) but the band
        // right above it is the first to qualify, so the cutoff is read within half a band.
        var verdict = SpectrumAnalysis.Analyse(LowPassedNoise(cutoffHz: 16_000, floorDb: -30), SampleRate);

        _output.WriteLine(verdict.ToString());
        verdict.Outcome.Should().Be(SpectralOutcome.Lossy);
        verdict.CutoffHz.Should().BeInRange(15_750, 16_250);
    }

    [Fact]
    public void A_perfect_brick_wall_at_16_kHz_is_lossy()
    {
        // Nothing at all above the cut-off. The scan runs from the top and the logarithm of an
        // empty band is very deep, so the cliff is first met up to four bands above the true edge:
        // the verdict is the same, the reported cutoff reads high.
        var verdict = SpectrumAnalysis.Analyse(LowPassedNoise(cutoffHz: 16_000), SampleRate);

        _output.WriteLine(verdict.ToString());
        verdict.Outcome.Should().Be(SpectralOutcome.Lossy);
        verdict.CutoffHz.Should().BeInRange(16_000, 17_250);
    }

    [Fact]
    public void Noise_low_passed_at_19_kHz_is_lossy()
    {
        var verdict = SpectrumAnalysis.Analyse(LowPassedNoise(cutoffHz: 19_000, floorDb: -60), SampleRate);

        _output.WriteLine(verdict.ToString());
        verdict.Outcome.Should().Be(SpectralOutcome.Lossy);
    }

    [Fact]
    public void Noise_low_passed_at_20_5_kHz_is_genuine()
    {
        var verdict = SpectrumAnalysis.Analyse(LowPassedNoise(cutoffHz: 20_500, floorDb: -60), SampleRate);

        _output.WriteLine(verdict.ToString());
        verdict.Outcome.Should().Be(SpectralOutcome.Genuine);
    }

    [Fact]
    public void A_gentle_roll_off_is_genuine()
    {
        // White noise through a one-pole low-pass at 8 kHz: a slope, not a cliff.
        var samples = LowPassedNoise(cutoffHz: 22_000);
        var coefficient = (float)Math.Exp(-2.0 * Math.PI * 8_000 / SampleRate);
        var state = 0f;

        for (var index = 0; index < samples.Length; index++)
        {
            state = (coefficient * state) + ((1 - coefficient) * samples[index]);
            samples[index] = state;
        }

        var verdict = SpectrumAnalysis.Analyse(samples, SampleRate);

        _output.WriteLine(verdict.ToString());
        verdict.Outcome.Should().Be(SpectralOutcome.Genuine);
    }

    [Fact]
    public void Silence_is_inconclusive()
    {
        var verdict = SpectrumAnalysis.Analyse(new float[ThirtySeconds], SampleRate);

        verdict.Outcome.Should().Be(SpectralOutcome.Inconclusive);
        verdict.AnalysedFrames.Should().Be(0);
    }

    [Fact]
    public void Too_few_loud_frames_is_inconclusive()
    {
        // 100 000 samples hold 48 frames of 4096 at a hop of 2048.
        var verdict = SpectrumAnalysis.Analyse(LowPassedNoise(cutoffHz: 16_000).AsSpan(0, 100_000), SampleRate);

        verdict.Outcome.Should().Be(SpectralOutcome.Inconclusive);
        verdict.AnalysedFrames.Should().BeLessThan(SpectrumAnalysis.MinimumFrames);
    }

    [Fact]
    public void Quiet_frames_are_skipped()
    {
        // A loud second followed by near-silence: only the loud frames count.
        var samples = LowPassedNoise(cutoffHz: 22_000);
        for (var index = SampleRate; index < samples.Length; index++)
        {
            samples[index] *= 1e-5f;
        }

        var verdict = SpectrumAnalysis.Analyse(samples, SampleRate);

        verdict.Outcome.Should().Be(SpectralOutcome.Inconclusive);
        verdict.AnalysedFrames.Should().BeLessThan((SampleRate / SpectrumAnalysis.Hop) + 4, "only the loud second and the frames straddling it count");
    }

    [Fact]
    public void Real_samples_are_classified_when_a_folder_is_named()
    {
        // Opt-in: WONDARR_SPECTRAL_SAMPLES names a folder of mono float32 LE, 44.1 kHz *.f32 files
        // named for what they are (-genuine, -mp3-128, ...). Skipped in CI.
        var folder = Environment.GetEnvironmentVariable("WONDARR_SPECTRAL_SAMPLES");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.f32").Order(StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(file);
            var samples = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(float));

            var verdict = SpectrumAnalysis.Analyse(samples, SampleRate);
            var name = Path.GetFileName(file);
            _output.WriteLine($"{name}: {verdict.Outcome}, cutoff {verdict.CutoffHz?.ToString("0") ?? "none"} Hz, {verdict.AnalysedFrames} frames");

            if (name.Contains("-mp3-128", StringComparison.Ordinal) || name.Contains("-mp3-192", StringComparison.Ordinal))
            {
                verdict.Outcome.Should().Be(SpectralOutcome.Lossy, name);
            }
            else if (name.Contains("-genuine", StringComparison.Ordinal)
                || name.Contains("-mp3-320", StringComparison.Ordinal)
                || name.Contains("-aac-256", StringComparison.Ordinal))
            {
                verdict.Outcome.Should().NotBe(SpectralOutcome.Lossy, name);
            }
        }
    }

    /// <summary>
    /// 30 s of seeded noise with a brick-wall low-pass: one period of cosines at every bin of an
    /// 8192-point grid up to <paramref name="cutoffHz"/>, tiled. With <paramref name="floorDb"/> set,
    /// every bin above the cut-off carries a cosine that many dB below the passband's.
    /// </summary>
    private static float[] LowPassedNoise(double cutoffHz, double? floorDb = null)
    {
        var random = new Random(20261009);
        var bins = Math.Min(Period / 2 - 1, (int)(cutoffHz * Period / SampleRate));
        var top = floorDb is null ? bins : (Period / 2) - 1;
        var floor = floorDb is { } db ? Math.Pow(10.0, db / 20.0) : 0.0;
        var phases = new double[top + 1];
        for (var bin = 1; bin <= top; bin++)
        {
            phases[bin] = random.NextDouble() * 2.0 * Math.PI;
        }

        var cycle = new double[Period];
        for (var bin = 1; bin <= top; bin++)
        {
            var amplitude = bin <= bins ? 1.0 : floor;
            for (var index = 0; index < Period; index++)
            {
                cycle[index] += amplitude * Math.Cos((2.0 * Math.PI * bin * index / Period) + phases[bin]);
            }
        }

        var peak = cycle.Max(Math.Abs);
        var samples = new float[ThirtySeconds];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = (float)(0.5 * cycle[index % Period] / peak);
        }

        return samples;
    }
}
