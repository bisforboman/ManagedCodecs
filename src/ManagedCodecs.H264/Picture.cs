using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ManagedCodecs.H264;

/// <summary>
/// A decoded picture in macroblock-aligned planes (4:2:0, 4:2:2, 4:4:4, or luma alone in monochrome), with its motion
/// field (which direct prediction of later B pictures reads) and its reference marking state.
/// </summary>
internal sealed class Picture
{
    public Picture(int widthInMbs, int heightInMbs, int mbHeightC = 8, int bitDepth = 8, int mbWidthC = 8)
    {
        WidthInMbs = widthInMbs;
        HeightInMbs = heightInMbs;
        MbHeightC = mbHeightC;
        MbWidthC = mbWidthC;
        BitDepth = bitDepth;
        LumaStride = widthInMbs * 16;
        ChromaStride = widthInMbs * mbWidthC;
        int lumaSize = LumaStride * heightInMbs * 16;
        int chromaSize = ChromaStride * heightInMbs * mbHeightC;
        _planes = bitDepth > 8
            ? [new ushort[lumaSize], new ushort[chromaSize], new ushort[chromaSize]]
            : [new byte[lumaSize], new byte[chromaSize], new byte[chromaSize]];

        int mbCount = widthInMbs * heightInMbs;
        MvX = [new short[mbCount * 16], new short[mbCount * 16]];
        MvY = [new short[mbCount * 16], new short[mbCount * 16]];
        RefIdx = [new sbyte[mbCount * 4], new sbyte[mbCount * 4]];
        RefId = [new long[mbCount * 4], new long[mbCount * 4]];
        RefParity = [new sbyte[mbCount * 4], new sbyte[mbCount * 4]];
        FieldMb = new bool[mbCount];
        Frame = this;
        Parity = -1;

        // Both views at once: decoding threads ask for them too (H264Decoder.Threads).
        _top = new Picture(this, 0);
        _bottom = new Picture(this, 1);
    }

