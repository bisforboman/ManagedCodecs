namespace ManagedCodecs.Aac.Sbr;

/// <summary>
/// The frequency band tables of SBR (ISO/IEC 14496-3 4.6.18.3.2), in QMF subbands of the output rate: the master table
/// from the header, the high- and low-resolution envelope tables and the noise floor table derived from it.
/// </summary>
internal sealed class SbrFrequencies
{
    public SbrFrequencies(SbrHeader header, int sampleRate)
    {
        K0 = StartBand(sampleRate, header.StartFrequency);
        K2 = StopBand(sampleRate, header.StopFrequency, K0);
        int maxWidth = sampleRate <= 32000 ? 48 : sampleRate == 44100 ? 35 : sampleRate >= 48000 ? 32 : 48;
        if (K2 <= K0 || K2 - K0 > maxWidth)
        {
            throw new InvalidDataException($"SBR bands {K0} to {K2} are out of range at {sampleRate} Hz.");
        }

        Master = header.FrequencyScale > 0
            ? LogarithmicMaster(K0, K2, header.FrequencyScale, header.AlterScale)
            : LinearMaster(K0, K2, header.AlterScale);
        int numMaster = Master.Length - 1;
        if (header.CrossoverBand >= numMaster)
        {
            throw new InvalidDataException($"SBR crossover band {header.CrossoverBand} is past the {numMaster} master bands.");
        }

        // 4.6.18.3.2.2: the high-resolution table from the crossover band on; the low-resolution one every other band.
        High = Master[header.CrossoverBand..];
        int numHigh = High.Length - 1;
        int numLow = (numHigh + 1) / 2;
        Low = new int[numLow + 1];
        Low[0] = High[0];
        for (int k = 1; k <= numLow; k++)
        {
            Low[k] = High[(2 * k) - (numHigh & 1)];
        }

        Kx = High[0];
        M = High[numHigh] - Kx;
        if (Kx > 32 || Kx + M > 64)
        {
            throw new InvalidDataException($"SBR range {Kx} to {Kx + M} is out of range.");
        }

        // The noise floor bands (4.6.18.3.2.3), from the low-resolution table.
        int numNoise = Math.Max(1, Nint(header.NoiseBands * Math.Log2((double)K2 / Kx)));
        if (numNoise > 5)
        {
            throw new InvalidDataException($"{numNoise} SBR noise floor bands, more than 5.");
        }

        Noise = new int[numNoise + 1];
        int index = 0;
        Noise[0] = Low[0];
        for (int k = 1; k <= numNoise; k++)
        {
            index += (numLow - index) / (numNoise + 1 - k);
            Noise[k] = Low[index];
        }

        (PatchStart, PatchBands) = BuildPatches(sampleRate);
        LimiterTables = [.. Enumerable.Range(0, 4).Select(BuildLimiterTable)];
    }

    /// <summary>The first and the (exclusive) last subband of the master table.</summary>
    public int K0 { get; }

    public int K2 { get; }

    /// <summary>The master table's band borders (f_Master).</summary>
    public int[] Master { get; }

    /// <summary>Band borders at high and low frequency resolution (f_TableHigh, f_TableLow) and of the noise floor (f_TableNoise).</summary>
    public int[] High { get; }

    public int[] Low { get; }

    public int[] Noise { get; }

    /// <summary>The first SBR subband (kx) and how many there are (M).</summary>
    public int Kx { get; }

    public int M { get; }

    /// <summary>
    /// The patches of the HF generator (4.6.18.6.3): patch p copies the subbands from PatchStart[p] up, PatchBands[p] of
    /// them, to the next PatchBands[p] subbands from kx up.
    /// </summary>
    public int[] PatchStart { get; }

    public int[] PatchBands { get; }

    /// <summary>The limiter band borders (4.6.18.3.2.4) for each bs_limiter_bands, 0 to 3.</summary>
    public int[][] LimiterTables { get; }

    /// <summary>Bands at frequency resolution 0 (low) or 1 (high).</summary>
    public int Bands(int resolution) => resolution == 0 ? Low.Length - 1 : High.Length - 1;

    public int NoiseBands => Noise.Length - 1;

    /// <summary>k0 (4.6.18.3.2.1): the lowest subband, from bs_start_freq.</summary>
    public static int StartBand(int sampleRate, int startFrequency)
    {
        int row = sampleRate switch
        {
            16000 => 0,
            22050 => 1,
            24000 => 2,
            32000 => 3,
            >= 44100 and <= 64000 => 4,
            > 64000 => 5,
            _ => 6,
        };
        return Nint(MinimumBand(sampleRate, 3000) / 1.0) + SbrTables.StartOffsets[row][startFrequency];
    }

