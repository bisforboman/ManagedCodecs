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

    private static double[] RoundTrip(Func<double, double> signal)
    {
        var analysis = new QmfAnalysis();
        var synthesis = new QmfSynthesis();
        var output = new double[Slots * 64];
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
            synthesis.Process(real, imaginary, output.AsSpan(64 * slot, 64));
        }

        return output;
    }
}
