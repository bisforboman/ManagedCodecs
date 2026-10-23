namespace ManagedCodecs.Aac.Sbr;

/// <summary>
/// One channel's SBR data of one frame as coded (ISO/IEC 14496-3 4.4.2.8): the time grid, the delta coding directions,
/// the inverse filtering levels, the envelope and noise floor values (deltas, or a first value and deltas), and the
/// sinusoids to add.
/// </summary>
internal sealed class SbrChannelData
{
    public const int FixFix = 0;
    public const int FixVar = 1;
    public const int VarFix = 2;
    public const int VarVar = 3;

    public int FrameClass { get; private set; }

    public int NumEnvelopes { get; private set; }

    /// <summary>Envelope borders in time slots (t_E, NumEnvelopes + 1 of them), and each envelope's frequency resolution.</summary>
    public int[] Borders { get; } = new int[6];

    public int[] FrequencyResolution { get; } = new int[5];

    /// <summary>The envelope a transient starts in (l_A), or -1.</summary>
    public int TransientEnvelope { get; private set; }

    public int NumNoiseFloors { get; private set; }

    /// <summary>Noise floor borders in time slots (t_Q).</summary>
    public int[] NoiseBorders { get; } = new int[3];

    /// <summary>Whether each envelope and noise floor is coded as deltas in time (bs_df_env, bs_df_noise) rather than in frequency.</summary>
    public bool[] DeltaTimeEnvelope { get; } = new bool[5];

    public bool[] DeltaTimeNoise { get; } = new bool[2];

    /// <summary>bs_invf_mode per noise floor band.</summary>
    public int[] InverseFilteringMode { get; } = new int[5];

    /// <summary>The amplitude resolution the envelopes are coded with (0: 1.5 dB, 1: 3 dB).</summary>
    public int AmpResolution { get; private set; }

    /// <summary>Envelope values as coded, per envelope and band; noise floor values per noise floor and band.</summary>
    public int[][] Envelope { get; } = [.. Enumerable.Range(0, 5).Select(_ => new int[64])];

    public int[][] NoiseFloor { get; } = [new int[5], new int[5]];

    /// <summary>bs_add_harmonic per high-resolution band.</summary>
    public bool[] AddHarmonic { get; } = new bool[64];

