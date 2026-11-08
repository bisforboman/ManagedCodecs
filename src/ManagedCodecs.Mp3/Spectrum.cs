namespace ManagedCodecs.Mp3;

/// <summary>
/// A granule's spectrum for one channel: its scalefactors, Huffman-coded lines and their requantized values (ISO/IEC
/// 11172-3 2.4.3.4; 13818-3 2.4.3.2 for the lower rates' scalefactors), in the order they are coded: short blocks band
/// by band, each band's three windows in turn.
/// </summary>
/// <remarks>
/// The scalefactors are kept as one array of 40 as FFmpeg's decoder keeps them: long bands at their index, then each
/// short band's three windows, from the first short band (3 in mixed blocks, after the long ones); MPEG-1's scfsi copies
/// granule 0's by index.
/// </remarks>
internal static class Spectrum
{
    // Linbits by table_select (2.4.2.7, Table B.7).
    private static readonly byte[] Linbits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 6, 8, 10, 13, 4, 5, 6, 7, 8, 9, 11, 13];

    // The code table by table_select: null for 0 (all zeros), and for 4 and 14, which the spec doesn't use and FFmpeg's
    // decoder reads as table 0.
    private static readonly Vlc?[] Tables = BuildTables();

    private static readonly Vlc Count1A = new(HuffmanTables.Count1A);

    // |x|^(4/3) for every value a big-value table with 13 linbits can code (15 + 8191).
    private static readonly double[] PowFourThirds = [.. Enumerable.Range(0, 15 + 8192).Select(ExactMath.FourThirdsPower)];

    /// <summary>The long bands of a mixed block: below line 36 (72 at 8 kHz, FFmpeg's reading of MPEG-2.5).</summary>
    public static int MixedLongBands(bool lsf) => lsf ? 6 : 8;

    /// <summary>
    /// Reads MPEG-1 scalefactors (2.4.2.7) into <paramref name="scalefactors"/>: granule 1 keeps granule 0's for a band
    /// group whose scfsi bit is set (<paramref name="previous"/>, granule 0's array).
    /// </summary>
    public static void ReadMpeg1Scalefactors(ref BitReader r, Granule g, int granule, int[] scalefactors, int[] previous)
    {
        int slen1 = Mp3Tables.Slen1[g.ScalefacCompress];
        int slen2 = Mp3Tables.Slen2[g.ScalefacCompress];
        if (g.ShortBlocks)
        {
            int j = 0;
            int first = g.Mixed ? 17 : 18; // mixed: 8 long bands and short bands 3 to 5
            for (int i = 0; i < first; i++)
            {
                scalefactors[j++] = r.Read(slen1);
            }

            for (int i = 0; i < 18; i++)
            {
                scalefactors[j++] = r.Read(slen2);
            }

            while (j < 40)
            {
                scalefactors[j++] = 0;
            }

            return;
        }

        // Long blocks: band groups 0-5, 6-10, 11-15 and 16-20.
        int band = 0;
        for (int group = 0; group < 4; group++)
        {
            int count = group == 0 ? 6 : 5;
            int bits = group < 2 ? slen1 : slen2;
            bool copy = granule == 1 && (g.Scfsi & (8 >> group)) != 0;
            for (int i = 0; i < count; i++, band++)
            {
                scalefactors[band] = copy ? previous[band] : r.Read(bits);
            }
        }

        for (; band < 40; band++)
        {
            scalefactors[band] = 0;
        }
    }

    /// <summary>
    /// Reads the lower rates' scalefactors (13818-3 2.4.3.2): their bit lengths from scalefac_compress, the intensity
    /// channel's (<paramref name="intensityChannel"/>) by its own ranges, in four partitions. <paramref name="illegal"/>
    /// marks, for the intensity channel, a scalefactor at its partition's largest value: an illegal intensity position.
    /// Sets the granule's preflag.
    /// </summary>
    public static void ReadLsfScalefactors(ref BitReader r, Granule g, bool intensityChannel, int[] scalefactors, bool[] illegal)
    {
        Span<int> slen = stackalloc int[4];
        int table;
        int sfc = g.ScalefacCompress;
        g.Preflag = false;
        if (intensityChannel)
        {
            sfc >>= 1; // bit 0 is intensity_scale
            if (sfc < 180)
            {
                Expand(slen, sfc, 6, 6, 0);
                table = 3;
            }
            else if (sfc < 244)
            {
                Expand(slen, sfc - 180, 4, 4, 0);
                table = 4;
            }
            else
            {
                Expand(slen, sfc - 244, 3, 0, 0);
                table = 5;
            }
        }
        else if (sfc < 400)
        {
            Expand(slen, sfc, 5, 4, 4);
            table = 0;
        }
        else if (sfc < 500)
        {
            Expand(slen, sfc - 400, 5, 4, 0);
            table = 1;
        }
        else
        {
            Expand(slen, sfc - 500, 3, 0, 0);
            table = 2;
            g.Preflag = true;
        }

        var counts = Mp3Tables.LsfPartitions[table][g.ShortBlocks ? (g.Mixed ? 2 : 1) : 0];
        int j = 0;
        for (int k = 0; k < 4; k++)
        {
            int bits = slen[k];
            int max = (1 << bits) - 1;
            for (int i = 0; i < counts[k]; i++, j++)
            {
                int value = r.Read(bits);
                scalefactors[j] = value;
                illegal[j] = intensityChannel && bits > 0 && value == max;
            }
        }

        for (; j < 40; j++)
        {
            scalefactors[j] = 0;
            illegal[j] = false;
        }
    }

    // slen[0..3] from a number whose digits in bases n1, n2 and n3 (from the last) are slen[1], slen[2] and slen[3].
    private static void Expand(Span<int> slen, int sf, int n1, int n2, int n3)
    {
        slen[3] = n3 != 0 ? sf % n3 : 0;
        sf = n3 != 0 ? sf / n3 : sf;
        slen[2] = n2 != 0 ? sf % n2 : 0;
        sf = n2 != 0 ? sf / n2 : sf;
        slen[1] = n1 != 0 ? sf % n1 : 0;
        sf = n1 != 0 ? sf / n1 : sf;
        slen[0] = sf;
    }

    /// <summary>
    /// Decodes the Huffman-coded lines (2.4.2.7, 2.4.3.4.4) from the reader's position up to
    /// <paramref name="end"/> (in bits) into <paramref name="lines"/>: the big values' pairs region by region, then
    /// count1 quadruples while bits remain. As FFmpeg's decoder, a quadruple that reads past the end is dropped, and the
    /// reader is left at the end. Returns the number of lines up to the last that may be nonzero.
    /// </summary>
    public static int ReadLines(ref BitReader r, Granule g, int end, int[] lines, Mp3Statistics statistics)
    {
        int bigEnd = g.BigValues * 2;
        int i = 0;
        for (int region = 0; region < 3; region++)
        {
            int regionEnd = Math.Min(bigEnd, region == 0 ? g.Region1Start : region == 1 ? g.Region2Start : 576);
            int select = g.TableSelect[region];
            var table = Tables[select];
            if (table is null)
            {
                for (; i < regionEnd; i++)
                {
                    lines[i] = 0;
                }

                continue;
            }

            int linbits = Linbits[select];
            for (; i < regionEnd; i += 2)
            {
                int value = table.Lookup((uint)r.Peek(table.MaxLength), out int length);
                r.Position += length;
                lines[i] = Value(ref r, value >> 4, linbits);
                lines[i + 1] = Value(ref r, value & 15, linbits);
            }
        }

        // Every region ends at a band edge or at big_values * 2, all even: i is now big_values * 2.
        int lastPosition = -1;
        while (i <= 572)
        {
            int position = r.Position;
            if (position >= end)
            {
                if (position > end && lastPosition >= 0)
                {
                    // The last quadruple read past the granule's end: some encoders give a wrong length; it is dropped.
                    i -= 4;
                    r.Position = lastPosition;
                    statistics.DroppedQuadruples++;
                }

                break;
            }

            lastPosition = position;
            int quad;
            if (g.Count1Table == 0)
            {
                quad = Count1A.Lookup((uint)r.Peek(Count1A.MaxLength), out int length);
                r.Position += length;
            }
            else
            {
                quad = 15 - r.Read(4);
            }

            for (int k = 0; k < 4; k++)
            {
                int magnitude = (quad >> (3 - k)) & 1;
                lines[i + k] = magnitude != 0 && r.ReadBit() == 1 ? -1 : magnitude;
            }

            i += 4;
        }

        int count = i;
        for (; i < 576; i++)
        {
            lines[i] = 0;
        }

        r.Position = end;
        return count;
    }

    // A big value's magnitude with its linbits (for 15) and sign bit.
    private static int Value(ref BitReader r, int magnitude, int linbits)
    {
        if (magnitude == 15 && linbits > 0)
        {
            magnitude += r.Read(linbits);
        }

        return magnitude != 0 && r.ReadBit() == 1 ? -magnitude : magnitude;
    }

    /// <summary>
    /// Requantizes the first <paramref name="count"/> lines (2.4.3.4.7): sign(x) |x|^(4/3) times 2 to the power of a
    /// quarter of (global_gain - 210), less 8 subblock_gain for a short window, less the band's scalefactor (plus the
    /// preemphasis on long bands with preflag) shifted by scalefac_scale + 1.
    /// </summary>
    public static void Requantize(Granule g, bool lsf, int rateIndex, int[] lines, int count, int[] scalefactors, double[] spectrum)
    {
        Array.Clear(spectrum);
        int gain = g.GlobalGain - 210;
        int shift = g.ScalefacScale + 1;
        var longBands = Mp3Tables.LongBands[rateIndex];
        int longEnd = g.ShortBlocks ? (g.Mixed ? MixedLongBands(lsf) : 0) : 22;
        int line = 0;
        for (int band = 0; band < longEnd && line < count; band++)
        {
            int quarters = gain - ((scalefactors[band] + (g.Preflag ? Mp3Tables.Pretab[band] : 0)) << shift);
            line = RequantizeRun(lines, spectrum, line, Math.Min(longBands[band + 1], count), ExactMath.QuarterPowerOfTwo(quarters));
        }

        if (!g.ShortBlocks)
        {
            return;
        }

        var shortBands = Mp3Tables.ShortBands[rateIndex];
        int shortStart = g.Mixed ? 3 : 0;
        line = 3 * shortBands[shortStart];
        for (int band = shortStart; band < 13 && line < count; band++)
        {
            int width = shortBands[band + 1] - shortBands[band];
            for (int w = 0; w < 3; w++)
            {
                int sf = scalefactors[longEnd + (3 * (band - shortStart)) + w];
                int quarters = gain - (g.SubblockGain[w] << 3) - (sf << shift);
                line = RequantizeRun(lines, spectrum, line, Math.Min(line + width, count), ExactMath.QuarterPowerOfTwo(quarters));
            }
        }
    }

    private static int RequantizeRun(int[] lines, double[] spectrum, int start, int end, double scale)
    {
        for (int i = start; i < end; i++)
        {
            int x = lines[i];
            double magnitude = PowFourThirds[Math.Abs(x)] * scale;
            spectrum[i] = x < 0 ? -magnitude : magnitude;
        }

        return Math.Max(start, end);
    }

    private static Vlc?[] BuildTables()
    {
        var tables = new Vlc?[32];
        string[]?[] codes =
        [
            null, HuffmanTables.Table1, HuffmanTables.Table2, HuffmanTables.Table3, null, HuffmanTables.Table5,
            HuffmanTables.Table6, HuffmanTables.Table7, HuffmanTables.Table8, HuffmanTables.Table9, HuffmanTables.Table10,
            HuffmanTables.Table11, HuffmanTables.Table12, HuffmanTables.Table13, null, HuffmanTables.Table15,
        ];
        for (int i = 0; i < 16; i++)
        {
            tables[i] = codes[i] is { } c ? new Vlc(c) : null;
        }

        var table16 = new Vlc(HuffmanTables.Table16);
        var table24 = new Vlc(HuffmanTables.Table24);
        for (int i = 16; i < 24; i++)
        {
            tables[i] = table16;
            tables[i + 8] = table24;
        }

        return tables;
    }
}
