using System.Runtime.InteropServices;
#if NET
using System.Runtime.Intrinsics;
#endif

namespace ManagedCodecs.MonoGame;

/// <summary>
/// Converts a decoded frame to 8-bit RGBA (MonoGame's <c>SurfaceFormat.Color</c>: R, G, B, A in memory), any of the
/// library's layouts (4:0:0, 4:2:0, 4:2:2, 4:4:4) at any bit depth, nearest-neighbour chroma. Fixed point, 16
/// fractional bits. <see cref="For"/> picks the matrix and range a frame says (<see cref="VideoFrame.ColorMatrix"/>,
/// <see cref="VideoFrame.ColorRange"/>), and where it says nothing what a player usually assumes: BT.601 below 720
/// lines, BT.709 from 720, limited range. The transfer and primaries are not converted (the RGB is the source's, as
/// most players show SDR video; PQ and HLG come out dim and flat without tone mapping). 8-bit samples go through
/// tables of each sample's term, a byte at a time and a pixel written as one word; <see cref="ConvertReference"/>, the
/// formula sample by sample, is what a test compares them with.
/// </summary>
internal sealed class YuvToRgba
{
    private static readonly Dictionary<(double Kr, double Kb, bool Full), YuvToRgba> Converters = [];

    private readonly int _yScale;
    private readonly int _yOffset;
    private readonly int _crToR;
    private readonly int _cbToG;
    private readonly int _crToG;
    private readonly int _cbToB;
    private readonly int _cScale;

    // For 8-bit samples, each one's term in the sums Convert8 makes: luma's (offset, scaled and rounded), Cr's in red
    // and green, Cb's in green and blue, all with ConvertReference's 16 fractional bits and its rounding.
    private readonly int[] _y8 = new int[256];
    private readonly int[] _crR8 = new int[256];
    private readonly int[] _crG8 = new int[256];
    private readonly int[] _cbG8 = new int[256];
    private readonly int[] _cbB8 = new int[256];

    /// <param name="kr">The matrix's Kr (ITU-T H.273 Table 4).</param>
    /// <param name="kb">The matrix's Kb.</param>
    /// <param name="fullRange">Full range samples, else limited (16 to 235, 16 to 240, scaled by the depth).</param>
    public YuvToRgba(double kr, double kb, bool fullRange)
    {
        // R = Y + 2 (1 - Kr) Cr, B = Y + 2 (1 - Kb) Cb, G from Y = Kr R + Kg G + Kb B.
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
        for (int i = 0; i < 256; i++)
        {
            _y8[i] = ((i - _yOffset) * _yScale) + (1 << 15);
            int c = ((i - 128) * _cScale) >> 16;
            _crR8[i] = c * _crToR;
            _crG8[i] = c * _crToG;
            _cbG8[i] = c * _cbToG;
            _cbB8[i] = c * _cbToB;
        }
    }

    /// <summary>
    /// The converter for a frame's matrix and range, made once for each: BT.709, BT.601 (BT.470BG, SMPTE 170M), BT.2020
    /// (non-constant luminance; constant luminance converted as if it were) and SMPTE 240M; any other matrix, or none
    /// said, by the frame's height. Limited range unless the frame says full.
    /// </summary>
    public static YuvToRgba For(VideoFrame frame)
    {
        var (kr, kb) = frame.ColorMatrix switch
        {
            ColorMatrix.Bt709 => (0.2126, 0.0722),
            ColorMatrix.Bt470BG or ColorMatrix.Smpte170M => (0.299, 0.114),
            ColorMatrix.Bt2020Ncl or ColorMatrix.Bt2020Cl => (0.2627, 0.0593),
            ColorMatrix.Smpte240M => (0.212, 0.087),
            ColorMatrix.Fcc => (0.30, 0.11),
            _ => frame.Height >= 720 ? (0.2126, 0.0722) : (0.299, 0.114),
        };
        bool full = frame.ColorRange == ColorRange.Full;
        lock (Converters)
        {
            if (!Converters.TryGetValue((kr, kb, full), out var converter))
            {
                converter = new YuvToRgba(kr, kb, full);
                Converters.Add((kr, kb, full), converter);
            }

            return converter;
        }
    }

    /// <summary>Writes <paramref name="frame"/> into <paramref name="rgba"/>, width x height x 4 bytes, on this thread.</summary>
    public void Convert(VideoFrame frame, Span<byte> rgba) => ConvertRows(frame, rgba, 0, frame.Height);

