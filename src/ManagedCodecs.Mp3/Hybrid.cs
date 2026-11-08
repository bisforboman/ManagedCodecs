namespace ManagedCodecs.Mp3;

/// <summary>
/// The hybrid filterbank's frequency-to-time half (ISO/IEC 11172-3 2.4.3.4.10): alias reduction, the IMDCT of each
/// subband's 18 lines (or three windows of 6), windowing, overlap-add with the previous granule and frequency inversion,
/// giving 18 samples of each of the 32 subbands for the synthesis filterbank.
/// </summary>
/// <remarks>
/// The IMDCTs are their sums by the definition (2.4.3.4.10.2), and every cosine and sine of the tables comes from
/// <see cref="ExactMath"/>, so that each runtime has the same bits.
/// </remarks>
internal static class Hybrid
{
    // cos(pi / 72 (2i + 1 + 18) (2k + 1)) for i 0..35, k 0..17, at [18 i + k].
    private static readonly double[] LongCosines = BuildCosines(36, 18, 72, 18);

    // cos(pi / 24 (2i + 1 + 6) (2k + 1)) for i 0..11, k 0..5, at [6 i + k].
    private static readonly double[] ShortCosines = BuildCosines(12, 6, 24, 6);

    // The windows of block types 0 (normal), 1 (start) and 3 (stop), 36 each, and the short window, 12.
    private static readonly double[][] LongWindows = [Window(0), Window(1), [], Window(3)];
    private static readonly double[] ShortWindow = [.. Enumerable.Range(0, 12).Select(i => ExactMath.SinPi((2 * i) + 1, 24))];

    // Alias reduction's butterflies (Table B.9): c_i, then cs = 1 / sqrt(1 + c^2), ca = c / sqrt(1 + c^2).
    private static readonly double[] AliasCoefficients = [-0.6, -0.535, -0.33, -0.185, -0.095, -0.041, -0.0142, -0.0037];
    private static readonly double[] Cs = [.. AliasCoefficients.Select(c => 1 / Math.Sqrt(1 + (c * c)))];
    private static readonly double[] Ca = [.. AliasCoefficients.Select(c => c / Math.Sqrt(1 + (c * c)))];

    /// <summary>
    /// Turns a granule's spectrum (<paramref name="spectrum"/>, in coded order; overwritten) into
    /// <paramref name="output"/>[18 sb + t], subband sb's sample t, overlapping with <paramref name="overlap"/> (the
    /// previous granule's second halves, at the same indices; updated).
    /// </summary>
    public static void Process(Granule g, bool lsf, int rateIndex, double[] spectrum, double[] overlap, double[] output)
    {
        // Subbands with long blocks: all, none, or a mixed block's lowest (2, or 4 at 8 kHz, where its long bands end at
        // line 72). mixed_block_flag counts in start and stop blocks too: their lowest subbands take the normal window
        // (2.4.2.7: "the two lowest subbands are transformed using a normal window"), as in l3-si_block.
        int mixedSubbands = g.Mixed ? Mp3Tables.LongBands[rateIndex][Spectrum.MixedLongBands(lsf)] / 18 : 0;
        int longSubbands = !g.ShortBlocks ? 32 : mixedSubbands;
        if (g.ShortBlocks)
        {
            Reorder(spectrum, rateIndex, g.Mixed ? 3 : 0);
        }

        // Between every two subbands of long blocks; of a mixed block only between the first two (as FFmpeg's decoder,
        // also at 8 kHz).
        int boundaries = !g.ShortBlocks ? 31 : g.Mixed ? 1 : 0;
        for (int sb = 1; sb <= boundaries; sb++)
        {
            for (int i = 0; i < 8; i++)
            {
                int lower = (18 * sb) - 1 - i;
                int upper = (18 * sb) + i;
                double a = spectrum[lower];
                double b = spectrum[upper];
                spectrum[lower] = (a * Cs[i]) - (b * Ca[i]);
                spectrum[upper] = (b * Cs[i]) + (a * Ca[i]);
            }
        }

        Span<double> block = stackalloc double[36];
        for (int sb = 0; sb < 32; sb++)
        {
            int at = 18 * sb;
            if (sb < longSubbands)
            {
                LongImdct(spectrum.AsSpan(at, 18), LongWindows[g.ShortBlocks || sb < mixedSubbands ? 0 : g.BlockType], block);
            }
            else
            {
                ShortImdct(spectrum.AsSpan(at, 18), block);
            }

            for (int t = 0; t < 18; t++)
            {
                double sample = block[t] + overlap[at + t];
                overlap[at + t] = block[18 + t];

                // Frequency inversion: odd samples of odd subbands negated.
                output[at + t] = (sb & 1) != 0 && (t & 1) != 0 ? -sample : sample;
            }
        }
    }