    /// <summary>k2 (4.6.18.3.2.1): the subband above the highest, from bs_stop_freq.</summary>
    public static int StopBand(int sampleRate, int stopFrequency, int k0)
    {
        switch (stopFrequency)
        {
            case 14:
                return Math.Min(64, 2 * k0);
            case 15:
                return Math.Min(64, 3 * k0);
        }

        int stopMin = Nint(MinimumBand(sampleRate, 6000));
        Span<int> dk = stackalloc int[13];
        for (int p = 0; p < 13; p++)
        {
            dk[p] = Nint(stopMin * Math.Pow(64.0 / stopMin, (p + 1) / 13.0)) - Nint(stopMin * Math.Pow(64.0 / stopMin, p / 13.0));
        }

        dk.Sort();
        int k2 = stopMin;
        for (int p = 0; p < stopFrequency; p++)
        {
            k2 += dk[p];
        }

        return Math.Min(64, k2);
    }

    // The patch construction of 4.6.18.6.3 (its flowchart): from kx up, each patch copies the highest subbands below k0
    // it can (from an even subband distance, so the copy keeps the subbands' phase), ending on a master band border, up
    // to the goal subband of about 16 kHz and then to kx + M; a last patch of fewer than 3 subbands goes.
    private (int[] Start, int[] Bands) BuildPatches(int sampleRate)
    {
        var start = new List<int>();
        var bands = new List<int>();
        int numMaster = Master.Length - 1;
        int msb = K0;
        int usb = Kx;
        int goal = Nint(2.048e6 / sampleRate);
        int k = numMaster;
        if (goal < Kx + M)
        {
            k = 0;
            while (Master[k] < goal)
            {
                k++;
            }
        }

        int sb;
        int rounds = 0;
        do
        {
            if (++rounds > 64)
            {
                throw new InvalidDataException("The SBR patches do not reach the top band.");
            }

            int j = k + 1;
            int odd;
            do
            {
                if (--j < 0)
                {
                    throw new InvalidDataException("No SBR patch fits.");
                }

                sb = Master[j];
                odd = (sb - 2 + K0) % 2;
            }
            while (sb > K0 - 1 + msb - odd);

            int count = Math.Max(sb - usb, 0);
            if (count > 0)
            {
                if (start.Count == 5)
                {
                    throw new InvalidDataException("More than 5 SBR patches.");
                }

                start.Add(K0 - odd - count);
                bands.Add(count);
                usb = sb;
                msb = sb;
            }
            else
            {
                msb = Kx;
            }

            if (Master[k] - sb < 3)
            {
                k = numMaster;
            }
        }
        while (sb != Kx + M);

        if (bands.Count > 1 && bands[^1] < 3)
        {
            start.RemoveAt(start.Count - 1);
            bands.RemoveAt(bands.Count - 1);
        }

        if (start.Count == 0 || start[0] < 1)
        {
            throw new InvalidDataException("The SBR patches start below subband 1.");
        }

        return ([.. start], [.. bands]);
    }

    // 4.6.18.3.2.4: bs_limiter_bands 0 is one band over the whole range; 1 to 3 (1.2, 2 or 3 bands an octave) start
    // from the low-resolution table and the patch borders, and drop a border closer than 0.49 bands to the one below
    // it: the upper one unless it is a patch border, else the lower one unless that is one too.
    private int[] BuildLimiterTable(int limiterBands)
    {
        if (limiterBands == 0)
        {
            return [Low[0], Low[^1]];
        }

        double bandsPerOctave = limiterBands switch { 1 => 1.2, 2 => 2.0, _ => 3.0 };
        var patchBorders = new int[PatchBands.Length + 1];
        patchBorders[0] = Kx;
        for (int p = 0; p < PatchBands.Length; p++)
        {
            patchBorders[p + 1] = patchBorders[p] + PatchBands[p];
        }

        var table = new List<int>(Low);
        table.AddRange(patchBorders[1..^1]);
        table.Sort();
        int k = 1;
        while (k < table.Count)
        {
            double octaves = Math.Log2((double)table[k] / table[k - 1]);
            if (octaves * bandsPerOctave >= 0.49)
            {
                k++;
            }
            else if (table[k] == table[k - 1] || Array.IndexOf(patchBorders, table[k]) < 0)
            {
                table.RemoveAt(k);
            }
            else if (Array.IndexOf(patchBorders, table[k - 1]) < 0)
            {
                table.RemoveAt(k - 1);
            }
            else
            {
                k++;
            }
        }

        return [.. table];
    }

