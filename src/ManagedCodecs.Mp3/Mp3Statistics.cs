namespace ManagedCodecs.Mp3;

/// <summary>What the frames decoded so far used, for the tests to check that each sample exercises what it is named for.</summary>
internal sealed class Mp3Statistics
{
    public int Frames { get; private set; }

    /// <summary>Frames at the lower sample rates (MPEG-2 and MPEG-2.5).</summary>
    public int LsfFrames { get; private set; }

    public int MidSideFrames { get; private set; }

    public int IntensityFrames { get; private set; }

    public int CrcFrames { get; private set; }

    /// <summary>Granules (of one channel) by block type: normal, start, short, stop.</summary>
    public int[] BlockTypes { get; } = new int[4];

    public int MixedGranules { get; private set; }

    /// <summary>Granules whose count1 lines use table B.</summary>
    public int Count1B { get; private set; }

    /// <summary>Granules using a table with linbits (16 to 31) in some region.</summary>
    public int LinbitsGranules { get; private set; }

    /// <summary>Frames whose main data starts in earlier frames' (main_data_begin above 0).</summary>
    public int ReservoirFrames { get; set; }

    /// <summary>Frames whose main data starts before the reservoir's start: their first granules come out silent.</summary>
    public int MissingReservoir { get; set; }

    /// <summary>Count1 quadruples dropped for reading past their granule's end.</summary>
    public int DroppedQuadruples { get; set; }

    public void Count(FrameHeader header)
    {
        Frames++;
        LsfFrames += header.Lsf ? 1 : 0;
        MidSideFrames += header.Channels == 2 && header.MidSide ? 1 : 0;
        IntensityFrames += header.Channels == 2 && header.Intensity ? 1 : 0;
        CrcFrames += header.HasCrc ? 1 : 0;
    }

    public void Count(Granule g)
    {
        BlockTypes[g.BlockType]++;
        MixedGranules += g.ShortBlocks && g.Mixed ? 1 : 0;
        Count1B += g.Count1Table;
        LinbitsGranules += g.TableSelect.Any(t => t >= 16) ? 1 : 0;
    }
}