    /// <summary>A view of one field of <paramref name="frame"/>: the same arrays, every other row.</summary>
    private Picture(Picture frame, int parity)
    {
        WidthInMbs = frame.WidthInMbs;
        HeightInMbs = frame.HeightInMbs;
        MbHeightC = frame.MbHeightC;
        MbWidthC = frame.MbWidthC;
        BitDepth = frame.BitDepth;
        LumaStride = frame.LumaStride;
        ChromaStride = frame.ChromaStride;
        _planes = frame._planes;
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
    public Picture Field(int parity) => parity == 0 ? Frame._top! : Frame._bottom!;

    // The Y, Cb and Cr planes: byte[] for 8-bit samples, ushort[] for deeper ones.
    private readonly Array[] _planes;
    private Picture? _top;
    private Picture? _bottom;
    private readonly Mark[] _marks = new Mark[2];
    private int _poc;
    private long _id;

    public int WidthInMbs { get; }

    public int HeightInMbs { get; }

    /// <summary>Chroma rows per macroblock: 8 (4:2:0) or 16 (4:2:2).</summary>
    public int MbHeightC { get; }

    /// <summary>Chroma columns per macroblock: 8, or 16 in 4:4:4.</summary>
    public int MbWidthC { get; }

    public int LumaStride { get; }

    public int ChromaStride { get; }

    /// <summary>Bits per sample (luma and chroma alike): 8 with byte planes, more with ushort planes.</summary>
    public int BitDepth { get; }

    /// <summary>The 8-bit planes (<see cref="BitDepth"/> 8).</summary>
    public byte[] Y => (byte[])_planes[0];

    public byte[] Cb => (byte[])_planes[1];

    public byte[] Cr => (byte[])_planes[2];

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

    public byte[] Plane(int component) => (byte[])_planes[component];

    /// <summary>Plane <paramref name="component"/> (0 Y, 1 Cb, 2 Cr) as samples of <typeparamref name="TPixel"/>, byte or ushort as <see cref="BitDepth"/> says.</summary>
    public TPixel[] Plane<TPixel>(int component)
    {
        Debug.Assert(_planes[component] is TPixel[], "The sample type does not match the bit depth.");
        return Unsafe.As<TPixel[]>(_planes[component]);
    }

    // ---- Decoding progress, for frame threading (H264Decoder.Threads) ----
    // Kept on the frame: how many of its macroblock rows are decoded (their motion final, which direct prediction of a
    // later picture reads) and how many luma rows are final (decoded and deblocked, which motion compensation reads);
    // per field the same in field rows when it is coded as two field pictures; and whether decoding it failed.
    // Progress is written by the thread decoding the picture after the data it covers, and read by the threads of
    // later pictures.
    private readonly object _progressLock = new();
    private readonly int[] _fieldDecodedMbRows = new int[2];
    private readonly int[] _fieldFinalRows = new int[2];
    private volatile int _decodedMbRows;
    private volatile int _finalLumaRows;
    private volatile bool _failed;

    /// <summary>Before decoding into the picture again: nothing of it is decoded.</summary>
    public void ResetProgress()
    {
        var frame = Frame;
        lock (frame._progressLock)
        {
            frame._decodedMbRows = 0;
            frame._finalLumaRows = 0;
            Array.Clear(frame._fieldDecodedMbRows);
            Array.Clear(frame._fieldFinalRows);
            frame._failed = false;
        }
    }

    /// <summary>
    /// The first <paramref name="decodedMbRows"/> macroblock rows are decoded and the first
    /// <paramref name="finalLumaRows"/> luma rows final: of the frame, or of this field when it is a field picture.
    /// </summary>
    public void ReportProgress(int decodedMbRows, int finalLumaRows)
    {
        var frame = Frame;
        lock (frame._progressLock)
        {
            if (Parity < 0)
            {
                frame._decodedMbRows = decodedMbRows;
                frame._finalLumaRows = finalLumaRows;
            }
            else
            {
                Volatile.Write(ref frame._fieldDecodedMbRows[Parity], decodedMbRows);
                Volatile.Write(ref frame._fieldFinalRows[Parity], finalLumaRows);
            }

            Monitor.PulseAll(frame._progressLock);
        }
    }

    /// <summary>The picture (this frame, or this field when it is a field picture) is decoded and deblocked.</summary>
    public void ReportDone()
    {
        var frame = Frame;
        if (Parity >= 0)
        {
            ReportProgress(HeightInMbs / 2, HeightInMbs * 8);
            if (Volatile.Read(ref frame._fieldFinalRows[1 - Parity]) < HeightInMbs * 8)
            {
                return;
            }
        }

        frame.ReportProgress(HeightInMbs, HeightInMbs * 16);
    }

    /// <summary>Decoding the picture failed: whatever waits on it fails too, rather than waiting forever.</summary>
    public void ReportFailed()
    {
        var frame = Frame;
        lock (frame._progressLock)
        {
            frame._failed = true;
            Monitor.PulseAll(frame._progressLock);
        }
    }

    /// <summary>Waits until luma row <paramref name="row"/> of this frame or field (and every row above it) is final.</summary>
    public void WaitForRow(int row)
    {
        int height = Parity < 0 ? HeightInMbs * 16 : HeightInMbs * 8;
        int clamped = Math.Clamp(row, 0, height - 1);
        if (!RowReady(clamped))
        {
            Wait(() => RowReady(clamped));
        }
    }

    // A field's row y is the frame's row 2y + parity; a frame's row y, of a frame coded as fields, row y / 2 of both.
    private bool RowReady(int row)
    {
        var frame = Frame;
        return Parity < 0
            ? frame._finalLumaRows > row || (Volatile.Read(ref frame._fieldFinalRows[0]) > row / 2 && Volatile.Read(ref frame._fieldFinalRows[1]) > row / 2)
            : frame._finalLumaRows > (2 * row) + Parity || Volatile.Read(ref frame._fieldFinalRows[Parity]) > row;
    }

    /// <summary>Waits until the motion of macroblock row <paramref name="frameMbRow"/> of the frame (and every row above it) is decoded.</summary>
    public void WaitForMotion(int frameMbRow)
    {
        int row = Math.Min(frameMbRow, HeightInMbs - 1);
        if (!MotionReady(row))
        {
            Wait(() => MotionReady(row));
        }
    }

    // A frame macroblock row r is field macroblock row r / 2 (the pair row) of each field.
    private bool MotionReady(int frameMbRow)
    {
        var frame = Frame;
        return frame._decodedMbRows > frameMbRow || (Parity < 0
            ? Volatile.Read(ref frame._fieldDecodedMbRows[0]) > frameMbRow / 2 && Volatile.Read(ref frame._fieldDecodedMbRows[1]) > frameMbRow / 2
            : Volatile.Read(ref frame._fieldDecodedMbRows[Parity]) > frameMbRow / 2);
    }

    private void Wait(Func<bool> ready)
    {
        var frame = Frame;
        lock (frame._progressLock)
        {
            while (!ready())
            {
                if (frame._failed)
                {
                    throw new InvalidDataException("A picture this one predicts from could not be decoded.");
                }

                Monitor.Wait(frame._progressLock);
            }
        }
    }
}
