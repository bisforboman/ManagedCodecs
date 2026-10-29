using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ManagedCodecs.Aac.Sbr;

/// <summary>
/// One output channel through spectral band replication (ISO/IEC 14496-3 4.6.18): QMF analysis of the core decoder's
/// 1024 samples, HF generation and adjustment from its element's SBR data, and QMF synthesis of 2048 samples at twice
/// the core's rate (downsampled, of the lower 32 subbands into 1024 samples at the core's rate). Without SBR data (an
/// LFE, or an element before its first header) the high band stays empty: the core's signal through the same banks and
/// with the same delay.
/// </summary>
/// <remarks>
/// Times are in QMF time slots, 32 a frame (two per SBR time slot). The envelopes of a frame cover the slots from twice
/// its first border to twice its last, which can start up to 6 slots into the frame and end up to 6 past it; the high
/// band of slot l comes from the analysis' slot l + 2 (t_HFAdj), and the HF generator's prediction reaches 2 slots
/// further back, so 8 slots of the previous frame's analysis are kept (t_HFGen). Samples are processed in the 16-bit
/// range the spec's constants assume (the core's output times 32768).
/// </remarks>
internal sealed class SbrChannel(bool downsampled = false)
{
    private const int Slots = 32;
    private const int Kept = 8;
    private const int HfAdjust = 2;
    private const int Buffered = Slots + Kept;

    // The HF adjustment's limits (4.6.18.7.5): the gain limiter's gains (bs_limiter_gains: -3, 0, 3 dB, none) and
    // largest gain, the largest boost, and the energies' epsilons.
    private static readonly double[] LimiterGains = [0.70795, 1.0, 1.41254, 1e10];
    private const double MaxGain = 1e5;
    private const double MaxBoost = 1.584893192;
    private const double Epsilon = 1;
    private const double Epsilon0 = 1e-12;

    // The chirp factor a noise band's bs_invf_mode asks for, by the previous frame's mode (rows) and this one's (4.6.18.6.2).
    private static readonly double[][] ChirpFactors =
    [
        [0.0, 0.6, 0.9, 0.98],
        [0.6, 0.75, 0.9, 0.98],
        [0.0, 0.75, 0.9, 0.98],
        [0.0, 0.75, 0.9, 0.98],
    ];

    // The gain smoother's weights (h_smooth, 4.6.18.7.6), oldest slot first.
    private static readonly double[] Smoothing = [0.03183050093751, 0.11516383427084, 0.21816949906249, 0.30150283239582, 0.33333333333333];

    // The phase of an added sinusoid, by slot (the imaginary part's sign also alternating by subband).
    private static readonly double[] SineReal = [1, 0, -1, 0];
    private static readonly double[] SineImaginary = [0, 1, 0, -1];

    // The noise table's real and imaginary values apart, each 64 longer than the table (its start again), so that the
    // values of a row of up to 64 subbands are one run without the wrap at 512; and the sign of the sine's imaginary part
    // by subband (-1 at odd ones).
    private static readonly double[] NoiseReal = [.. Enumerable.Range(0, 512 + 64).Select(i => SbrNoiseTable.Values[2 * (i & 511)])];
    private static readonly double[] NoiseImaginary = [.. Enumerable.Range(0, 512 + 64).Select(i => SbrNoiseTable.Values[(2 * (i & 511)) + 1])];
    private static readonly double[] SineSign = [.. Enumerable.Range(0, 128).Select(k => (k & 1) != 0 ? -1.0 : 1)];

    private readonly QmfAnalysis _analysis = new();
    private readonly QmfSynthesis _synthesis = new(downsampled);

    // Parametric stereo, once it comes: its processor, the right channel's synthesis bank, and the frame's synthesis
    // input for the left (with the look-ahead slots) and the right.
    private static readonly PsData NoPs = new();
    private PsProcessor? _ps;
    private QmfSynthesis? _rightSynthesis;
    private double[][]? _stereoRe;
    private double[][]? _stereoIm;
    private double[][]? _rightRe;
    private double[][]? _rightIm;

    // X_Low: the analysis' subbands by slot, slots 0 to 7 the previous frame's last.
    private readonly double[][] _lowReal = Buffers(Buffered, 32);
    private readonly double[][] _lowImaginary = Buffers(Buffered, 32);

    // Y: the adjusted high band by envelope time slot, slots 0 to 7 the previous frame's 32 to 39, and the subband it
    // starts at in each slot (kx; 32 where there is none).
    private readonly double[][] _highReal = Buffers(Buffered, 64);
    private readonly double[][] _highImaginary = Buffers(Buffered, 64);
    private readonly int[] _highStart = [.. Enumerable.Repeat(32, Buffered)];

    // The HF generator's chirp factors and inverse filtering modes of the previous frame, by noise band.
    private readonly double[] _previousChirp = new double[5];
    private readonly int[] _previousInverseFiltering = new int[5];

    // The HF adjustment's state: where the previous frame had sinusoids (by subband), whether its last envelope ended
    // on a transient (l_APrev: 0 if so, else -1), the last 5 slots' gains and noise levels (from kx up, the newest last),
    // and the noise table's and the sinusoids' phase indices.
    private readonly bool[] _previousHarmonic = new bool[64];
    private int _previousTransient = -1;
    private readonly double[][] _gainHistory = Buffers(5, 64);
    private readonly double[][] _noiseHistory = Buffers(5, 64);
    private bool _startUp = true;
    private int _noiseIndex;
    private int _sineIndex;

