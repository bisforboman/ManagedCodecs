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
        Frame = this;
        Parity = -1;
    }

    /// <summary>A view of one field of <paramref name="frame"/>: the same arrays, every other row.</summary>
    private Picture(Picture frame, int parity)
    {
        WidthInMbs = frame.WidthInMbs;
        HeightInMbs = frame.HeightInMbs;
        LumaStride = frame.LumaStride;
        ChromaStride = frame.ChromaStride;
        Y = frame.Y;
        Cb = frame.Cb;
        Cr = frame.Cr;
        MvX = frame.MvX;
        MvY = frame.MvY;
        RefIdx = frame.RefIdx;
        RefId = frame.RefId;
        Frame = frame;
        Parity = parity;
    }

    /// <summary>The frame this picture is, or is a field of.</summary>
    public Picture Frame { get; }

    /// <summary>-1 for a frame; 0 or 1 for the top or bottom field view (<see cref="Field"/>).</summary>
    public int Parity { get; }

    /// <summary>
    /// The top (0) or bottom (1) field of this frame as a reference for field macroblocks: one
    /// object per field, so that comparing references compares fields.
    /// </summary>
    public Picture Field(int parity) => parity == 0 ? _top ??= new Picture(this, 0) : _bottom ??= new Picture(this, 1);

    private Picture? _top;
    private Picture? _bottom;

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

    /// <summary>
    /// A frame inferred for a gap in frame_num (8.2.5.2): it holds a place among the short-term
    /// references but has no samples, is never output and must not be predicted from.
    /// </summary>
    public bool IsNonExisting { get; set; }

    public bool IsReference => IsShortTermReference || IsLongTermReference;

    public byte[] Plane(int component) => component switch
    {
        0 => Y,
        1 => Cb,
        _ => Cr,
    };
}
