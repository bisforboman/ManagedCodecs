using ManagedCodecs.Aac.Sbr;

namespace ManagedCodecs.Tests.Aac;

/// <summary>
/// The SBR QMF banks: 32-band analysis at the core's rate, then 64-band synthesis with the upper 32 bands empty, gives
/// the core's signal at twice the rate, delayed. That is SBR's output where it adds no high band, and checks the window,
/// both kernels, the sample order and the state kept between time slots.
/// </summary>
public class QmfTests
{
    // The banks' delay at the output rate, as measured: a fractional delay or a misplaced sample would leave no single
    // integer delay that fits.
    private const int Delay = 578;

    // Slots before the comparison starts: past the banks' start-up (the delay is 9 slots of output).
    private const int StartUp = 40;

    // The banks' delay with downsampled synthesis, at the core's rate, as measured: half the full bank's, its output on
    // the full bank's even samples.
    private const double DownsampledDelay = 289;

    private const int Slots = 200;

    [Fact]
    public void Window_IsSymmetricInMagnitude()
    {
        var c = QmfWindow.Coefficients;
        Assert.Equal(640, c.Length);
        Assert.Equal(0, c[0]);
        for (int n = 1; n < 320; n++)
        {
            Assert.Equal(Math.Abs(c[n]), Math.Abs(c[640 - n]));
        }
    }

    /// <summary>The kernels' cos and sin, computed without Math.Cos and Math.Sin, agree with them to double precision.</summary>
    [Fact]
    public void Trig_MatchesTheLibrary()
    {
        for (int m = -70000; m <= 70000; m += 7)
        {
            Assert.Equal(Math.Cos(Math.PI * (m % 512) / 256), QmfTrig.CosPi256(m), 1e-15);
            Assert.Equal(Math.Sin(Math.PI * (m % 512) / 256), QmfTrig.SinPi256(m), 1e-15);
        }
    }

    /// <summary>
    /// Tones at random frequencies and phases below 0.9 of the core's Nyquist frequency come out as the same tones at
    /// twice the rate, to the window's ripple: the bank is nearly, not perfectly, reconstructing (about 1e-3 in gain).
    /// </summary>
    [Fact]
    public void AnalysisThenSynthesis_UpsamplesTheSignal()
    {
        var random = new Random(5);
        var tones = Enumerable.Range(0, 8).Select(_ => (Frequency: random.NextDouble() * 0.9 * Math.PI, Phase: random.NextDouble() * 2 * Math.PI, Amplitude: 0.1 + random.NextDouble())).ToArray();
        double Signal(double t) => tones.Sum(tone => tone.Amplitude * Math.Sin((tone.Frequency * t) + tone.Phase));

        var output = RoundTrip(Signal);
        double Error(int delay) => Enumerable.Range(StartUp * 64, (Slots - StartUp) * 64).Max(m => Math.Abs(output[m] - Signal((m - delay) / 2.0)));
        double peak = tones.Sum(tone => tone.Amplitude);
        Assert.True(Error(Delay) < 1.5e-3 * peak, $"Error {Error(Delay) / peak:e2} of the peak.");
        Assert.Equal(Delay, Enumerable.Range(0, 1500).MinBy(Error));
    }

    /// <summary>A single tone's gain is 1 and its phase delayed by exactly <see cref="Delay"/>, across the core's band.</summary>
    [Fact]
    public void AnalysisThenSynthesis_HasUnitGainAcrossTheBand()
    {
        for (double frequency = 0.05; frequency < 0.98; frequency += 0.0123)
        {
            var output = RoundTrip(t => Math.Cos(frequency * Math.PI * t));

            // In phase and in quadrature with the delayed tone.
            double inPhase = 0;
            double quadrature = 0;
            int count = (Slots - StartUp) * 64;
            for (int m = StartUp * 64; m < Slots * 64; m++)
            {
                double phase = frequency * Math.PI * (m - Delay) / 2;
                inPhase += output[m] * Math.Cos(phase);
                quadrature += output[m] * Math.Sin(phase);
            }

            Assert.InRange(2 * inPhase / count, 0.997, 1.003);
            Assert.InRange(2 * quadrature / count, -1e-3, 1e-3);
        }
    }

    /// <summary>
    /// Downsampled synthesis after analysis gives the core's signal back at its own rate, delayed by
    /// <see cref="DownsampledDelay"/> (the best fit in sixteenths of a sample), to the window's ripple and aliasing (up to about 5e-3), with
    /// a gain of 1 across the band.
    /// </summary>
    [Fact]
    public void AnalysisThenDownsampledSynthesis_GivesTheSignalBack()
    {
        var random = new Random(6);
        var tones = Enumerable.Range(0, 8).Select(_ => (Frequency: random.NextDouble() * 0.9 * Math.PI, Phase: random.NextDouble() * 2 * Math.PI, Amplitude: 0.1 + random.NextDouble())).ToArray();
        double Signal(double t) => tones.Sum(tone => tone.Amplitude * Math.Sin((tone.Frequency * t) + tone.Phase));

        var output = RoundTrip(Signal, downsampled: true);
        double Error(double delay) => Enumerable.Range(StartUp * 32, (Slots - StartUp) * 32).Max(m => Math.Abs(output[m] - Signal(m - delay)));
        double peak = tones.Sum(tone => tone.Amplitude);
        Assert.Equal(DownsampledDelay, Enumerable.Range(0, 16 * 600).Select(q => q / 16.0).MinBy(Error));
        Assert.True(Error(DownsampledDelay) < 5e-3 * peak, $"Error {Error(DownsampledDelay) / peak:e2} of the peak.");

        double worstGain = 0;
        double worstQuadrature = 0;
        for (double frequency = 0.05; frequency < 0.98; frequency += 0.0123)
        {
            var tone = RoundTrip(t => Math.Cos(frequency * Math.PI * t), downsampled: true);
            double inPhase = 0;
            double quadrature = 0;
            int count = (Slots - StartUp) * 32;
            for (int m = StartUp * 32; m < Slots * 32; m++)
            {
                double phase = frequency * Math.PI * (m - DownsampledDelay);
                inPhase += tone[m] * Math.Cos(phase);
                quadrature += tone[m] * Math.Sin(phase);
            }

            worstGain = Math.Max(worstGain, Math.Abs((2 * inPhase / count) - 1));
            worstQuadrature = Math.Max(worstQuadrature, Math.Abs(2 * quadrature / count));
        }

        Assert.True(worstGain < 6e-3 && worstQuadrature < 6e-3, $"Gain off by up to {worstGain:e2}, quadrature up to {worstQuadrature:e2}.");
    }

