namespace ManagedCodecs.H264;

/// <summary>
/// A decoded picture in macroblock-aligned planes (4:2:0), with its motion field (which direct
/// prediction of later B pictures reads) and its reference marking state.
/// </summary>
internal sealed class Picture
{
    public Picture(int widthInMbs, int heightInMbs)
    {
        WidthInMbs = widthInMbs;
        HeightInMbs = heightInMbs;
        LumaStride = widthInMbs * 16;
        ChromaStride = widthInMbs * 8;
        Y = new byte[LumaStride * heightInMbs * 16];
        Cb = new byte[ChromaStride * heightInMbs * 8];
        Cr = new byte[ChromaStride * heightInMbs * 8];

        int mbCount = widthInMbs * heightInMbs;
        MvX = [new short[mbCount * 16], new short[mbCount * 16]];
        MvY = [new short[mbCount * 16], new short[mbCount * 16]];
        RefIdx = [new sbyte[mbCount * 4], new sbyte[mbCount * 4]];
        RefId = [new long[mbCount * 4], new long[mbCount * 4]];
    }

    public int WidthInMbs { get; }

    public int HeightInMbs { get; }

    public int LumaStride { get; }

    public int ChromaStride { get; }

    public byte[] Y { get; }

    public byte[] Cb { get; }

    public byte[] Cr { get; }

    /// <summary>
    /// Identifies the picture decoded into this object; pictures are reused once they stop being
    /// references, so anything that names a picture across pictures uses this, not the object.
    /// </summary>
    public long Id { get; set; }

    /// <summary>PicOrderCnt (8.2.1).</summary>
    public int Poc { get; set; }

    /// <summary>Motion vector per list (0, 1) and luma 4x4 block, in quarter samples.</summary>
    public short[][] MvX { get; }

    public short[][] MvY { get; }

    /// <summary>Reference index per list and 8x8 block; -1 where the list is not used (and in intra macroblocks).</summary>
    public sbyte[][] RefIdx { get; }

    /// <summary><see cref="Id"/> of the referenced picture per list and 8x8 block, where <see cref="RefIdx"/> is not -1.</summary>
    public long[][] RefId { get; }

    public int FrameNum { get; set; }

    /// <summary>FrameNumWrap (8.2.4.1), updated whenever reference lists are built.</summary>
    public int FrameNumWrap { get; set; }

    public bool IsShortTermReference { get; set; }

    public bool IsLongTermReference { get; set; }

    public int LongTermFrameIdx { get; set; }

    public bool IsReference => IsShortTermReference || IsLongTermReference;

    public byte[] Plane(int component) => component switch
    {
        0 => Y,
        1 => Cb,
        _ => Cr,
    };
}