    /// <summary>
    /// Writes <paramref name="frame"/> into <paramref name="rgba"/>, width x height x 4 bytes: a frame of
    /// <see cref="ParallelPixels"/> or more in bands of rows on the thread pool (each row is converted on its own, so
    /// the result is the same), a smaller one on this thread.
    /// </summary>
    public void Convert(VideoFrame frame, byte[] rgba)
    {
        int height = frame.Height;
        int bands = Math.Min(Environment.ProcessorCount, height / 16);
        if (bands < 2 || (long)frame.Width * height < ParallelPixels)
        {
            ConvertRows(frame, rgba, 0, height);
            return;
        }

        Parallel.For(0, bands, band => ConvertRows(frame, rgba, (int)((long)height * band / bands), (int)((long)height * (band + 1) / bands)));
    }

    /// <summary>The fewest pixels a frame has for <see cref="Convert(VideoFrame, byte[])"/> to share it out: about 512x512.</summary>
    public const int ParallelPixels = 1 << 18;

    /// <summary>
    /// <see cref="Convert(VideoFrame, Span{byte})"/> by the formula, sample by sample at the samples' own depth: any
    /// depth, and what the 8-bit paths must match byte for byte.
    /// </summary>
    public void ConvertReference(VideoFrame frame, Span<byte> rgba) => ReferenceRows(frame, rgba, 0, frame.Height);

    private void ConvertRows(VideoFrame frame, Span<byte> rgba, int top, int bottom)
    {
        if (frame.Format.BytesPerSample() == 1 && BitConverter.IsLittleEndian)
        {
            Convert8(frame, rgba, top, bottom);
        }
        else
        {
            ReferenceRows(frame, rgba, top, bottom);
        }
    }

    private void ReferenceRows(VideoFrame frame, Span<byte> rgba, int top, int bottom)
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
        int xShift = Shift(width, chromaWidth);
        int yShift = Shift(height, chromaHeight);
        for (int y = top; y < bottom; y++)
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