    /// <summary>
    /// The 36-point IMDCT of 18 lines (2.4.3.4.10.2): x_i = sum over k of X_k cos(pi / 72 (2i + 1 + 18) (2k + 1)),
    /// windowed.
    /// </summary>
    public static void LongImdct(ReadOnlySpan<double> lines, double[] window, Span<double> output)
    {
        for (int i = 0; i < 36; i++)
        {
            double sum = 0;
            for (int k = 0; k < 18; k++)
            {
                sum += lines[k] * LongCosines[(18 * i) + k];
            }

            output[i] = sum * window[i];
        }
    }

    /// <summary>
    /// Three 12-point IMDCTs of a short block's windows (lines 6 w .. 6 w + 5 after <see cref="Reorder"/>), each windowed
    /// and overlapped into the 36 samples at 6 + 6 w; the first 6 and last 6 are zero.
    /// </summary>
    public static void ShortImdct(ReadOnlySpan<double> lines, Span<double> output)
    {
        output.Clear();
        for (int w = 0; w < 3; w++)
        {
            for (int i = 0; i < 12; i++)
            {
                double sum = 0;
                for (int k = 0; k < 6; k++)
                {
                    sum += lines[(6 * w) + k] * ShortCosines[(6 * i) + k];
                }

                output[6 + (6 * w) + i] += sum * ShortWindow[i];
            }
        }
    }

    /// <summary>
    /// Moves a short block's lines from coded order (band by band, each band's windows in turn) to subband order: line f
    /// of window w to 18 (f / 6) + 6 w + f % 6, from short band <paramref name="shortStart"/> (3 in a mixed block) on.
    /// </summary>
    public static void Reorder(double[] spectrum, int rateIndex, int shortStart)
    {
        var shortBands = Mp3Tables.ShortBands[rateIndex];
        int start = 3 * shortBands[shortStart];
        Span<double> ordered = stackalloc double[576];
        int line = start;
        for (int band = shortStart; band < 13; band++)
        {
            int width = shortBands[band + 1] - shortBands[band];
            for (int w = 0; w < 3; w++)
            {
                for (int i = 0; i < width; i++)
                {
                    int f = shortBands[band] + i;
                    ordered[(18 * (f / 6)) + (6 * w) + (f % 6)] = spectrum[line++];
                }
            }
        }

        ordered[start..].CopyTo(spectrum.AsSpan(start));
    }

    private static double[] BuildCosines(int outputs, int inputs, int denominator, int offset)
    {
        var table = new double[outputs * inputs];
        for (int i = 0; i < outputs; i++)
        {
            for (int k = 0; k < inputs; k++)
            {
                table[(inputs * i) + k] = ExactMath.CosPi(((2L * i) + 1 + offset) * ((2 * k) + 1), denominator);
            }
        }

        return table;
    }

    // The windows of 2.4.3.4.10.3: sin(pi / 36 (i + 1/2)) for normal blocks; a start block's second half 1 for 6 samples,
    // then sin(pi / 12 (i - 18 + 1/2)), then 0; a stop block that mirrored.
    private static double[] Window(int blockType)
    {
        var window = new double[36];
        for (int i = 0; i < 36; i++)
        {
            double normal = ExactMath.SinPi((2 * i) + 1, 72);
            window[i] = blockType switch
            {
                1 => i < 18 ? normal : i < 24 ? 1 : i < 30 ? ExactMath.SinPi((2 * (i - 18)) + 1, 24) : 0,
                3 => i < 6 ? 0 : i < 12 ? ExactMath.SinPi((2 * (i - 6)) + 1, 24) : i < 18 ? 1 : normal,
                _ => normal,
            };
        }

        return window;
    }
}