    /// <summary>
    /// Takes one frame of the core's output for this channel (1024 samples in [-1, 1]) and writes 2048 samples at twice
    /// the rate (downsampled, 1024 at the core's): with <paramref name="channel"/> of <paramref name="element"/>'s SBR
    /// data when it has this frame's, else with no high band.
    /// </summary>
    public void Process(SbrElement? element, int channel, ReadOnlySpan<float> core, Span<float> output, AacStatistics statistics) =>
        Process(element, channel, core, output, [], statistics);

    /// <summary>
    /// As <see cref="Process(SbrElement?, int, ReadOnlySpan{float}, Span{float}, AacStatistics)"/>, and with parametric
    /// stereo when <paramref name="right"/> is not empty: the mono channel becomes <paramref name="output"/> (left) and
    /// <paramref name="right"/>, by the element's PS data (or its last).
    /// </summary>
    public void Process(SbrElement? element, int channel, ReadOnlySpan<float> core, Span<float> output, Span<float> right, AacStatistics statistics)
    {
        Span<double> input = stackalloc double[32];
        for (int slot = 0; slot < Slots; slot++)
        {
            for (int n = 0; n < 32; n++)
            {
                input[n] = core[(32 * slot) + n] * 32768.0;
            }

            _analysis.Process(input, _lowReal[Kept + slot], _lowImaginary[Kept + slot]);
        }

        if (element is { HasFrame: true, Header: { } header, Frequencies: { } frequencies })
        {
            var data = element.Channels[channel];
            if (element.Reset)
            {
                _startUp = true;
                _noiseIndex = 0;
            }

            Span<double> chirp = stackalloc double[5];
            UpdateChirp(data, frequencies, chirp, statistics);
            GenerateHighBand(data, frequencies, chirp);
            Adjust(data, header, frequencies, statistics);
        }

        if (right.IsEmpty)
        {
            Synthesize(output);
        }
        else
        {
            SynthesizeStereo(element, output, right, statistics);
        }

        MoveOn();
    }

    // 4.6.18.6.2: each noise band's chirp factor, from its bs_invf_mode, smoothed with the previous frame's.
    private void UpdateChirp(SbrChannelData data, SbrFrequencies frequencies, Span<double> chirp, AacStatistics statistics)
    {
        for (int i = 0; i < frequencies.NoiseBands; i++)
        {
            int mode = data.InverseFilteringMode[i];
            double target = ChirpFactors[_previousInverseFiltering[i]][mode];
            double previous = _previousChirp[i];
            double factor = target < previous ? (0.75 * target) + (0.25 * previous) : (0.90625 * target) + (0.09375 * previous);
            chirp[i] = factor < 0.015625 ? 0 : factor;
            _previousChirp[i] = chirp[i];
            _previousInverseFiltering[i] = mode;
            statistics.SbrChirpBands += chirp[i] > 0 ? 1 : 0;
        }
    }

    // 4.6.18.6: the high band of the frame's envelope slots, each patch copying low subbands up, filtered by the
    // subband's second-order linear prediction (from the covariance over 38 slots), scaled by its noise band's chirp
    // factor, to whiten what the core's tonality would make too tonal. Subbands past the last patch stay empty. The
    // predictions come for every source subband at once, and the slots are filled row by row.
    private void GenerateHighBand(SbrChannelData data, SbrFrequencies frequencies, ReadOnlySpan<double> chirp)
    {
        int first = 2 * data.Borders[0];
        int last = 2 * data.Borders[data.NumEnvelopes];
        int kx = frequencies.Kx;
        for (int l = first; l < last; l++)
        {
            _highReal[l].AsSpan(kx).Clear();
            _highImaginary[l].AsSpan(kx).Clear();
            _highStart[l] = kx;
        }

        int sources = 0;
        for (int p = 0; p < frequencies.PatchStart.Length; p++)
        {
            sources = Math.Max(sources, frequencies.PatchStart[p] + frequencies.PatchBands[p]);
        }

        // Up to a whole vector of subbands: the rows hold 32, and the ones past the last source go unused.
        Span<double> phi = stackalloc double[Covariance.Length];
        Covariances(_lowReal, _lowImaginary, Math.Min((sources + 3) & ~3, 32), phi);

        // Each destination subband's coefficients, scaled by its chirp factor (none where the factor is 0).
        Span<double> a0Real = stackalloc double[64];
        Span<double> a0Imaginary = stackalloc double[64];
        Span<double> a1Real = stackalloc double[64];
        Span<double> a1Imaginary = stackalloc double[64];
        int destination = kx;
        int noiseBand = 0;
        for (int p = 0; p < frequencies.PatchStart.Length; p++)
        {
            for (int x = 0; x < frequencies.PatchBands[p]; x++, destination++)
            {
                int source = frequencies.PatchStart[p] + x;
                while (noiseBand + 1 < frequencies.NoiseBands && destination >= frequencies.Noise[noiseBand + 1])
                {
                    noiseBand++;
                }

                double bw = chirp[noiseBand];
                var (r0, i0, r1, i1) = bw > 0 ? Prediction(phi, source) : default;
                a0Real[destination] = r0 * bw;
                a0Imaginary[destination] = i0 * bw;
                a1Real[destination] = r1 * (bw * bw);
                a1Imaginary[destination] = i1 * (bw * bw);
            }
        }

        for (int l = first; l < last; l++)
        {
            int t = l + HfAdjust;
            destination = kx;
            for (int p = 0; p < frequencies.PatchStart.Length; p++)
            {
                int count = frequencies.PatchBands[p];
                int source = frequencies.PatchStart[p];
                FilterPatch(
                    _lowReal[t].AsSpan(source, count),
                    _lowImaginary[t].AsSpan(source, count),
                    _lowReal[t - 1].AsSpan(source, count),
                    _lowImaginary[t - 1].AsSpan(source, count),
                    _lowReal[t - 2].AsSpan(source, count),
                    _lowImaginary[t - 2].AsSpan(source, count),
                    a0Real.Slice(destination, count),
                    a0Imaginary.Slice(destination, count),
                    a1Real.Slice(destination, count),
                    a1Imaginary.Slice(destination, count),
                    _highReal[l].AsSpan(destination, count),
                    _highImaginary[l].AsSpan(destination, count));
                destination += count;
            }
        }
    }