    /// <summary>
    /// sbr_grid (Table 4.65): the frame class, envelope borders and resolutions, and the noise floor borders (4.6.18.3.3),
    /// for frames of 16 time slots (1024-sample cores).
    /// </summary>
    public void ReadGrid(AacBitReader r)
    {
        int frameClass = r.Read(2);
        Span<int> relativeLead = stackalloc int[4];
        Span<int> relativeTrail = stackalloc int[4];
        int numEnvelopes;
        int absoluteLead = 0;
        int absoluteTrail = 16;
        int numLead = 0;
        int numTrail = 0;
        int pointer = 0;
        switch (frameClass)
        {
            case FixFix:
                numEnvelopes = 1 << r.Read(2);
                if (numEnvelopes > 4)
                {
                    throw new InvalidDataException($"{numEnvelopes} SBR envelopes in a FIXFIX frame.");
                }

                int resolution = r.Read(1);
                FrequencyResolution.AsSpan(0, numEnvelopes).Fill(resolution);
                numLead = numEnvelopes - 1;
                for (int k = 0; k < numLead; k++)
                {
                    relativeLead[k] = 16 / numEnvelopes;
                }

                break;

            case FixVar:
            case VarFix:
                int variableBorder = r.Read(2);
                int numRelative = r.Read(2);
                numEnvelopes = numRelative + 1;
                var relative = frameClass == FixVar ? relativeTrail : relativeLead;
                for (int k = 0; k < numRelative; k++)
                {
                    relative[k] = (2 * r.Read(2)) + 2;
                }

                pointer = r.Read(PointerBits(numEnvelopes));
                for (int l = 0; l < numEnvelopes; l++)
                {
                    // FIXVAR codes the resolutions from the last envelope back.
                    FrequencyResolution[frameClass == FixVar ? numEnvelopes - 1 - l : l] = r.Read(1);
                }

                if (frameClass == FixVar)
                {
                    absoluteTrail = 16 + variableBorder;
                    numTrail = numRelative;
                }
                else
                {
                    absoluteLead = variableBorder;
                    numLead = numRelative;
                }

                break;

            default: // VARVAR
                absoluteLead = r.Read(2);
                absoluteTrail = 16 + r.Read(2);
                numLead = r.Read(2);
                numTrail = r.Read(2);
                numEnvelopes = numLead + numTrail + 1;
                if (numEnvelopes > 5)
                {
                    throw new InvalidDataException($"{numEnvelopes} SBR envelopes, more than 5.");
                }

                for (int k = 0; k < numLead; k++)
                {
                    relativeLead[k] = (2 * r.Read(2)) + 2;
                }

                for (int k = 0; k < numTrail; k++)
                {
                    relativeTrail[k] = (2 * r.Read(2)) + 2;
                }

                pointer = r.Read(PointerBits(numEnvelopes));
                for (int l = 0; l < numEnvelopes; l++)
                {
                    FrequencyResolution[l] = r.Read(1);
                }

                break;
        }

        FrameClass = frameClass;
        NumEnvelopes = numEnvelopes;

        // t_E: the lead borders count up from the first, the trail ones down from the last.
        Borders[0] = absoluteLead;
        Borders[numEnvelopes] = absoluteTrail;
        for (int l = 1; l <= numLead; l++)
        {
            Borders[l] = Borders[l - 1] + relativeLead[l - 1];
        }

        for (int l = numEnvelopes - 1; l > numLead; l--)
        {
            Borders[l] = Borders[l + 1] - relativeTrail[numEnvelopes - 1 - l];
        }

        for (int l = 0; l < numEnvelopes; l++)
        {
            if (Borders[l + 1] <= Borders[l])
            {
                throw new InvalidDataException("SBR envelope borders do not increase.");
            }
        }

        // l_A and the middle border the two noise floors split at (Tables 4.155 and 4.156).
        int middle;
        switch (frameClass)
        {
            case FixFix:
                middle = numEnvelopes / 2;
                TransientEnvelope = -1;
                break;
            case VarFix:
                middle = pointer switch { 0 => 1, 1 => numEnvelopes - 1, _ => pointer - 1 };
                TransientEnvelope = pointer <= 1 ? -1 : pointer - 1;
                break;
            default:
                middle = pointer <= 1 ? numEnvelopes - 1 : numEnvelopes + 1 - pointer;
                TransientEnvelope = pointer == 0 ? -1 : numEnvelopes + 1 - pointer;
                break;
        }

        if (TransientEnvelope > numEnvelopes || middle < 0 || middle > numEnvelopes)
        {
            throw new InvalidDataException($"SBR pointer {pointer} is past the frame's {numEnvelopes} envelopes.");
        }

        NumNoiseFloors = numEnvelopes > 1 ? 2 : 1;
        NoiseBorders[0] = Borders[0];
        NoiseBorders[1] = numEnvelopes > 1 ? Borders[middle] : Borders[numEnvelopes];
        NoiseBorders[2] = Borders[numEnvelopes];
    }

    /// <summary>A coupled pair's right channel takes the left one's grid.</summary>
    public void CopyGrid(SbrChannelData left)
    {
        FrameClass = left.FrameClass;
        NumEnvelopes = left.NumEnvelopes;
        TransientEnvelope = left.TransientEnvelope;
        NumNoiseFloors = left.NumNoiseFloors;
        left.Borders.CopyTo(Borders, 0);
        left.FrequencyResolution.CopyTo(FrequencyResolution, 0);
        left.NoiseBorders.CopyTo(NoiseBorders, 0);
    }

