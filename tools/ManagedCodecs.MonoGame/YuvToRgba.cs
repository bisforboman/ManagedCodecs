using System.Runtime.InteropServices;

namespace ManagedCodecs.MonoGame;

/// <summary>
/// Converts a decoded frame to 8-bit RGBA (MonoGame's <c>SurfaceFormat.Color</c>: R, G, B, A in memory), any of the
/// library's layouts (4:0:0, 4:2:0, 4:2:2, 4:4:4) at any bit depth, nearest-neighbour chroma. Fixed point, 16
/// fractional bits. Until frames carry their colour metadata (docs/decisions/2026-10-31-colour-metadata.md), the
/// caller picks the matrix and range: what a player usually assumes (BT.601 below 720 lines, BT.709 from 720, limited
/// range for H.264, full range BT.601 for JPEG).
/// </summary>
internal sealed class YuvToRgba
{
    private readonly int _yScale;
    private readonly int _yOffset;
    private readonly int _crToR;
    private readonly int _cbToG;
    private readonly int _crToG;
    private readonly int _cbToB;
    private readonly int _cScale;

    public YuvToRgba(bool bt709, bool fullRange)
    {
        // Kr and Kb of BT.601 and BT.709; R = Y + 2 (1 - Kr) Cr, B = Y + 2 (1 - Kb) Cb, G from Y = Kr R + Kg G + Kb B.
        double kr = bt709 ? 0.2126 : 0.299;
        double kb = bt709 ? 0.0722 : 0.114;
        double kg = 1 - kr - kb;
        double yScale = fullRange ? 1 : 255.0 / 219;
        double cScale = fullRange ? 1 : 255.0 / 224;
        _yScale = Fixed(yScale);
        _yOffset = fullRange ? 0 : 16;
        _cScale = Fixed(cScale);
        _crToR = Fixed(2 * (1 - kr));
        _cbToB = Fixed(2 * (1 - kb));
        _cbToG = Fixed(2 * (1 - kb) * kb / kg);
        _crToG = Fixed(2 * (1 - kr) * kr / kg);
    }

    /// <summary>Writes <paramref name="frame"/> into <paramref name="rgba"/>, width x height x 4 bytes.</summary>
    public void Convert(VideoFrame frame, Span<byte> rgba)
    {
        int width = frame.Width;
        int height = frame.Height;
        var format = frame.Format;
        int shift = format.BitDepth() - 8;
        bool deep = format.BytesPerSample() == 2;
        bool gray = format.PlaneCount() == 1;

        // At the samples' own depth: offsets scaled up to it, the result shifted down to 8 bits once, rounded.
        int yOffset = _yOffset << shift;
        int half = 128 << shift;
        int down = 16 + shift;
        int round = 1 << (down - 1);
        var (chromaWidth, chromaHeight) = gray ? (width, height) : format.PlaneSize(1, width, height);
        int xShift = chromaWidth < width ? 1 : 0;
        int yShift = chromaHeight < height ? 1 : 0;
        for (int y = 0; y < height; y++)
        {
            var luma = frame.GetRow(0, y);
            var cb = gray ? default : frame.GetRow(1, y >> yShift);
            var cr = gray ? default : frame.GetRow(2, y >> yShift);
            var row = rgba.Slice(y * width * 4, width * 4);
            for (int x = 0; x < width; x++)
            {
                int yv = Sample(luma, x, deep);
                int u = gray ? half : Sample(cb, x >> xShift, deep);
                int v = gray ? half : Sample(cr, x >> xShift, deep);
                long l = ((long)(yv - yOffset) * _yScale) + round;
                long cbs = ((long)(u - half) * _cScale) >> 16;
                long crs = ((long)(v - half) * _cScale) >> 16;
                row[(x * 4) + 0] = Clip((l + (crs * _crToR)) >> down);
                row[(x * 4) + 1] = Clip((l - (cbs * _cbToG) - (crs * _crToG)) >> down);
                row[(x * 4) + 2] = Clip((l + (cbs * _cbToB)) >> down);
                row[(x * 4) + 3] = 255;
            }
        }
    }

    private static int Sample(Span<byte> row, int x, bool deep) => deep ? MemoryMarshal.Cast<byte, ushort>(row)[x] : row[x];

    private static byte Clip(long value) => (byte)Math.Clamp(value, 0, 255);

    private static int Fixed(double value) => (int)Math.Round(value * 65536);
}