    // A patch's subbands of one slot: X_High = X_Low(l) + alpha0 X_Low(l - 1) + alpha1 X_Low(l - 2), complex, each
    // element's sums in the same order in the vector lanes as in the scalar tail.
    internal static void FilterPatch(
        ReadOnlySpan<double> re0,
        ReadOnlySpan<double> im0,
        ReadOnlySpan<double> re1,
        ReadOnlySpan<double> im1,
        ReadOnlySpan<double> re2,
        ReadOnlySpan<double> im2,
        ReadOnlySpan<double> a0Real,
        ReadOnlySpan<double> a0Imaginary,
        ReadOnlySpan<double> a1Real,
        ReadOnlySpan<double> a1Imaginary,
        Span<double> real,
        Span<double> imaginary)
    {
        int i = 0;
        int length = real.Length;
        if (Vector256.IsHardwareAccelerated && length >= 4 && re0.Length >= length && im0.Length >= length && re1.Length >= length
            && im1.Length >= length && re2.Length >= length && im2.Length >= length && a0Real.Length >= length
            && a0Imaginary.Length >= length && a1Real.Length >= length && a1Imaginary.Length >= length && imaginary.Length >= length)
        {
            ref double x0 = ref MemoryMarshal.GetReference(re0);
            ref double y0 = ref MemoryMarshal.GetReference(im0);
            ref double x1 = ref MemoryMarshal.GetReference(re1);
            ref double y1 = ref MemoryMarshal.GetReference(im1);
            ref double x2 = ref MemoryMarshal.GetReference(re2);
            ref double y2 = ref MemoryMarshal.GetReference(im2);
            ref double c0 = ref MemoryMarshal.GetReference(a0Real);
            ref double d0 = ref MemoryMarshal.GetReference(a0Imaginary);
            ref double c1 = ref MemoryMarshal.GetReference(a1Real);
            ref double d1 = ref MemoryMarshal.GetReference(a1Imaginary);
            ref double outRe = ref MemoryMarshal.GetReference(real);
            ref double outIm = ref MemoryMarshal.GetReference(imaginary);
            for (; i + 4 <= length; i += 4)
            {
                var r1 = Vector256.LoadUnsafe(ref x1, (nuint)i);
                var j1 = Vector256.LoadUnsafe(ref y1, (nuint)i);
                var r2 = Vector256.LoadUnsafe(ref x2, (nuint)i);
                var j2 = Vector256.LoadUnsafe(ref y2, (nuint)i);
                var ar0 = Vector256.LoadUnsafe(ref c0, (nuint)i);
                var ai0 = Vector256.LoadUnsafe(ref d0, (nuint)i);
                var ar1 = Vector256.LoadUnsafe(ref c1, (nuint)i);
                var ai1 = Vector256.LoadUnsafe(ref d1, (nuint)i);
                (Vector256.LoadUnsafe(ref x0, (nuint)i) + (ar0 * r1) - (ai0 * j1) + (ar1 * r2) - (ai1 * j2)).StoreUnsafe(ref outRe, (nuint)i);
                (Vector256.LoadUnsafe(ref y0, (nuint)i) + (ai0 * r1) + (ar0 * j1) + (ai1 * r2) + (ar1 * j2)).StoreUnsafe(ref outIm, (nuint)i);
            }
        }
        else if (!Vector256.IsHardwareAccelerated && Vector128.IsHardwareAccelerated && length >= 2 && re0.Length >= length && im0.Length >= length && re1.Length >= length
            && im1.Length >= length && re2.Length >= length && im2.Length >= length && a0Real.Length >= length
            && a0Imaginary.Length >= length && a1Real.Length >= length && a1Imaginary.Length >= length && imaginary.Length >= length)
        {
            ref double x0 = ref MemoryMarshal.GetReference(re0);
            ref double y0 = ref MemoryMarshal.GetReference(im0);
            ref double x1 = ref MemoryMarshal.GetReference(re1);
            ref double y1 = ref MemoryMarshal.GetReference(im1);
            ref double x2 = ref MemoryMarshal.GetReference(re2);
            ref double y2 = ref MemoryMarshal.GetReference(im2);
            ref double c0 = ref MemoryMarshal.GetReference(a0Real);
            ref double d0 = ref MemoryMarshal.GetReference(a0Imaginary);
            ref double c1 = ref MemoryMarshal.GetReference(a1Real);
            ref double d1 = ref MemoryMarshal.GetReference(a1Imaginary);
            ref double outRe = ref MemoryMarshal.GetReference(real);
            ref double outIm = ref MemoryMarshal.GetReference(imaginary);
            for (; i + 2 <= length; i += 2)
            {
                var r1 = Vector128.LoadUnsafe(ref x1, (nuint)i);
                var j1 = Vector128.LoadUnsafe(ref y1, (nuint)i);
                var r2 = Vector128.LoadUnsafe(ref x2, (nuint)i);
                var j2 = Vector128.LoadUnsafe(ref y2, (nuint)i);
                var ar0 = Vector128.LoadUnsafe(ref c0, (nuint)i);
                var ai0 = Vector128.LoadUnsafe(ref d0, (nuint)i);
                var ar1 = Vector128.LoadUnsafe(ref c1, (nuint)i);
                var ai1 = Vector128.LoadUnsafe(ref d1, (nuint)i);
                (Vector128.LoadUnsafe(ref x0, (nuint)i) + (ar0 * r1) - (ai0 * j1) + (ar1 * r2) - (ai1 * j2)).StoreUnsafe(ref outRe, (nuint)i);
                (Vector128.LoadUnsafe(ref y0, (nuint)i) + (ai0 * r1) + (ar0 * j1) + (ai1 * r2) + (ar1 * j2)).StoreUnsafe(ref outIm, (nuint)i);
            }
        }

        for (; i < real.Length; i++)
        {
            real[i] = re0[i] + (a0Real[i] * re1[i]) - (a0Imaginary[i] * im1[i]) + (a1Real[i] * re2[i]) - (a1Imaginary[i] * im2[i]);
            imaginary[i] = im0[i] + (a0Imaginary[i] * re1[i]) + (a0Real[i] * im1[i]) + (a1Imaginary[i] * re2[i]) + (a1Real[i] * im2[i]);
        }
    }

