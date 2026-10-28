namespace ManagedCodecs.Aac.Sbr;

/// <summary>
/// Parametric stereo data of one frame (ISO/IEC 14496-3 8.3, ps_data, in a mono SBR element's extended data): the
/// envelopes' borders and, per envelope and parameter band, the inter-channel intensity difference (IID) and coherence
/// (ICC) indices, their deltas summed (8.6.4.5). The header's settings and the last envelope's values carry over to
/// later frames.
/// </summary>
/// <remarks>
/// The 10- and 20-band configurations are read; 34 bands are refused. The extension (ps_extension 0) brings the phase
/// parameters, the inter-channel and overall phase differences (IPD and OPD), per envelope in 5 or 11 bands (by the IID's
/// 10 or 20), as indices 0 to 7 (multiples of pi / 4) whose deltas are summed modulo 8; other extensions are skipped.
/// </remarks>
internal sealed class PsData
{
    public const int MaxEnvelopes = 5;

    private static readonly int[] FixedEnvelopes = [0, 1, 2, 4];
    private static readonly int[] ParameterBands = [10, 20, 34];
    private static readonly int[] PhaseBandCounts = [5, 11, 17];

    private static readonly SbrHuffman IidTime = new(PsTables.IidTime);
    private static readonly SbrHuffman IidFrequency = new(PsTables.IidFrequency);
    private static readonly SbrHuffman IidFineTime = new(PsTables.IidFineTime);
    private static readonly SbrHuffman IidFineFrequency = new(PsTables.IidFineFrequency);
    private static readonly SbrHuffman IccTime = new(PsTables.IccTime);
    private static readonly SbrHuffman IccFrequency = new(PsTables.IccFrequency);
    private static readonly Vlc IpdTime = new(PsTables.IpdTime);
    private static readonly Vlc IpdFrequency = new(PsTables.IpdFrequency);
    private static readonly Vlc OpdTime = new(PsTables.OpdTime);
    private static readonly Vlc OpdFrequency = new(PsTables.OpdFrequency);

    // The last envelope's values (at the band count they were sent in), which the next ones' time deltas add to.
    private readonly int[] _lastIid = new int[34];
    private readonly int[] _lastIcc = new int[34];
    private int _lastIidBands = 20;
    private int _lastIccBands = 20;
    private readonly int[] _lastIpd = new int[17];
    private readonly int[] _lastOpd = new int[17];
    private int _lastPhaseBands;

    /// <summary>Whether a header has come: until then the data can't be read.</summary>
    public bool HasHeader { get; private set; }

    public bool EnableIid { get; private set; }

    /// <summary>iid_mode: 10, 20 or 34 bands (0 to 2), the same with fine quantization (3 to 5).</summary>
    public int IidMode { get; private set; }

    public bool EnableIcc { get; private set; }

    /// <summary>icc_mode: 10, 20 or 34 bands with mixing procedure Ra (0 to 2), or with Rb (3 to 5).</summary>
    public int IccMode { get; private set; }

    public bool EnableExtension { get; private set; }

    public bool FineIid => IidMode >= 3;

    public int IidBands => ParameterBands[IidMode % 3];

    public int IccBands => ParameterBands[IccMode % 3];

    /// <summary>
    /// The IPD and OPD's bands: 5, 11 or 17, by the iid_mode of the last header that enabled IID, as FFmpeg keeps it
    /// (0 before one: no phase values are read). The FDK encoder's first header with phases has IID disabled.
    /// </summary>
    public int PhaseBands { get; private set; }

    /// <summary>enable_ipdopd: whether this frame's extension sent phase parameters.</summary>
    public bool EnableIpdOpd { get; private set; }

    /// <summary>frame_class: fixed (false) or variable (true) envelope borders.</summary>
    public bool VariableBorders { get; private set; }

    /// <summary>Envelopes this frame; 0 sends none (the previous frame's last values hold).</summary>
    public int NumEnvelopes { get; private set; }

    /// <summary>The time slot each envelope ends at (border_position), as sent; fixed borders are (32 (e + 1)) / num_env - 1.</summary>
    public int[] Borders { get; } = new int[MaxEnvelopes];

