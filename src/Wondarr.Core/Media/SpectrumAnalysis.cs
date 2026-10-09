namespace Wondarr.Core.Media;

/// <summary>How the spectrum of a lossless file reads (DECISIONS build session 10 #2).</summary>
public enum SpectralOutcome
{
    /// <summary>No lossy brick-wall low-pass: the file may be what it claims.</summary>
    Genuine,

    /// <summary>A brick-wall low-pass below 20 kHz: the audio went through a lossy encoder.</summary>
    Lossy,

    /// <summary>Too little signal to tell.</summary>
    Inconclusive,
}

/// <summary>The result of a spectral check.</summary>
/// <param name="Outcome">What the spectrum says.</param>
/// <param name="CutoffHz">The centre of the band where the cliff was found, or <c>null</c> when there is none.</param>
/// <param name="AnalysedFrames">How many loud frames the power spectrum averaged.</param>
public sealed record SpectralVerdict(SpectralOutcome Outcome, double? CutoffHz, int AnalysedFrames);

/// <summary>
/// Looks for the brick-wall low-pass every lossy encoder leaves in a spectrum: a Welch power
/// spectrum over mono samples, grouped into 250 Hz bands, searched from the top for a drop of at
/// least 25 dB. Pure arithmetic, no I/O; the constants are the ones the measurement on real FLACs and
/// their MP3 re-encodes settled on, so tests name them rather than repeat them.
/// </summary>
public static class SpectrumAnalysis
{
    /// <summary>The FFT frame length, in samples.</summary>
    internal const int FrameLength = 4096;

    /// <summary>The distance between frame starts: half overlap.</summary>
    internal const int Hop = 2048;

    /// <summary>The RMS below which a frame counts as silence, in linear full scale (-60 dBFS).</summary>
    internal const double SilenceRms = 0.001;

    /// <summary>Fewer loud frames than this and the file is not judged.</summary>
    internal const int MinimumFrames = 50;

    /// <summary>The width of one band, in Hz.</summary>
    internal const double BandWidthHz = 250.0;

    /// <summary>The scan stops at the first band whose centre is below this, in Hz.</summary>
    internal const double ScanFloorHz = 10_000.0;

    /// <summary>How many bands under the candidate are averaged.</summary>
    internal const int BandsBelow = 4;

    /// <summary>The drop, in dB, that makes a cliff.</summary>
    internal const double CliffDb = 25.0;

    /// <summary>A cliff below this frequency is a lossy encoder's low-pass, in Hz.</summary>
    internal const double LossyBelowHz = 20_000.0;

    /// <summary>Added to a band's power so silence does not take a logarithm of zero.</summary>
    internal const double PowerFloor = 1e-30;

    private const int Bins = FrameLength / 2;

    /// <summary>Analyses <paramref name="monoSamples"/> for a lossy low-pass.</summary>
    /// <param name="monoSamples">Mono samples in the range -1 to 1.</param>
    /// <param name="sampleRate">The sample rate in Hz.</param>
    /// <returns>The verdict, with the cutoff when a cliff was found.</returns>
    public static SpectralVerdict Analyse(ReadOnlySpan<float> monoSamples, int sampleRate = 44100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var power = new double[Bins];
        var frames = Welch(monoSamples, power);

        if (frames < MinimumFrames)
        {
            return new SpectralVerdict(SpectralOutcome.Inconclusive, null, frames);
        }

        var binHz = (double)sampleRate / FrameLength;
        var bandCount = (int)Math.Floor(sampleRate / 2.0 / BandWidthHz);
        var sums = new double[bandCount];
        var counts = new int[bandCount];

        for (var bin = 0; bin < Bins; bin++)
        {
            var band = (int)(bin * binHz / BandWidthHz);
            if (band >= bandCount)
            {
                break;
            }

            sums[band] += power[bin] / frames;
            counts[band]++;
        }

        var levels = new double[bandCount];
        for (var band = 0; band < bandCount; band++)
        {
            var mean = counts[band] == 0 ? 0.0 : sums[band] / counts[band];
            levels[band] = 10.0 * Math.Log10(mean + PowerFloor);
        }

        // The highest level of everything above each band, filled from the top.
        var aboveMax = new double[bandCount];
        var running = double.NegativeInfinity;
        for (var band = bandCount - 1; band >= 0; band--)
        {
            aboveMax[band] = running;
            running = Math.Max(running, levels[band]);
        }

        // The top band has nothing above it to compare with, so the scan starts one below.
        for (var band = bandCount - 2; band >= BandsBelow; band--)
        {
            var centre = (band + 0.5) * BandWidthHz;
            if (centre < ScanFloorHz)
            {
                break;
            }

            var below = 0.0;
            for (var offset = 1; offset <= BandsBelow; offset++)
            {
                below += levels[band - offset];
            }

            below /= BandsBelow;

            if (below - aboveMax[band] >= CliffDb)
            {
                return new SpectralVerdict(
                    centre < LossyBelowHz ? SpectralOutcome.Lossy : SpectralOutcome.Genuine,
                    centre,
                    frames);
            }
        }

        return new SpectralVerdict(SpectralOutcome.Genuine, null, frames);
    }

    /// <summary>
    /// Adds the power spectrum of every loud frame into <paramref name="power"/> and returns how many
    /// frames that was. One pair of buffers serves every frame.
    /// </summary>
    private static int Welch(ReadOnlySpan<float> samples, double[] power)
    {
        var window = new double[FrameLength];
        for (var index = 0; index < FrameLength; index++)
        {
            window[index] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * index / FrameLength));
        }

        var cos = new double[Bins];
        var sin = new double[Bins];
        for (var index = 0; index < Bins; index++)
        {
            var angle = -2.0 * Math.PI * index / FrameLength;
            cos[index] = Math.Cos(angle);
            sin[index] = Math.Sin(angle);
        }

        var re = new double[FrameLength];
        var im = new double[FrameLength];
        var frames = 0;

        for (var start = 0; start + FrameLength <= samples.Length; start += Hop)
        {
            var frame = samples.Slice(start, FrameLength);

            var energy = 0.0;
            foreach (var sample in frame)
            {
                energy += (double)sample * sample;
            }

            if (Math.Sqrt(energy / FrameLength) < SilenceRms)
            {
                continue;
            }

            for (var index = 0; index < FrameLength; index++)
            {
                re[index] = frame[index] * window[index];
                im[index] = 0.0;
            }

            Fft(re, im, cos, sin);

            for (var bin = 0; bin < Bins; bin++)
            {
                power[bin] += (re[bin] * re[bin]) + (im[bin] * im[bin]);
            }

            frames++;
        }

        return frames;
    }

    /// <summary>An in-place iterative radix-2 FFT of <see cref="FrameLength"/> points.</summary>
    private static void Fft(double[] re, double[] im, double[] cos, double[] sin)
    {
        const int N = FrameLength;

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < N; i++)
        {
            var bit = N >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;

            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var length = 2; length <= N; length <<= 1)
        {
            var half = length >> 1;
            var step = N / length;

            for (var start = 0; start < N; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = cos[k * step];
                    var wi = sin[k * step];
                    var even = start + k;
                    var odd = even + half;
                    var tr = (re[odd] * wr) - (im[odd] * wi);
                    var ti = (re[odd] * wi) + (im[odd] * wr);

                    re[odd] = re[even] - tr;
                    im[odd] = im[even] - ti;
                    re[even] += tr;
                    im[even] += ti;
                }
            }
        }
    }
}
