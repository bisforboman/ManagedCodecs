namespace ManagedCodecs.Mp3;

/// <summary>One granule of one channel's side information (ISO/IEC 11172-3 2.4.1.7; 13818-3 2.4.1.7).</summary>
internal sealed class Granule
{
    public int Part23Length { get; set; }

    /// <summary>Pairs of lines coded with the big-value tables (at most 288).</summary>
    public int BigValues { get; set; }

    public int GlobalGain { get; set; }

    public int ScalefacCompress { get; set; }

    /// <summary>0 normal, 1 start, 2 short, 3 stop.</summary>
    public int BlockType { get; set; }

    /// <summary>Short blocks above the lowest two subbands only (mixed_block_flag).</summary>
    public bool Mixed { get; set; }

    public int[] TableSelect { get; } = new int[3];

    public int[] SubblockGain { get; } = new int[3];

    /// <summary>The first line of region 1 and of region 2 (the big values' table regions).</summary>
    public int Region1Start { get; set; }

    /// <inheritdoc cref="Region1Start"/>
    public int Region2Start { get; set; }

    public bool Preflag { get; set; }

    public int ScalefacScale { get; set; }

    /// <summary>The count1 table: 0 table A, 1 table B.</summary>
    public int Count1Table { get; set; }

    /// <summary>MPEG-1's scfsi for this channel: scalefactor band group g's are granule 0's when bit (3 - g) is set.</summary>
    public int Scfsi { get; set; }

    public bool ShortBlocks => BlockType == 2;

    /// <summary>
    /// Reads one granule's side information for one channel, then works out its table regions in lines for the
    /// sample rate (<paramref name="rateIndex"/>): from region0_count and region1_count, or for a window-switching block
    /// from the implied counts (short blocks, mixed or not, as FFmpeg's decoder; long bands 0 to 7 otherwise).
    /// </summary>
    public void Read(ref BitReader r, bool lsf, int rateIndex)
    {
        Part23Length = r.Read(12);
        BigValues = r.Read(9);
        if (BigValues > 288)
        {
            throw new InvalidDataException("big_values is above 288.");
        }

        GlobalGain = r.Read(8);
        ScalefacCompress = r.Read(lsf ? 9 : 4);
        var longBands = Mp3Tables.LongBands[rateIndex];
        if (r.ReadBit() == 1)
        {
            BlockType = r.Read(2);
            if (BlockType == 0)
            {
                throw new InvalidDataException("A window-switching granule has block type 0.");
            }

            Mixed = r.ReadBit() == 1;
            TableSelect[0] = r.Read(5);
            TableSelect[1] = r.Read(5);
            TableSelect[2] = 0;
            for (int w = 0; w < 3; w++)
            {
                SubblockGain[w] = r.Read(3);
            }

            // region0_count is 7 (8 for short blocks) and region 1 takes the rest (2.4.2.7): long bands 0 to 7, or for short
            // blocks three short bands of three windows (36 lines, 72 at 8 kHz); mixed blocks too, as FFmpeg's decoder.
            Region1Start = BlockType == 2 ? 3 * Mp3Tables.ShortBands[rateIndex][3] : longBands[8];
            Region2Start = 576;
        }
        else
        {
            BlockType = 0;
            Mixed = false;
            for (int i = 0; i < 3; i++)
            {
                TableSelect[i] = r.Read(5);
            }

            SubblockGain[0] = SubblockGain[1] = SubblockGain[2] = 0;
            int region0 = r.Read(4);
            int region1 = r.Read(3);
            Region1Start = longBands[Math.Min(region0 + 1, 22)];
            Region2Start = longBands[Math.Min(region0 + region1 + 2, 22)];
        }

        Preflag = !lsf && r.ReadBit() == 1; // LSF: set by the scalefactors' compression (13818-3 2.4.3.2)
        ScalefacScale = r.ReadBit();
        Count1Table = r.ReadBit();
    }
}
