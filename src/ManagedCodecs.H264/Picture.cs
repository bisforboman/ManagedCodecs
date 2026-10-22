namespace ManagedCodecs.H264;

/// <summary>
/// A decoded picture in macroblock-aligned planes (4:2:0 or 4:2:2), with its motion field (which direct
/// prediction of later B pictures reads) and its reference marking state.
/// </summary>
internal sealed class Picture
{
    public Picture(int widthInMbs, int heightInMbs, int mbHeightC = 8)
    {
        WidthInMbs = widthInMbs;
        HeightInMbs = heightInMbs;
        MbHeightC = mbHeightC;
        LumaStride = widthInMbs * 16;
        ChromaStride = widthInMbs * 8;
        Y = new byte[LumaStride * heightInMbs * 16];
        Cb = new byte[ChromaStride * heightInMbs * mbHeightC];
        Cr = new byte[ChromaStride * heightInMbs * mbHeightC];

        int mbCount = widthInMbs * heightInMbs;
        MvX = [new short[mbCount * 16], new short[mbCount * 16]];
        MvY = [new short[mbCount * 16], new short[mbCount * 16]];
        RefIdx = [new sbyte[mbCount * 4], new sbyte[mbCount * 4]];
        RefId = [new long[mbCount * 4], new long[mbCount * 4]];
        RefParity = [new sbyte[mbCount * 4], new sbyte[mbCount * 4]];
        FieldMb = new bool[mbCount];
        Frame = this;
        Parity = -1;
    }

    /// <summary>A view of one field of <paramref name="frame"/>: the same arrays, every other row.</summary>
    private Picture(Picture frame, int parity)
    {
        WidthInMbs = frame.WidthInMbs;
        HeightInMbs = frame.HeightInMbs;
        MbHeightC = frame.MbHeightC;
        LumaStride = frame.LumaStride;
        ChromaStride = frame.ChromaStride;
        Y = frame.Y;
        Cb = frame.Cb;
        Cr = frame.Cr;
        MvX = frame.MvX;
        MvY = frame.MvY;
        RefIdx = frame.RefIdx;
        RefId = frame.RefId;
        RefParity = frame.RefParity;
        FieldMb = frame.FieldMb;
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
    private readonly Mark[] _marks = new Mark[2];
    private int _poc;
    private long _id;

    public int WidthInMbs { get; }

    public int HeightInMbs { get; }

    /// <summary>Chroma rows per macroblock: 8 (4:2:0) or 16 (4:2:2).</summary>
    public int MbHeightC { get; }

    public int LumaStride { get; }

    public int ChromaStride { get; }

    public byte[] Y { get; }

    public byte[] Cb { get; }

    public byte[] Cr { get; }

    /// <summary>
    /// Identifies the picture decoded into this object; pictures are reused once they stop being
    /// references, so anything that names a picture across pictures uses this, not the object.
    /// </summary>
    public long Id
    {
        get => Frame._id;
        set => Frame._id = value;
    }

    /// <summary>PicOrderCnt (8.2.1): of the frame, the smaller of its fields'; of a field view, that field's.</summary>
    public int Poc
    {
        get => Parity < 0 ? _poc : Parity == 0 ? Frame.TopPoc : Frame.BottomPoc;
        set => _poc = value;
    }

    /// <summary>TopFieldOrderCnt and BottomFieldOrderCnt of the frame.</summary>
    public int TopPoc { get; set; }

    public int BottomPoc { get; set; }

    /// <summary>Per macroblock (MBAFF addresses): coded as a field macroblock; all false outside MBAFF frames.</summary>
    public bool[] FieldMb { get; }

    /// <summary>
    /// Whether the frame's macroblocks are stored by pair (MBAFF addresses for <see cref="FieldMb"/>
    /// and motion): an MBAFF frame, or a frame coded as two field pictures (all field macroblocks).
    /// </summary>
    public bool Mbaff { get; set; }

    /// <summary>Motion vector per list (0, 1) and luma 4x4 block, in quarter samples.</summary>
    public short[][] MvX { get; }

    public short[][] MvY { get; }

    /// <summary>Reference index per list and 8x8 block; -1 where the list is not used (and in intra macroblocks).</summary>
    public sbyte[][] RefIdx { get; }

    /// <summary><see cref="Id"/> of the referenced picture per list and 8x8 block, where <see cref="RefIdx"/> is not -1.</summary>
    public long[][] RefId { get; }

    /// <summary>
    /// Which field of <see cref="RefId"/> the block refers to (0 top, 1 bottom) where the block
    /// predicts from a field (field macroblocks, field pictures); -1 where from a frame.
    /// </summary>
    public sbyte[][] RefParity { get; }

    public int FrameNum { get; set; }

    /// <summary>FrameNumWrap (8.2.4.1), updated whenever reference lists are built.</summary>
    public int FrameNumWrap { get; set; }

    /// <summary>
    /// Marked as used for short-term reference: a frame when both its fields are (8.2.4.2.1), a
    /// field view when its field is. Setting a frame's marks both fields.
    /// </summary>
    public bool IsShortTermReference
    {
        get => Parity < 0 ? Frame._marks[0] == Mark.Short && Frame._marks[1] == Mark.Short : Frame._marks[Parity] == Mark.Short;
        set => SetMark(Mark.Short, value);
    }

    /// <summary>Marked as used for long-term reference, as for <see cref="IsShortTermReference"/>.</summary>
    public bool IsLongTermReference
    {
        get => Parity < 0 ? Frame._marks[0] == Mark.Long && Frame._marks[1] == Mark.Long : Frame._marks[Parity] == Mark.Long;
        set => SetMark(Mark.Long, value);
    }

    /// <summary>Whether any field of the frame is marked as used for reference.</summary>
    public bool HasReferenceField => Frame._marks[0] != Mark.None || Frame._marks[1] != Mark.None;

    /// <summary>Whether the frame was decoded as two field pictures.</summary>
    public bool CodedAsFields { get; set; }

    private void SetMark(Mark mark, bool on)
    {
        for (int parity = 0; parity < 2; parity++)
        {
            if (Parity < 0 || Parity == parity)
            {
                ref var current = ref Frame._marks[parity];
                current = on ? mark : current == mark ? Mark.None : current;
            }
        }
    }

    private enum Mark : byte
    {
        None,
        Short,
        Long,
    }

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