    // startMin and stopMin before rounding: a frequency of 3 or 6 kHz below 32 kHz, 4/3 and 5/3 of it from 32 and from
    // 64 kHz, in subbands of 64 over half the rate.
    private static double MinimumBand(int sampleRate, double frequency)
    {
        double scaled = sampleRate < 32000 ? frequency : sampleRate < 64000 ? frequency * 4 / 3 : frequency * 5 / 3;
        return scaled * 128 / sampleRate;
    }

    // 4.6.18.3.2.1, bs_freq_scale 1 to 3: bands per octave (12, 10 or 8), in one region from k0 to k2, or in two (k0 to
    // 2 k0, then to k2, warped by 1.3 with bs_alter_scale) when k2 is over 2.2449 times k0.
    private static int[] LogarithmicMaster(int k0, int k2, int frequencyScale, bool alterScale)
    {
        int bandsPerOctave = frequencyScale switch { 1 => 12, 2 => 10, _ => 8 };
        bool twoRegions = (double)k2 / k0 > 2.2449;
        int k1 = twoRegions ? 2 * k0 : k2;
        int numBands0 = 2 * Nint(bandsPerOctave * Math.Log2((double)k1 / k0) / 2);
        if (numBands0 <= 0)
        {
            throw new InvalidDataException("No SBR bands below the second region.");
        }

        var dk0 = Widths(k0, k1, numBands0);
        Array.Sort(dk0);
        if (!twoRegions)
        {
            return CumulativeSum(k0, dk0);
        }

        double warp = alterScale ? 1.3 : 1.0;
        int numBands1 = 2 * Nint(bandsPerOctave * Math.Log2((double)k2 / k1) / (2 * warp));
        if (numBands1 <= 0)
        {
            throw new InvalidDataException("No SBR bands in the second region.");
        }

        var dk1 = Widths(k1, k2, numBands1);
        Array.Sort(dk1);
        if (dk1[0] < dk0[^1])
        {
            // The second region's narrowest band widened to the first's widest, at its widest band's expense.
            int change = Math.Min(dk0[^1] - dk1[0], (dk1[^1] - dk1[0]) / 2);
            dk1[0] += change;
            dk1[^1] -= change;
            Array.Sort(dk1);
        }

        return [.. CumulativeSum(k0, dk0), .. CumulativeSum(k1, dk1)[1..]];
    }

    // 4.6.18.3.2.1, bs_freq_scale 0: bands of 1 subband (bs_alter_scale 0) or 2, the last ones (or first) a subband
    // narrower or wider to end at k2.
    private static int[] LinearMaster(int k0, int k2, bool alterScale)
    {
        int dk = alterScale ? 2 : 1;
        int numBands = alterScale ? 2 * Nint((k2 - k0) / 4.0) : 2 * ((k2 - k0) / 2);
        if (numBands <= 0)
        {
            throw new InvalidDataException("No SBR master bands.");
        }

        var widths = new int[numBands];
        Array.Fill(widths, dk);
        int diff = k2 - (k0 + (numBands * dk));
        int increment = diff < 0 ? 1 : -1;
        for (int k = diff < 0 ? 0 : numBands - 1; diff != 0; k += increment, diff += increment)
        {
            widths[k] -= increment;
        }

        return CumulativeSum(k0, widths);
    }

    // The widths of numBands bands from a to b, evenly spaced on a log scale.
    private static int[] Widths(int a, int b, int numBands)
    {
        var widths = new int[numBands];
        for (int k = 0; k < numBands; k++)
        {
            widths[k] = Nint(a * Math.Pow((double)b / a, (k + 1) / (double)numBands)) - Nint(a * Math.Pow((double)b / a, k / (double)numBands));
            if (widths[k] <= 0)
            {
                throw new InvalidDataException("An SBR master band is empty.");
            }
        }

        return widths;
    }

    private static int[] CumulativeSum(int start, int[] widths)
    {
        var borders = new int[widths.Length + 1];
        borders[0] = start;
        for (int k = 0; k < widths.Length; k++)
        {
            borders[k + 1] = borders[k] + widths[k];
        }

        return borders;
    }

    /// <summary>NINT (1.5): the nearest integer, halves up.</summary>
    private static int Nint(double x) => (int)Math.Floor(x + 0.5);
}
