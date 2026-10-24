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
    // factor, to whiten what the core's tonality would make too tonal. Subbands past the last patch stay empty.
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
                var (a0Real, a0Imaginary, a1Real, a1Imaginary) = bw > 0 ? Prediction(source) : default;
                a0Real *= bw;
                a0Imaginary *= bw;
                a1Real *= bw * bw;
                a1Imaginary *= bw * bw;
                for (int l = first; l < last; l++)
                {
                    int t = l + HfAdjust;
                    double re1 = _lowReal[t - 1][source];
                    double im1 = _lowImaginary[t - 1][source];
                    double re2 = _lowReal[t - 2][source];
                    double im2 = _lowImaginary[t - 2][source];
                    _highReal[l][destination] = _lowReal[t][source] + (a0Real * re1) - (a0Imaginary * im1) + (a1Real * re2) - (a1Imaginary * im2);
                    _highImaginary[l][destination] = _lowImaginary[t][source] + (a0Imaginary * re1) + (a0Real * im1) + (a1Imaginary * re2) + (a1Real * im2);
                }
            }
        }
    }

    // 4.6.18.6.2: the prediction coefficients alpha0 and alpha1 of subband k, from the covariances
    // phi(i, j) = the sum over n in 0..37 of X_Low(k, n + 2 - i) X_Low*(k, n + 2 - j); both zero if either reaches 4.
    private (double A0Real, double A0Imaginary, double A1Real, double A1Imaginary) Prediction(int k)
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
            double re0 = _lowReal[n + 2][k];
            double im0 = _lowImaginary[n + 2][k];
            double re1 = _lowReal[n + 1][k];
            double im1 = _lowImaginary[n + 1][k];
            double re2 = _lowReal[n][k];
            double im2 = _lowImaginary[n][k];
            phi01Real += (re0 * re1) + (im0 * im1);
            phi01Imaginary += (im0 * re1) - (re0 * im1);
            phi02Real += (re0 * re2) + (im0 * im2);
            phi02Imaginary += (im0 * re2) - (re0 * im2);
            phi11 += (re1 * re1) + (im1 * im1);
            phi12Real += (re1 * re2) + (im1 * im2);
            phi12Imaginary += (im1 * re2) - (re1 * im2);
            phi22 += (re2 * re2) + (im2 * im2);
        }

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

            for (int band = 0; band < table.Length - 1; band++)
            {
                int low = table[band];
                int high = table[band + 1];
                bool sineInBand = false;
                for (int k = low; k < high; k++)
                {
                    double energy = 0;
                    for (int t = first; t < last; t++)
                    {
                        energy += (_highReal[t][k] * _highReal[t][k]) + (_highImaginary[t][k] * _highImaginary[t][k]);
                    }

                    estimate[k - kx] = last > first ? energy / (last - first) : 0;
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
                for (int i = 0; i < m; i++)
                {
                    double g = gain[i];
                    double n = noise[i];
                    if (smooth)
                    {
                        g = 0;
                        n = 0;
                        for (int j = 0; j < 5; j++)
                        {
                            g += _gainHistory[j][i] * Smoothing[j];
                            n += _noiseHistory[j][i] * Smoothing[j];
                        }
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

    // Parametric stereo: the frame's synthesis input (and the hybrid filters' 6 slots of the three lowest bands after it,
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
            _lowReal[l + HfAdjust].AsSpan(0, 3).CopyTo(_stereoRe[l]);
            _lowImaginary[l + HfAdjust].AsSpan(0, 3).CopyTo(_stereoIm[l]);
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
}