    /// <summary>Where each covariance of <see cref="Covariances"/> starts in its span: a run of 32 subbands each.</summary>
    internal static class Covariance
    {
        public const int Phi01Real = 0;
        public const int Phi01Imaginary = 32;
        public const int Phi02Real = 64;
        public const int Phi02Imaginary = 96;
        public const int Phi11 = 128;
        public const int Phi12Real = 160;
        public const int Phi12Imaginary = 192;
        public const int Phi22 = 224;
        public const int Length = 256;
    }

    /// <summary>
    /// 4.6.18.6.2's covariances phi(i, j) = the sum over n in 0..37 of X_Low(k, n + 2 - i) X_Low*(k, n + 2 - j) of the
    /// subbands below <paramref name="count"/>, into <paramref name="phi"/> (<see cref="Covariance"/> runs of 32): four
    /// subbands at a time (two with Vector128 alone), each lane summing in <see cref="CovariancesScalar"/>'s order, which does the rest.
    /// </summary>
    internal static void Covariances(double[][] real, double[][] imaginary, int count, Span<double> phi)
    {
        int k = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; k + 4 <= count; k += 4)
            {
                var phi01Real = Vector256<double>.Zero;
                var phi01Imaginary = Vector256<double>.Zero;
                var phi02Real = Vector256<double>.Zero;
                var phi02Imaginary = Vector256<double>.Zero;
                var phi11 = Vector256<double>.Zero;
                var phi12Real = Vector256<double>.Zero;
                var phi12Imaginary = Vector256<double>.Zero;
                var phi22 = Vector256<double>.Zero;
                for (int n = 0; n < Buffered - 2; n++)
                {
                    var re0 = Vector256.Create(real[n + 2].AsSpan(k));
                    var im0 = Vector256.Create(imaginary[n + 2].AsSpan(k));
                    var re1 = Vector256.Create(real[n + 1].AsSpan(k));
                    var im1 = Vector256.Create(imaginary[n + 1].AsSpan(k));
                    var re2 = Vector256.Create(real[n].AsSpan(k));
                    var im2 = Vector256.Create(imaginary[n].AsSpan(k));
                    phi01Real += (re0 * re1) + (im0 * im1);
                    phi01Imaginary += (im0 * re1) - (re0 * im1);
                    phi02Real += (re0 * re2) + (im0 * im2);
                    phi02Imaginary += (im0 * re2) - (re0 * im2);
                    phi11 += (re1 * re1) + (im1 * im1);
                    phi12Real += (re1 * re2) + (im1 * im2);
                    phi12Imaginary += (im1 * re2) - (re1 * im2);
                    phi22 += (re2 * re2) + (im2 * im2);
                }

                phi01Real.CopyTo(phi[(Covariance.Phi01Real + k)..]);
                phi01Imaginary.CopyTo(phi[(Covariance.Phi01Imaginary + k)..]);
                phi02Real.CopyTo(phi[(Covariance.Phi02Real + k)..]);
                phi02Imaginary.CopyTo(phi[(Covariance.Phi02Imaginary + k)..]);
                phi11.CopyTo(phi[(Covariance.Phi11 + k)..]);
                phi12Real.CopyTo(phi[(Covariance.Phi12Real + k)..]);
                phi12Imaginary.CopyTo(phi[(Covariance.Phi12Imaginary + k)..]);
                phi22.CopyTo(phi[(Covariance.Phi22 + k)..]);
            }
        }
        else if (!Vector256.IsHardwareAccelerated && Vector128.IsHardwareAccelerated)
        {
            for (; k + 2 <= count; k += 2)
            {
                var phi01Real = Vector128<double>.Zero;
                var phi01Imaginary = Vector128<double>.Zero;
                var phi02Real = Vector128<double>.Zero;
                var phi02Imaginary = Vector128<double>.Zero;
                var phi11 = Vector128<double>.Zero;
                var phi12Real = Vector128<double>.Zero;
                var phi12Imaginary = Vector128<double>.Zero;
                var phi22 = Vector128<double>.Zero;
                for (int n = 0; n < Buffered - 2; n++)
                {
                    var re0 = Vector128.Create(real[n + 2].AsSpan(k));
                    var im0 = Vector128.Create(imaginary[n + 2].AsSpan(k));
                    var re1 = Vector128.Create(real[n + 1].AsSpan(k));
                    var im1 = Vector128.Create(imaginary[n + 1].AsSpan(k));
                    var re2 = Vector128.Create(real[n].AsSpan(k));
                    var im2 = Vector128.Create(imaginary[n].AsSpan(k));
                    phi01Real += (re0 * re1) + (im0 * im1);
                    phi01Imaginary += (im0 * re1) - (re0 * im1);
                    phi02Real += (re0 * re2) + (im0 * im2);
                    phi02Imaginary += (im0 * re2) - (re0 * im2);
                    phi11 += (re1 * re1) + (im1 * im1);
                    phi12Real += (re1 * re2) + (im1 * im2);
                    phi12Imaginary += (im1 * re2) - (re1 * im2);
                    phi22 += (re2 * re2) + (im2 * im2);
                }

                phi01Real.CopyTo(phi[(Covariance.Phi01Real + k)..]);
                phi01Imaginary.CopyTo(phi[(Covariance.Phi01Imaginary + k)..]);
                phi02Real.CopyTo(phi[(Covariance.Phi02Real + k)..]);
                phi02Imaginary.CopyTo(phi[(Covariance.Phi02Imaginary + k)..]);
                phi11.CopyTo(phi[(Covariance.Phi11 + k)..]);
                phi12Real.CopyTo(phi[(Covariance.Phi12Real + k)..]);
                phi12Imaginary.CopyTo(phi[(Covariance.Phi12Imaginary + k)..]);
                phi22.CopyTo(phi[(Covariance.Phi22 + k)..]);
            }
        }

        CovariancesScalar(real, imaginary, k, count, phi);
    }

    /// <summary>The covariances of subbands <paramref name="from"/> to <paramref name="count"/>, one at a time: the reference.</summary>
    internal static void CovariancesScalar(double[][] real, double[][] imaginary, int from, int count, Span<double> phi)
    {
        for (int k = from; k < count; k++)
        {
            double phi01Real = 0;
            double phi01Imaginary = 0;
            double phi02Real = 0;
            double phi02Imaginary = 0;
            double phi11 = 0;
            double phi12Real = 0;
            double phi12Imaginary = 0;
            double phi22 = 0;
            for (int n = 0; n < Buffered - 2; n++)
            {
                double re0 = real[n + 2][k];
                double im0 = imaginary[n + 2][k];
                double re1 = real[n + 1][k];
                double im1 = imaginary[n + 1][k];
                double re2 = real[n][k];
                double im2 = imaginary[n][k];
                phi01Real += (re0 * re1) + (im0 * im1);
                phi01Imaginary += (im0 * re1) - (re0 * im1);
                phi02Real += (re0 * re2) + (im0 * im2);
                phi02Imaginary += (im0 * re2) - (re0 * im2);
                phi11 += (re1 * re1) + (im1 * im1);
                phi12Real += (re1 * re2) + (im1 * im2);
                phi12Imaginary += (im1 * re2) - (re1 * im2);
                phi22 += (re2 * re2) + (im2 * im2);
            }

            phi[Covariance.Phi01Real + k] = phi01Real;
            phi[Covariance.Phi01Imaginary + k] = phi01Imaginary;
            phi[Covariance.Phi02Real + k] = phi02Real;
            phi[Covariance.Phi02Imaginary + k] = phi02Imaginary;
            phi[Covariance.Phi11 + k] = phi11;
            phi[Covariance.Phi12Real + k] = phi12Real;
            phi[Covariance.Phi12Imaginary + k] = phi12Imaginary;
            phi[Covariance.Phi22 + k] = phi22;
        }
    }

    // 4.6.18.6.2: the prediction coefficients alpha0 and alpha1 of subband k from its covariances; both zero if either
    // reaches 4.
    private static (double A0Real, double A0Imaginary, double A1Real, double A1Imaginary) Prediction(ReadOnlySpan<double> phi, int k)
    {
        double phi01Real = phi[Covariance.Phi01Real + k];
        double phi01Imaginary = phi[Covariance.Phi01Imaginary + k];
        double phi02Real = phi[Covariance.Phi02Real + k];
        double phi02Imaginary = phi[Covariance.Phi02Imaginary + k];
        double phi11 = phi[Covariance.Phi11 + k];
        double phi12Real = phi[Covariance.Phi12Real + k];
        double phi12Imaginary = phi[Covariance.Phi12Imaginary + k];
        double phi22 = phi[Covariance.Phi22 + k];

        double a1Real = 0;
        double a1Imaginary = 0;
        double determinant = (phi11 * phi22) - (((phi12Real * phi12Real) + (phi12Imaginary * phi12Imaginary)) / (1 + 1e-6));
        if (determinant != 0)
        {
            a1Real = ((phi01Real * phi12Real) - (phi01Imaginary * phi12Imaginary) - (phi02Real * phi11)) / determinant;
            a1Imaginary = ((phi01Imaginary * phi12Real) + (phi01Real * phi12Imaginary) - (phi02Imaginary * phi11)) / determinant;
        }

        double a0Real = 0;
        double a0Imaginary = 0;
        if (phi11 != 0)
        {
            a0Real = -(phi01Real + (a1Real * phi12Real) + (a1Imaginary * phi12Imaginary)) / phi11;
            a0Imaginary = -(phi01Imaginary + (a1Imaginary * phi12Real) - (a1Real * phi12Imaginary)) / phi11;
        }

        if ((a0Real * a0Real) + (a0Imaginary * a0Imaginary) >= 16 || (a1Real * a1Real) + (a1Imaginary * a1Imaginary) >= 16)
        {
            return default;
        }

        return (a0Real, a0Imaginary, a1Real, a1Imaginary);
    }

    // 4.6.18.7: per envelope, the gains that bring each subband's energy to the envelope's, with the noise floor and
    // the sinusoids added at the levels they ask for; limited per limiter band, boosted back towards the band's energy,
    // smoothed over time outside transients; then noise from the table and sinusoids in quadrature phase steps.
    private void Adjust(SbrChannelData data, SbrHeader header, SbrFrequencies frequencies, AacStatistics statistics)
    {
        int kx = frequencies.Kx;
        int m = frequencies.M;
        int transient = data.TransientEnvelope;
        int[] limiter = frequencies.LimiterTables[header.LimiterBands];

        // Where this frame's sinusoids go: the middle subband of each high-resolution band with bs_add_harmonic.
        Span<bool> harmonic = stackalloc bool[64];
        harmonic.Clear();
        for (int i = 0; i < frequencies.Bands(1); i++)
        {
            if (data.AddHarmonic[i])
            {
                harmonic[(frequencies.High[i] + frequencies.High[i + 1]) >> 1] = true;
            }
        }

        Span<double> estimate = stackalloc double[64];
        Span<double> energy = stackalloc double[64];
        Span<double> reference = stackalloc double[64];
        Span<double> gain = stackalloc double[64];
        Span<double> noise = stackalloc double[64];
        Span<double> tone = stackalloc double[64];
        for (int l = 0; l < data.NumEnvelopes; l++)
        {
            int first = 2 * data.Borders[l];
            int last = 2 * data.Borders[l + 1];
            bool noNoise = l == transient || l == _previousTransient;
            int noiseFloor = data.NumNoiseFloors > 1 && data.Borders[l] >= data.NoiseBorders[1] ? 1 : 0;
            int[] table = data.FrequencyResolution[l] == 1 ? frequencies.High : frequencies.Low;
            int noiseBand = 0;
            statistics.SbrTransientEnvelopes += noNoise ? 1 : 0;

            // Each subband's energy over the envelope's slots, summed slot by slot along the rows (each subband's terms in
            // the same order as subband by subband).
            var energies = energy[..m];
            energies.Clear();
            for (int t = first; t < last; t++)
            {
                Vectors.AddMagnitudes(energies, _highReal[t].AsSpan(kx, m), _highImaginary[t].AsSpan(kx, m));
            }

            for (int band = 0; band < table.Length - 1; band++)
            {
                int low = table[band];
                int high = table[band + 1];
                bool sineInBand = false;
                for (int k = low; k < high; k++)
                {
                    estimate[k - kx] = last > first ? energies[k - kx] / (last - first) : 0;
                    sineInBand |= harmonic[k] && (l >= transient || _previousHarmonic[k]);
                }

                if (!header.InterpolateFrequency)
                {
                    double mean = 0;
                    for (int k = low; k < high; k++)
                    {
                        mean += estimate[k - kx];
                    }

                    estimate[(low - kx)..(high - kx)].Fill(mean / (high - low));
                }

                double envelope = data.EnvelopeEnergy[l][band];
                for (int k = low; k < high; k++)
                {
                    while (noiseBand + 1 < frequencies.NoiseBands && k >= frequencies.Noise[noiseBand + 1])
                    {
                        noiseBand++;
                    }

                    double q = data.NoiseRatio[noiseFloor][noiseBand];
                    int i = k - kx;
                    reference[i] = envelope;
                    noise[i] = Math.Sqrt(envelope * q / (1 + q));
                    tone[i] = harmonic[k] && (l >= transient || _previousHarmonic[k]) ? Math.Sqrt(envelope / (1 + q)) : 0;
                    double denominator = Epsilon + estimate[i];
                    gain[i] = Math.Sqrt(sineInBand ? envelope * q / ((1 + q) * denominator) : noNoise ? envelope / denominator : envelope / (denominator * (1 + q)));
                    statistics.SbrSinusoids += tone[i] > 0 ? 1 : 0;
                }
            }

            for (int c = 0; c < limiter.Length - 1; c++)
            {
                int low = limiter[c] - kx;
                int high = limiter[c + 1] - kx;
                double sumReference = 0;
                double sumEstimate = 0;
                for (int i = low; i < high; i++)
                {
                    sumReference += reference[i];
                    sumEstimate += estimate[i];
                }

                double maxGain = Math.Min(Math.Sqrt((sumReference + Epsilon0) / (sumEstimate + Epsilon0)) * LimiterGains[header.LimiterGains], MaxGain);
                double adjusted = 0;
                for (int i = low; i < high; i++)
                {
                    if (gain[i] > maxGain)
                    {
                        noise[i] *= maxGain / gain[i];
                        gain[i] = maxGain;
                        statistics.SbrLimitedGains++;
                    }

                    adjusted += gain[i] * gain[i] * estimate[i];
                    adjusted += tone[i] != 0 ? tone[i] * tone[i] : noNoise ? 0 : noise[i] * noise[i];
                }

                double boost = Math.Min(Math.Sqrt((sumReference + Epsilon0) / (adjusted + Epsilon0)), MaxBoost);
                for (int i = low; i < high; i++)
                {
                    gain[i] *= boost;
                    noise[i] *= boost;
                    tone[i] *= boost;
                }
            }

            if (_startUp)
            {
                for (int n = 0; n < 4; n++)
                {
                    gain[..m].CopyTo(_gainHistory[n]);
                    noise[..m].CopyTo(_noiseHistory[n]);
                }

                _startUp = false;
            }

            bool smooth = !noNoise && !header.SmoothingMode;
            statistics.SbrSmoothedEnvelopes += smooth ? 1 : 0;
            for (int t = first; t < last; t++)
            {
                var real = _highReal[t].AsSpan(kx, m);
                var imaginary = _highImaginary[t].AsSpan(kx, m);
                gain[..m].CopyTo(_gainHistory[4]);
                noise[..m].CopyTo(_noiseHistory[4]);
                if (smooth)
                {
                    for (int i = 0; i < m; i++)
                    {
                        double g = 0;
                        double n = 0;
                        for (int j = 0; j < 5; j++)
                        {
                            g += _gainHistory[j][i] * Smoothing[j];
                            n += _noiseHistory[j][i] * Smoothing[j];
                        }

                        _noiseIndex = (_noiseIndex + 1) & 511;
                        real[i] *= g;
                        imaginary[i] *= g;
                        if (tone[i] == 0 && !noNoise)
                        {
                            real[i] += n * SbrNoiseTable.Values[2 * _noiseIndex];
                            imaginary[i] += n * SbrNoiseTable.Values[(2 * _noiseIndex) + 1];
                        }

                        real[i] += tone[i] * SineReal[_sineIndex];
                        imaginary[i] += tone[i] * (((kx + i) & 1) != 0 ? -1 : 1) * SineImaginary[_sineIndex];
                    }
                }
                else
                {
                    ApplyGains(real, imaginary, gain[..m], noise[..m], tone[..m], kx, _noiseIndex, _sineIndex, noNoise);
                    _noiseIndex = (_noiseIndex + m) & 511;
                }

                _sineIndex = (_sineIndex + 1) & 3;
                (_gainHistory[0], _gainHistory[1], _gainHistory[2], _gainHistory[3], _gainHistory[4]) = (_gainHistory[1], _gainHistory[2], _gainHistory[3], _gainHistory[4], _gainHistory[0]);
                (_noiseHistory[0], _noiseHistory[1], _noiseHistory[2], _noiseHistory[3], _noiseHistory[4]) = (_noiseHistory[1], _noiseHistory[2], _noiseHistory[3], _noiseHistory[4], _noiseHistory[0]);
            }
        }

        harmonic.CopyTo(_previousHarmonic);
        _previousTransient = transient == data.NumEnvelopes ? 0 : -1;
    }

    // 4.6.18.4.2: each output slot from the low band's subbands below the slot's kx and the adjusted high band from it
    // up.
    private void Synthesize(Span<float> output)
    {
        Span<double> real = stackalloc double[64];
        Span<double> imaginary = stackalloc double[64];
        Span<double> samples = stackalloc double[64];
        for (int l = 0; l < Slots; l++)
        {
            SynthesisInput(l, real, imaginary);
            _synthesis.Process(real, imaginary, samples);
            Output(samples, output[(_synthesis.Bands * l)..], _synthesis.Bands);
        }
    }

    // Slot l's synthesis input: the low band below the slot's kx, the adjusted high band from it up.
    private void SynthesisInput(int l, Span<double> real, Span<double> imaginary)
    {
        int kx = _highStart[l];
        _lowReal[l + HfAdjust].AsSpan(0, kx).CopyTo(real);
        _lowImaginary[l + HfAdjust].AsSpan(0, kx).CopyTo(imaginary);
        _highReal[l].AsSpan(kx).CopyTo(real[kx..]);
        _highImaginary[l].AsSpan(kx).CopyTo(imaginary[kx..]);
    }

    private static void Output(ReadOnlySpan<double> samples, Span<float> output, int count)
    {
        for (int n = 0; n < count; n++)
        {
            output[n] = (float)(samples[n] / 32768);
        }
    }

    // Parametric stereo: the frame's synthesis input (and the hybrid filters' 6 slots of the five lowest bands after it,
    // from the low band) mixed into a left and a right channel, each through a synthesis bank of its own.
    private void SynthesizeStereo(SbrElement? element, Span<float> left, Span<float> right, AacStatistics statistics)
    {
        _ps ??= new PsProcessor();
        _rightSynthesis ??= new QmfSynthesis(downsampled);
        _stereoRe ??= Buffers(Slots + PsProcessor.LookAhead, 64);
        _stereoIm ??= Buffers(Slots + PsProcessor.LookAhead, 64);
        _rightRe ??= Buffers(Slots, 64);
        _rightIm ??= Buffers(Slots, 64);
        for (int l = 0; l < Slots; l++)
        {
            SynthesisInput(l, _stereoRe[l], _stereoIm[l]);
        }

        for (int l = Slots; l < Slots + PsProcessor.LookAhead; l++)
        {
            _lowReal[l + HfAdjust].AsSpan(0, PsProcessor.HybridQmfBands).CopyTo(_stereoRe[l]);
            _lowImaginary[l + HfAdjust].AsSpan(0, PsProcessor.HybridQmfBands).CopyTo(_stereoIm[l]);
        }

        int top = element is { HasFrame: true, Frequencies: { } frequencies } ? frequencies.Kx + frequencies.M : 32;
        _ps.Apply(element?.Ps ?? NoPs, element is { HasPs: true }, _stereoRe, _stereoIm, _rightRe, _rightIm, top, statistics);

        Span<double> samples = stackalloc double[64];
        for (int l = 0; l < Slots; l++)
        {
            int bands = _synthesis.Bands;
            _synthesis.Process(_stereoRe[l], _stereoIm[l], samples);
            Output(samples, left[(bands * l)..], bands);
            _rightSynthesis.Process(_rightRe[l], _rightIm[l], samples);
            Output(samples, right[(bands * l)..], bands);
        }
    }

    // The buffers move on a frame.
    private void MoveOn()
    {
        for (int l = 0; l < Kept; l++)
        {
            (_lowReal[l], _lowReal[l + Slots]) = (_lowReal[l + Slots], _lowReal[l]);
            (_lowImaginary[l], _lowImaginary[l + Slots]) = (_lowImaginary[l + Slots], _lowImaginary[l]);
            (_highReal[l], _highReal[l + Slots]) = (_highReal[l + Slots], _highReal[l]);
            (_highImaginary[l], _highImaginary[l + Slots]) = (_highImaginary[l + Slots], _highImaginary[l]);
            _highStart[l] = _highStart[l + Slots];
        }

        for (int l = Kept; l < Buffered; l++)
        {
            Array.Clear(_highReal[l]);
            Array.Clear(_highImaginary[l]);
            _highStart[l] = 32;
        }
    }

    private static double[][] Buffers(int count, int length) => [.. Enumerable.Range(0, count).Select(_ => new double[length])];

    /// <summary>
    /// One slot's subbands kx .. kx + m - 1 (<paramref name="real"/>, <paramref name="imaginary"/>) times their gains, plus
    /// their noise (where there is no sinusoid, and the envelope has noise) and their sinusoids, without smoothing: the
    /// noise index moves one a subband from <paramref name="noiseIndex"/>. Four subbands at a time where Vector256 is
    /// accelerated (two with Vector128 alone, ARM64's), each lane with <see cref="ApplyGainsScalar"/>'s operations in its order (the noise added under a mask,
    /// not as a zero), so the output is the same to the bit (<c>SbrApplyGains_MatchesScalar</c>).
    /// </summary>
    internal static void ApplyGains(Span<double> real, Span<double> imaginary, ReadOnlySpan<double> gain, ReadOnlySpan<double> noise, ReadOnlySpan<double> tone, int kx, int noiseIndex, int sineIndex, bool noNoise)
    {
        int m = gain.Length;
        if (real.Length < m || imaginary.Length < m || noise.Length < m || tone.Length < m || m > 64)
        {
            throw new ArgumentException("The spans are shorter than the gains, or longer than 64.");
        }

        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var noiseRe = NoiseReal.AsSpan((noiseIndex + 1) & 511, m);
            var noiseIm = NoiseImaginary.AsSpan((noiseIndex + 1) & 511, m);
            var sign = SineSign.AsSpan(kx, m);
            var sineRe = Vector256.Create(SineReal[sineIndex]);
            var sineIm = Vector256.Create(SineImaginary[sineIndex]);
            var addNoise = noNoise ? Vector256<double>.Zero : Vector256<double>.AllBitsSet;
            for (; i + 4 <= m; i += 4)
            {
                var g = Vector256.Create(gain[i..]);
                var n = Vector256.Create(noise[i..]);
                var t = Vector256.Create(tone[i..]);
                var re = Vector256.Create<double>(real[i..]) * g;
                var im = Vector256.Create<double>(imaginary[i..]) * g;
                var mask = Vector256.Equals(t, Vector256<double>.Zero) & addNoise;
                re = Vector256.ConditionalSelect(mask, re + (n * Vector256.Create(noiseRe[i..])), re);
                im = Vector256.ConditionalSelect(mask, im + (n * Vector256.Create(noiseIm[i..])), im);
                re += t * sineRe;
                im += t * Vector256.Create(sign[i..]) * sineIm;
                re.CopyTo(real[i..]);
                im.CopyTo(imaginary[i..]);
            }
        }
        else if (!Vector256.IsHardwareAccelerated && Vector128.IsHardwareAccelerated)
        {
            var noiseRe = NoiseReal.AsSpan((noiseIndex + 1) & 511, m);
            var noiseIm = NoiseImaginary.AsSpan((noiseIndex + 1) & 511, m);
            var sign = SineSign.AsSpan(kx, m);
            var sineRe = Vector128.Create(SineReal[sineIndex]);
            var sineIm = Vector128.Create(SineImaginary[sineIndex]);
            var addNoise = noNoise ? Vector128<double>.Zero : Vector128<double>.AllBitsSet;
            for (; i + 2 <= m; i += 2)
            {
                var g = Vector128.Create(gain[i..]);
                var n = Vector128.Create(noise[i..]);
                var t = Vector128.Create(tone[i..]);
                var re = Vector128.Create<double>(real[i..]) * g;
                var im = Vector128.Create<double>(imaginary[i..]) * g;
                var mask = Vector128.Equals(t, Vector128<double>.Zero) & addNoise;
                re = Vector128.ConditionalSelect(mask, re + (n * Vector128.Create(noiseRe[i..])), re);
                im = Vector128.ConditionalSelect(mask, im + (n * Vector128.Create(noiseIm[i..])), im);
                re += t * sineRe;
                im += t * Vector128.Create(sign[i..]) * sineIm;
                re.CopyTo(real[i..]);
                im.CopyTo(imaginary[i..]);
            }
        }

        ApplyGainsScalar(real[i..m], imaginary[i..m], gain[i..], noise[i..], tone[i..], kx + i, (noiseIndex + i) & 511, sineIndex, noNoise);
    }

    /// <summary>
    /// <see cref="ApplyGains"/> one subband at a time, as the decoding process says it (4.6.18.7.5): the reference for it and
    /// its tail.
    /// </summary>
    internal static void ApplyGainsScalar(Span<double> real, Span<double> imaginary, ReadOnlySpan<double> gain, ReadOnlySpan<double> noise, ReadOnlySpan<double> tone, int kx, int noiseIndex, int sineIndex, bool noNoise)
    {
        for (int i = 0; i < real.Length; i++)
        {
            double g = gain[i];
            double n = noise[i];
            noiseIndex = (noiseIndex + 1) & 511;
            real[i] *= g;
            imaginary[i] *= g;
            if (tone[i] == 0 && !noNoise)
            {
                real[i] += n * SbrNoiseTable.Values[2 * noiseIndex];
                imaginary[i] += n * SbrNoiseTable.Values[(2 * noiseIndex) + 1];
            }

            real[i] += tone[i] * SineReal[sineIndex];
            imaginary[i] += tone[i] * (((kx + i) & 1) != 0 ? -1 : 1) * SineImaginary[sineIndex];
        }
    }
}