    /// <summary>sbr_dtdf (Table 4.67): the delta coding direction of each envelope and noise floor.</summary>
    public void ReadDeltaDirections(AacBitReader r)
    {
        for (int l = 0; l < NumEnvelopes; l++)
        {
            DeltaTimeEnvelope[l] = r.ReadFlag();
        }

        for (int l = 0; l < NumNoiseFloors; l++)
        {
            DeltaTimeNoise[l] = r.ReadFlag();
        }
    }

    /// <summary>sbr_invf (Table 4.68).</summary>
    public void ReadInverseFiltering(AacBitReader r, int numNoiseBands)
    {
        for (int k = 0; k < numNoiseBands; k++)
        {
            InverseFilteringMode[k] = r.Read(2);
        }
    }

    /// <summary>
    /// sbr_envelope (Table 4.69): per envelope, a first value and deltas in frequency, or deltas in time; a coupled pair's
    /// right channel codes its balance (in half steps). A single FIXFIX envelope is always coded at 1.5 dB.
    /// </summary>
    public void ReadEnvelopes(AacBitReader r, SbrFrequencies frequencies, int ampResolution, bool balance)
    {
        AmpResolution = FrameClass == FixFix && NumEnvelopes == 1 ? 0 : ampResolution;
        bool fine = AmpResolution == 0;
        var (timeBook, frequencyBook) = balance
            ? (fine ? SbrHuffman.BalanceTime15 : SbrHuffman.BalanceTime30, fine ? SbrHuffman.BalanceFrequency15 : SbrHuffman.BalanceFrequency30)
            : (fine ? SbrHuffman.EnvelopeTime15 : SbrHuffman.EnvelopeTime30, fine ? SbrHuffman.EnvelopeFrequency15 : SbrHuffman.EnvelopeFrequency30);
        int startBits = balance ? (fine ? 6 : 5) : (fine ? 7 : 6);
        for (int l = 0; l < NumEnvelopes; l++)
        {
            int bands = frequencies.Bands(FrequencyResolution[l]);
            var values = Envelope[l];
            int k = 0;
            if (!DeltaTimeEnvelope[l])
            {
                values[0] = r.Read(startBits);
                k = 1;
            }

            var book = DeltaTimeEnvelope[l] ? timeBook : frequencyBook;
            for (; k < bands; k++)
            {
                values[k] = book.Read(r);
            }
        }
    }

    /// <summary>sbr_noise (Table 4.70): as the envelopes, at 3 dB, with a 5-bit first value.</summary>
    public void ReadNoiseFloors(AacBitReader r, SbrFrequencies frequencies, bool balance)
    {
        var timeBook = balance ? SbrHuffman.NoiseBalanceTime : SbrHuffman.NoiseTime;
        var frequencyBook = balance ? SbrHuffman.BalanceFrequency30 : SbrHuffman.EnvelopeFrequency30;
        for (int l = 0; l < NumNoiseFloors; l++)
        {
            var values = NoiseFloor[l];
            int k = 0;
            if (!DeltaTimeNoise[l])
            {
                values[0] = r.Read(5);
                k = 1;
            }

            var book = DeltaTimeNoise[l] ? timeBook : frequencyBook;
            for (; k < frequencies.NoiseBands; k++)
            {
                values[k] = book.Read(r);
            }
        }
    }

    /// <summary>sbr_sinusoidal_coding (Table 4.71), behind bs_add_harmonic_flag.</summary>
    public void ReadHarmonics(AacBitReader r, SbrFrequencies frequencies)
    {
        bool present = r.ReadFlag();
        int bands = frequencies.Bands(1);
        for (int k = 0; k < bands; k++)
        {
            AddHarmonic[k] = present && r.ReadFlag();
        }
    }

    private static int PointerBits(int numEnvelopes) => numEnvelopes switch { 1 => 1, 2 or 3 => 2, _ => 3 };
}
