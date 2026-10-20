namespace ManagedCodecs.H264;

/// <summary>A decoded picture in macroblock-aligned planes (4:2:0), plus reference marking state.</summary>
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
    }

    public int WidthInMbs { get; }

    public int HeightInMbs { get; }

    public int LumaStride { get; }

    public int ChromaStride { get; }

    public byte[] Y { get; }

    public byte[] Cb { get; }

    public byte[] Cr { get; }

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