    /// <summary>IID and ICC indices per envelope and parameter band (IID from -7 to 7, or -15 to 15 fine; ICC 0 to 7), 0 where not enabled.</summary>
    public int[][] Iid { get; } = [.. Enumerable.Range(0, MaxEnvelopes).Select(_ => new int[34])];

    public int[][] Icc { get; } = [.. Enumerable.Range(0, MaxEnvelopes).Select(_ => new int[34])];

    /// <summary>IPD and OPD indices (0 to 7) per envelope and band, 0 where not sent.</summary>
    public int[][] Ipd { get; } = [.. Enumerable.Range(0, MaxEnvelopes).Select(_ => new int[17])];

    public int[][] Opd { get; } = [.. Enumerable.Range(0, MaxEnvelopes).Select(_ => new int[17])];

    /// <summary>Reads ps_data from <paramref name="r"/>, at most <paramref name="bits"/> bits; false if it can't be read yet (no header).</summary>
    public bool Read(AacBitReader r, int bits, AacStatistics statistics)
    {
        int end = r.Position + bits;
        if (r.ReadFlag())
        {
            HasHeader = true;
            EnableIid = r.ReadFlag();
            IidMode = EnableIid ? r.Read(3) : 0;
            EnableIcc = r.ReadFlag();
            IccMode = EnableIcc ? r.Read(3) : 0;
            EnableExtension = r.ReadFlag();
            if (IidMode > 5 || IccMode > 5)
            {
                throw new InvalidDataException($"Parametric stereo iid_mode {IidMode}, icc_mode {IccMode}.");
            }

            PhaseBands = EnableIid ? PhaseBandCounts[IidMode % 3] : PhaseBands;

            if ((EnableIid && IidBands == 34) || (EnableIcc && IccBands == 34))
            {
                throw new NotSupportedException("Parametric stereo with 34 bands is not supported.");
            }

            statistics.PsHeaders++;
            statistics.PsIidModes |= EnableIid ? 1 << IidMode : 0;
            statistics.PsIccModes |= EnableIcc ? 1 << IccMode : 0;
        }

        if (!HasHeader)
        {
            return false;
        }

        VariableBorders = r.ReadFlag();
        int index = r.Read(2);
        NumEnvelopes = VariableBorders ? index + 1 : FixedEnvelopes[index];
        for (int e = 0; e < NumEnvelopes; e++)
        {
            Borders[e] = VariableBorders ? r.Read(5) : (32 * (e + 1) / NumEnvelopes) - 1;
        }

        // Every envelope's IID, then every envelope's ICC.
        for (int e = 0; e < NumEnvelopes; e++)
        {
            if (EnableIid)
            {
                bool time = r.ReadFlag();
                var book = time ? (FineIid ? IidFineTime : IidTime) : (FineIid ? IidFineFrequency : IidFrequency);
                int limit = FineIid ? 15 : 7;
                DecodeDeltas(r, book, time, Iid[e], IidBands, e == 0 ? _lastIid : Iid[e - 1], e == 0 ? _lastIidBands : IidBands, -limit, limit, "IID");
                statistics.PsTimeDeltas += time ? 1 : 0;
            }
            else
            {
                Array.Clear(Iid[e]);
            }
        }

        for (int e = 0; e < NumEnvelopes; e++)
        {
            if (EnableIcc)
            {
                bool time = r.ReadFlag();
                DecodeDeltas(r, time ? IccTime : IccFrequency, time, Icc[e], IccBands, e == 0 ? _lastIcc : Icc[e - 1], e == 0 ? _lastIccBands : IccBands, 0, 7, "ICC");
                statistics.PsTimeDeltas += time ? 1 : 0;
            }
            else
            {
                Array.Clear(Icc[e]);
            }
        }

        if (NumEnvelopes > 0)
        {
            Iid[NumEnvelopes - 1].CopyTo(_lastIid, 0);
            Icc[NumEnvelopes - 1].CopyTo(_lastIcc, 0);
            _lastIidBands = IidBands;
            _lastIccBands = IccBands;
        }

        EnableIpdOpd = false;
        if (EnableExtension)
        {
            // ps_extension, of a counted number of bytes: extensions while a byte's worth is left, each a 2-bit id and
            // its data, then fill. Only extension 0 (IPD and OPD) is defined; any other takes the rest.
            int count = r.Read(4);
            if (count == 15)
            {
                count += r.Read(8);
            }

            int extensionEnd = r.Position + (8 * count);
            while (extensionEnd - r.Position > 7)
            {
                int id = r.Read(2);
                if (id != 0)
                {
                    break;
                }

                ReadPhases(r, statistics);
            }

            if (r.Position > extensionEnd)
            {
                throw new InvalidDataException($"Parametric stereo extension runs {r.Position - extensionEnd} bits past its size.");
            }

            r.Skip(extensionEnd - r.Position);
            statistics.PsExtensions++;
        }

        if (!EnableIpdOpd)
        {
            for (int e = 0; e < NumEnvelopes; e++)
            {
                Array.Clear(Ipd[e]);
                Array.Clear(Opd[e]);
            }
        }

        if (NumEnvelopes > 0)
        {
            Ipd[NumEnvelopes - 1].CopyTo(_lastIpd, 0);
            Opd[NumEnvelopes - 1].CopyTo(_lastOpd, 0);
            _lastPhaseBands = PhaseBands;
        }

        if (r.Position > end)
        {
            throw new InvalidDataException($"Parametric stereo data runs {r.Position - end} bits past its extension.");
        }

        statistics.PsFrames++;
        statistics.PsEnvelopes += NumEnvelopes;
        statistics.PsVariableBorderFrames += VariableBorders ? 1 : 0;
        return true;
    }