    /// <summary>The vector FFT does each butterfly's arithmetic in the scalar order: the same doubles, to the bit.</summary>
    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    public void VectorFft_MatchesScalarExactly(int length)
    {
        var fft = new QmfFft(length);
        var random = new Random(length);
        var re = new double[length];
        var im = new double[length];
        for (int trial = 0; trial < 20; trial++)
        {
            for (int k = 0; k < length; k++)
            {
                re[k] = (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(0, 6));
                im[k] = (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(0, 6));
            }

            var expectedRe = (double[])re.Clone();
            var expectedIm = (double[])im.Clone();
            fft.Transform(expectedRe, expectedIm, scalar: true);
            fft.Transform(re, im);
            Assert.Equal(expectedRe, re);
            Assert.Equal(expectedIm, im);
        }
    }

    /// <summary>The vector rotations give the scalar results, tails included.</summary>
    [Fact]
    public void VectorRotations_MatchScalarExactly()
    {
        var random = new Random(9);
        for (int length = 0; length < 20; length++)
        {
            double[] Values() => [.. Enumerable.Range(0, length).Select(_ => (random.NextDouble() - 0.5) * 1e5)];
            var (re, im, cos, sin) = (Values(), Values(), Values(), Values());
            var (outRe, outIm, real) = (new double[length], new double[length], new double[length]);
            QmfState.Rotate(re, im, cos, sin, outRe, outIm);
            QmfState.RealOfRotation(re, im, cos, sin, real);
            Assert.Equal(re.Select((x, k) => (x * cos[k]) - (im[k] * sin[k])), outRe);
            Assert.Equal(re.Select((x, k) => (x * sin[k]) + (im[k] * cos[k])), outIm);
            Assert.Equal(re.Select((x, k) => (x * cos[k]) - (im[k] * sin[k])), real);
        }
    }

    /// <summary>
    /// The FFT-based banks give the spec's sums (<see cref="QmfAnalysisReference"/>, <see cref="QmfSynthesisReference"/>)
    /// on random input, slot after slot: past the point where their history buffers wrap around (after 15 slots of the
    /// analysis bank, 9 of the synthesis bank).
    /// </summary>
    [Fact]
    public void FastBanks_MatchTheSums()
    {
        var random = new Random(11);
        var (analysis, analysisReference) = (new QmfAnalysis(), new QmfAnalysisReference());
        var (synthesis, synthesisReference) = (new QmfSynthesis(), new QmfSynthesisReference());
        var (downsampled, downsampledReference) = (new QmfSynthesis(downsampled: true), new QmfSynthesisReference(downsampled: true));
        var input = new double[32];
        var (real, imaginary, realReference, imaginaryReference) = (new double[64], new double[64], new double[64], new double[64]);
        var (output, outputReference) = (new double[64], new double[64]);
        for (int slot = 0; slot < 50; slot++)
        {
            for (int n = 0; n < 32; n++)
            {
                input[n] = (random.NextDouble() - 0.5) * 65536;
            }

            analysis.Process(input, real, imaginary);
            analysisReference.Process(input, realReference, imaginaryReference);
            for (int k = 0; k < 32; k++)
            {
                Assert.Equal(realReference[k], real[k], 1e-6);
                Assert.Equal(imaginaryReference[k], imaginary[k], 1e-6);
            }

            for (int k = 0; k < 64; k++)
            {
                real[k] = (random.NextDouble() - 0.5) * 65536;
                imaginary[k] = (random.NextDouble() - 0.5) * 65536;
            }

            synthesis.Process(real, imaginary, output);
            synthesisReference.Process(real, imaginary, outputReference);
            for (int n = 0; n < 64; n++)
            {
                Assert.Equal(outputReference[n], output[n], 1e-6);
            }

            downsampled.Process(real, imaginary, output);
            downsampledReference.Process(real, imaginary, outputReference);
            for (int n = 0; n < 32; n++)
            {
                Assert.Equal(outputReference[n], output[n], 1e-6);
            }
        }
    }

    private static double[] RoundTrip(Func<double, double> signal, bool downsampled = false)
    {
        var analysis = new QmfAnalysis();
        var synthesis = new QmfSynthesis(downsampled);
        int bands = synthesis.Bands;
        var output = new double[Slots * bands];
        var input = new double[32];
        var real = new double[64];
        var imaginary = new double[64];
        for (int slot = 0; slot < Slots; slot++)
        {
            for (int n = 0; n < 32; n++)
            {
                input[n] = signal((32 * slot) + n);
            }

            analysis.Process(input, real, imaginary);
            synthesis.Process(real, imaginary, output.AsSpan(bands * slot, bands));
        }

        return output;
    }
}