    // 8-bit samples by the tables, each pixel written as one little-endian word (R, G, B, A in memory). With chroma at
    // half the width (4:2:0, 4:2:2) a chroma sample's terms serve the two pixels it covers, and where vectors are
    // accelerated, 32 pixels at a time by HalfWidthVectors, the tail by the tables.
    private void Convert8(VideoFrame frame, Span<byte> rgba, int top, int bottom)
    {
        int width = frame.Width;
        int height = frame.Height;
        var format = frame.Format;
        bool gray = format.PlaneCount() == 1;
        var (chromaWidth, chromaHeight) = gray ? (width, height) : format.PlaneSize(1, width, height);
        int xShift = Shift(width, chromaWidth);
        int yShift = Shift(height, chromaHeight);
        var words = MemoryMarshal.Cast<byte, uint>(rgba);
        int[] yt = _y8;
        for (int y = top; y < bottom; y++)
        {
            var luma = frame.GetRow(0, y).Slice(0, width);
            var row = words.Slice(y * width, width);
            if (gray)
            {
                for (int x = 0; x < luma.Length; x++)
                {
                    row[x] = Pack(yt[luma[x]], 0, 0, 0);
                }

                continue;
            }

            var cb = frame.GetRow(1, y >> yShift);
            var cr = frame.GetRow(2, y >> yShift);
            if (xShift == 1)
            {
                int pairs = width >> 1;
                cb = cb.Slice(0, (width + 1) >> 1);
                cr = cr.Slice(0, cb.Length);
                int done = HalfWidthVectors(luma, cb, cr, row, pairs);
                for (int i = done; i < pairs; i++)
                {
                    int u = cb[i];
                    int v = cr[i];
                    int r = _crR8[v];
                    int g = _cbG8[u] + _crG8[v];
                    int b = _cbB8[u];
                    row[2 * i] = Pack(yt[luma[2 * i]], r, g, b);
                    row[(2 * i) + 1] = Pack(yt[luma[(2 * i) + 1]], r, g, b);
                }

                if ((width & 1) != 0)
                {
                    int u = cb[pairs];
                    int v = cr[pairs];
                    row[width - 1] = Pack(yt[luma[width - 1]], _crR8[v], _cbG8[u] + _crG8[v], _cbB8[u]);
                }
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    int u = cb[x >> xShift];
                    int v = cr[x >> xShift];
                    row[x] = Pack(yt[luma[x]], _crR8[v], _cbG8[u] + _crG8[v], _cbB8[u]);
                }
            }
        }
    }

    // The pixel pairs of a row with chroma at half the width, 16 pairs at a time by 128-bit vectors where they are
    // accelerated: the tables' terms computed, not looked up, in the same 32-bit integers (the same products, shifts and
    // rounding), so byte for byte the same. Returns how many pairs it did (0 without vectors); the tables do the rest.
    private int HalfWidthVectors(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, Span<uint> row, int pairs)
    {
#if NET
        if (!Vector128.IsHardwareAccelerated || pairs < 16)
        {
            return 0;
        }

        var yScale = Vector128.Create(_yScale);
        var yBias = Vector128.Create((-_yOffset * _yScale) + (1 << 15));
        var cScale = Vector128.Create(_cScale);
        var crToR = Vector128.Create(_crToR);
        var crToG = Vector128.Create(_crToG);
        var cbToG = Vector128.Create(_cbToG);
        var cbToB = Vector128.Create(_cbToB);
        var half = Vector128.Create(128);
        int i = 0;
        for (; i + 16 <= pairs; i += 16)
        {
            var u = Vector128.Create(cb.Slice(i, 16));
            var v = Vector128.Create(cr.Slice(i, 16));
            var y0 = Vector128.Create(luma.Slice(2 * i, 16));
            var y1 = Vector128.Create(luma.Slice((2 * i) + 16, 16));
            var (uLow, uHigh) = Vector128.Widen(u);
            var (vLow, vHigh) = Vector128.Widen(v);
            var (y0Low, y0High) = Vector128.Widen(y0);
            var (y1Low, y1High) = Vector128.Widen(y1);
            var output = row.Slice(2 * i, 32);
            Quarter(uLow, vLow, y0Low, y0High, output);
            Quarter(uHigh, vHigh, y1Low, y1High, output.Slice(16));
        }

        return i;

        // 8 chroma samples (as 16 bits) and the 16 luma samples they cover: 16 pixels.
        void Quarter(Vector128<ushort> u8, Vector128<ushort> v8, Vector128<ushort> yLow, Vector128<ushort> yHigh, Span<uint> pixels)
        {
            var (uA, uB) = Vector128.Widen(u8);
            var (vA, vB) = Vector128.Widen(v8);
            var (yA, yB) = Vector128.Widen(yLow);
            var (yC, yD) = Vector128.Widen(yHigh);
            Four(uA.AsInt32(), vA.AsInt32(), yA.AsInt32(), yB.AsInt32(), pixels);
            Four(uB.AsInt32(), vB.AsInt32(), yC.AsInt32(), yD.AsInt32(), pixels.Slice(8));
        }

        // 4 chroma samples and their 8 luma samples: each chroma term serves two neighbouring pixels.
        void Four(Vector128<int> u4, Vector128<int> v4, Vector128<int> yFirst, Vector128<int> ySecond, Span<uint> pixels)
        {
            var c = Vector128.ShiftRightArithmetic((u4 - half) * cScale, 16);
            var d = Vector128.ShiftRightArithmetic((v4 - half) * cScale, 16);
            var r = d * crToR;
            var g = (c * cbToG) + (d * crToG);
            var b = c * cbToB;
            Pixels((yFirst * yScale) + yBias, Vector128.Shuffle(r, Vector128.Create(0, 0, 1, 1)), Vector128.Shuffle(g, Vector128.Create(0, 0, 1, 1)), Vector128.Shuffle(b, Vector128.Create(0, 0, 1, 1))).CopyTo(pixels);
            Pixels((ySecond * yScale) + yBias, Vector128.Shuffle(r, Vector128.Create(2, 2, 3, 3)), Vector128.Shuffle(g, Vector128.Create(2, 2, 3, 3)), Vector128.Shuffle(b, Vector128.Create(2, 2, 3, 3))).CopyTo(pixels.Slice(4));
        }

        static Vector128<uint> Pixels(Vector128<int> l, Vector128<int> r, Vector128<int> g, Vector128<int> b)
        {
            var low = Vector128<int>.Zero;
            var high = Vector128.Create(255);
            var red = Vector128.Clamp(Vector128.ShiftRightArithmetic(l + r, 16), low, high).AsUInt32();
            var green = Vector128.Clamp(Vector128.ShiftRightArithmetic(l - g, 16), low, high).AsUInt32();
            var blue = Vector128.Clamp(Vector128.ShiftRightArithmetic(l + b, 16), low, high).AsUInt32();
            return red | (green << 8) | (blue << 16) | Vector128.Create(0xFF000000u);
        }
#else
        return 0;
#endif
    }

    // One pixel from luma's term and the chroma terms (green's subtracted), shifted down and clipped.
    private static uint Pack(int l, int r, int g, int b) =>
        Clip8((l + r) >> 16) | (Clip8((l - g) >> 16) << 8) | (Clip8((l + b) >> 16) << 16) | 0xFF000000u;

    private static uint Clip8(int value) => (uint)Math.Clamp(value, 0, 255);

    /// <summary>The shift that rounds <paramref name="full"/> up to <paramref name="sub"/>: 1 for half, 2 for a quarter (4:1:1).</summary>
    private static int Shift(int full, int sub)
    {
        int shift = 0;
        while (-(-full >> shift) > sub)
        {
            shift++;
        }

        return shift;
    }

    private static int Sample(Span<byte> row, int x, bool deep) => deep ? MemoryMarshal.Cast<byte, ushort>(row)[x] : row[x];

    private static byte Clip(long value) => (byte)Math.Clamp(value, 0, 255);

    private static int Fixed(double value) => (int)Math.Round(value * 65536);
}