    // Extension 0: enable_ipdopd, per envelope the IPD's and then the OPD's time flag and deltas, and reserved_ps.
    private void ReadPhases(AacBitReader r, AacStatistics statistics)
    {
        EnableIpdOpd = r.ReadFlag();
        if (EnableIpdOpd)
        {
            for (int e = 0; e < NumEnvelopes; e++)
            {
                bool time = r.ReadFlag();
                DecodePhases(r, time ? IpdTime : IpdFrequency, time, Ipd[e], PhaseBands, e == 0 ? _lastIpd : Ipd[e - 1], e == 0 ? _lastPhaseBands : PhaseBands);
                time = r.ReadFlag();
                DecodePhases(r, time ? OpdTime : OpdFrequency, time, Opd[e], PhaseBands, e == 0 ? _lastOpd : Opd[e - 1], e == 0 ? _lastPhaseBands : PhaseBands);
            }

            statistics.PsIpdOpdFrames++;
        }

        r.Skip(1);
    }

    // As DecodeDeltas, modulo 8 (phases from 0 to 7 pi / 4), mapped between 5 and 11 bands as the IID's between 10 and
    // 20; the 11th band, which 5 bands lack, from 0.
    private static void DecodePhases(AacBitReader r, Vlc book, bool time, int[] values, int bands, int[] previous, int previousBands)
    {
        Array.Clear(values);
        for (int b = 0; b < bands; b++)
        {
            int start = !time ? (b == 0 ? 0 : values[b - 1])
                : previousBands == bands ? previous[b]
                : previousBands > bands ? previous[2 * b]
                : b / 2 < previousBands ? previous[b / 2] : 0;
            values[b] = (start + r.Read(book)) & 7;
        }
    }

    // 8.6.4.5: values from deltas in frequency (from 0) or in time (from the previous envelope's, mapped between 10 and
    // 20 bands: band b of 10 is band 2b of 20, band b of 20 band b / 2 of 10), which must stay in range.
    private static void DecodeDeltas(AacBitReader r, SbrHuffman book, bool time, int[] values, int bands, int[] previous, int previousBands, int min, int max, string name)
    {
        for (int b = 0; b < bands; b++)
        {
            int delta = book.Read(r);
            int start = !time ? (b == 0 ? 0 : values[b - 1])
                : previousBands == bands ? previous[b]
                : previousBands > bands ? previous[2 * b]
                : previous[b / 2];
            int value = start + delta;
            if (value < min || value > max)
            {
                throw new InvalidDataException($"Parametric stereo {name} index {value} out of range.");
            }

            values[b] = value;
        }
    }
}
