// Vector code: the net10.0 build only (the Unity build, netstandard2.1, has no System.Runtime.Intrinsics;
// docs/decisions/2026-10-31-unity-netstandard.md).
#if NET
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ManagedCodecs.H264;

internal static partial class InterPrediction
{
    // The largest luma block is 16x16; its 6-tap filters need 2 samples before and 3 after.
    private const int WindowStride = 16 + 5;

    /// <summary>Same output as <see cref="PredictLumaReference{TPixel}(Picture, TPixel[], int, int, int, int, int, int, int)"/>.</summary>
    public static void PredictLuma(Picture reference, byte[] dest, int stride, int x, int y, int width, int height, int mvX, int mvY) =>
        PredictLuma(reference, x, y, width, height, mvX, mvY, dest.AsSpan((y * stride) + x), stride);

    /// <summary>
    /// The same, written to <paramref name="output"/> (the block's top-left) with rows <paramref name="outputStride"/> apart;
    /// from plane <paramref name="component"/>, which in 4:4:4 can be a chroma one (8.4.2.2: the luma filter).
    /// </summary>
    /// <remarks>
    /// The scratch buffers are not zeroed: lanes past the block's width may hold anything, and
    /// only the first <c>width</c> bytes of each row are ever stored.
    /// </remarks>
    /// <remarks>
    /// A field view of a frame (<see cref="Picture.Field"/>) is read as a picture of its own: every
    /// other row, half the height, its edges repeated within the field. (<paramref name="x"/>,
    /// <paramref name="y"/>) are then field coordinates.
    /// </remarks>
    [SkipLocalsInit]
    public static void PredictLuma(Picture reference, int x, int y, int width, int height, int mvX, int mvY, Span<byte> output, int outputStride, int component = 0)
    {
        int stride = reference.LumaStride;
        int maxY = (reference.HeightInMbs * 16) - 1;
        ReadOnlySpan<byte> plane = reference.Plane(component);
        if (reference.Parity >= 0)
        {
            plane = plane[(reference.Parity * stride)..];
            stride *= 2;
            maxY = (reference.HeightInMbs * 8) - 1;
        }

        int xFrac = mvX & 3;
        int yFrac = mvY & 3;
        int maxX = (reference.WidthInMbs * 16) - 1;

        // Full sample motion inside the picture: a plain copy, which needs no margin for the
        // filter taps (blocks at the picture's edges, or still ones).
        if (xFrac == 0 && yFrac == 0 && CopyInside(plane, stride, maxX, maxY, x + (mvX >> 2), y + (mvY >> 2), width, height, output, outputStride))
        {
            return;
        }

        // Samples -2 .. size + 2 around the integer position. Read them straight from the
        // reference when they are inside it (with room for 16-byte loads), else from a copy
        // with the coordinates clamped.
        int left = x + (mvX >> 2) - 2;
        int top = y + (mvY >> 2) - 2;
        Span<byte> copy = stackalloc byte[(WindowStride * WindowStride) + 32]; // slack for 16-byte loads
        ReadOnlySpan<byte> window = copy;
        int ws;
        int g;
        if (left >= 0 && top >= 0 && left + width + 4 <= maxX && top + height + 4 <= maxY
            && ((top + height + 5) * stride) + left + 16 <= plane.Length)
        {
            window = plane;
            ws = stride;
            g = ((top + 2) * stride) + left + 2;
        }
        else
        {
            FetchWindow(plane, stride, maxX, maxY, left, top, width + 5, height + 5, copy);
            ws = WindowStride;
            g = (2 * WindowStride) + 2; // G of output (0, 0) sits at window (2, 2)
        }

        if (xFrac == 0 && yFrac == 0)
        {
            CheckRows(window, g, ws, height, output, outputStride, width);
            CopyRows(ref Unsafe.Add(ref MemoryMarshal.GetReference(window), g), ws, width, height, ref MemoryMarshal.GetReference(output), outputStride);
            return;
        }

        Span<byte> first = stackalloc byte[(16 * 16) + 16];
        Span<byte> second = stackalloc byte[(16 * 16) + 16];

        switch (xFrac, yFrac)
        {
            case (_, 0):
                // a, b, c: horizontal half sample b, averaged with G or H for the quarter positions.
                HalfHorizontal(window, ws, g, width, height, first);
                if (xFrac == 2)
                {
                    Store(first, width, height, output, outputStride);
                }
                else
                {
                    AverageWithWindow(first, window, ws, g + (xFrac == 3 ? 1 : 0), width, height, output, outputStride);
                }

                break;

            case (0, _):
                // d, h, n: vertical half sample h, averaged with G or M.
                HalfVertical(window, ws, g, width, height, first);
                if (yFrac == 2)
                {
                    Store(first, width, height, output, outputStride);
                }
                else
                {
                    AverageWithWindow(first, window, ws, g + (yFrac == 3 ? ws : 0), width, height, output, outputStride);
                }

                break;

            case (2, 2):
                Center(window, ws, g, width, height, first);
                Store(first, width, height, output, outputStride);
                break;

            case (2, _):
                // f, q: j averaged with b on this row or the next.
                Center(window, ws, g, width, height, first);
                HalfHorizontal(window, ws, g + (yFrac == 3 ? ws : 0), width, height, second);
                Average(first, second, width, height, output, outputStride);
                break;

            case (_, 2):
                // i, k: j averaged with h in this column or the next.
                Center(window, ws, g, width, height, first);
                HalfVertical(window, ws, g + (xFrac == 3 ? 1 : 0), width, height, second);
                Average(first, second, width, height, output, outputStride);
                break;

            default:
                // e, g, p, r: b (this row or next) averaged with h (this column or next).
                HalfHorizontal(window, ws, g + (yFrac == 3 ? ws : 0), width, height, first);
                HalfVertical(window, ws, g + (xFrac == 3 ? 1 : 0), width, height, second);
                Average(first, second, width, height, output, outputStride);
                break;
        }
    }

    /// <summary>
    /// Asks the cache for the reference area a macroblock four to the right will probably read (its motion like this
    /// one's): four of its 16 luma rows and two of its chroma rows a macroblock, in turn by <paramref name="phase"/>,
    /// so that the four macroblocks before it cover all of them. (<paramref name="x"/>, <paramref name="y"/>) is this
    /// macroblock's integer reference position; nothing is fetched outside the planes. A hint only: no output depends on it.
    /// </summary>
    public static unsafe void PrefetchAhead(Picture reference, int x, int y, int phase)
    {
        if (!Sse.IsSupported)
        {
            return;
        }

        int stride = reference.LumaStride;
        int row = y + (phase * 4);
        int column = x + 64;
        byte[] luma = reference.Y;
        if ((uint)column < (uint)stride && row >= 0 && ((row + 4) * stride) <= luma.Length)
        {
            byte* p = (byte*)Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(luma), (row * stride) + column));
            Sse.Prefetch0(p);
            Sse.Prefetch0(p + stride);
            Sse.Prefetch0(p + (2 * stride));
            Sse.Prefetch0(p + (3 * stride));
        }

        int chromaStride = reference.ChromaStride;
        int chromaRow = (y >> 1) + (phase * 2);
        int chromaColumn = column >> 1;
        byte[] cb = reference.Cb;
        if ((uint)chromaColumn < (uint)chromaStride && chromaRow >= 0 && ((chromaRow + 2) * chromaStride) <= cb.Length)
        {
            int offset = (chromaRow * chromaStride) + chromaColumn;
            byte* b = (byte*)Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(cb), offset));
            byte* r = (byte*)Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(reference.Cr), offset));
            Sse.Prefetch0(b);
            Sse.Prefetch0(b + chromaStride);
            Sse.Prefetch0(r);
            Sse.Prefetch0(r + chromaStride);
        }
    }

    /// <summary>Same output as <see cref="PredictChromaReference{TPixel}(Picture, int, TPixel[], int, int, int, int, int, int, int)"/>.</summary>
    public static void PredictChroma(Picture reference, int component, byte[] dest, int stride, int x, int y, int width, int height, int mvX, int mvY) =>
        PredictChroma(reference, component, x, y, width, height, mvX, mvY, dest.AsSpan((y * stride) + x), stride);

    /// <summary>The same, written to <paramref name="output"/> (the block's top-left) with rows <paramref name="outputStride"/> apart.</summary>
    [SkipLocalsInit]
    public static void PredictChroma(Picture reference, int component, int x, int y, int width, int height, int mvX, int mvY, Span<byte> output, int outputStride)
    {
        var block = new ChromaBlock(reference, x, y, width, height, mvX, mvY);
        Span<byte> copy = stackalloc byte[(9 * 17) + 32]; // up to 8x16 (4:2:2)
        block.Predict(block.Plane(reference, component), copy, output, outputStride);
    }

    /// <summary>
    /// <see cref="PredictChroma(Picture, int, int, int, int, int, int, int, Span{byte}, int)"/> of both chroma components
    /// of one block, sharing the block's position, fractions and bounds.
    /// </summary>
    [SkipLocalsInit]
    public static void PredictChromaPair(Picture reference, int x, int y, int width, int height, int mvX, int mvY, Span<byte> cb, Span<byte> cr, int outputStride)
    {
        var block = new ChromaBlock(reference, x, y, width, height, mvX, mvY);
        Span<byte> copy = stackalloc byte[(9 * 17) + 32];
        block.Predict(block.Plane(reference, 1), copy, cb, outputStride);
        block.Predict(block.Plane(reference, 2), copy, cr, outputStride);
    }

    /// <summary>Where a chroma block reads its reference, the same for Cb and Cr.</summary>
    private readonly ref struct ChromaBlock
    {
        private readonly int _stride;
        private readonly int _maxX;
        private readonly int _maxY;
        private readonly int _parity;
        private readonly int _left;
        private readonly int _top;
        private readonly int _width;
        private readonly int _height;
        private readonly int _xFrac;
        private readonly int _yFrac;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ChromaBlock(Picture reference, int x, int y, int width, int height, int mvX, int mvY)
        {
            _stride = reference.ChromaStride;
            _maxY = (reference.HeightInMbs * reference.MbHeightC) - 1;
            _parity = reference.Parity;
            if (_parity >= 0)
            {
                _stride *= 2;
                _maxY = (reference.HeightInMbs * reference.MbHeightC / 2) - 1;
            }

            _maxX = (reference.WidthInMbs * 8) - 1;
            _xFrac = mvX & 7;
            _yFrac = mvY & 7;
            _left = x + (mvX >> 3);
            _top = y + (mvY >> 3);
            _width = width;
            _height = height;
        }

        /// <summary>The component's plane as this block reads it: a field's from its first row.</summary>
        public ReadOnlySpan<byte> Plane(Picture reference, int component)
        {
            ReadOnlySpan<byte> plane = reference.Plane(component);
            return _parity >= 0 ? plane[(_parity * reference.ChromaStride)..] : plane;
        }

        public void Predict(ReadOnlySpan<byte> plane, Span<byte> copy, Span<byte> output, int outputStride)
        {
            int stride = _stride;
            if (_xFrac == 0 && _yFrac == 0 && CopyInside(plane, stride, _maxX, _maxY, _left, _top, _width, _height, output, outputStride))
            {
                return;
            }

            // As for luma: read the reference directly when the (width + 1) x (height + 1) area is
            // inside it with room for 16-byte loads, else a clamped copy.
            ReadOnlySpan<byte> window = copy;
            int windowStride = 9;
            int origin = 0;

            // The last row is read as 16 bytes from left + 1 (the right-hand neighbours).
            if (_left >= 0 && _top >= 0 && _left + _width <= _maxX && _top + _height <= _maxY && ((_top + _height) * stride) + _left + 17 <= plane.Length)
            {
                window = plane;
                windowStride = stride;
                origin = (_top * stride) + _left;
            }
            else
            {
                FetchWindow(plane, stride, _maxX, _maxY, _left, _top, _width + 1, _height + 1, copy, windowStride);
            }

            Bilinear(window, windowStride, origin, _width, _height, _xFrac, _yFrac, output, outputStride);
        }
    }

    /// <summary>
    /// The chroma filter (8-31) on a window: each row filtered across once, ((8 - xFrac) A + xFrac B), and each
    /// output row the two rows' sums weighted down, ((8 - yFrac) H0 + yFrac H1 + 32) >> 6. That is the same sum
    /// as the four corner weights' (they multiply out to it); it never exceeds 64 x 255, so 16 bits hold it.
    /// </summary>
    private static void Bilinear(ReadOnlySpan<byte> window, int ws, int origin, int width, int height, int xFrac, int yFrac, Span<byte> output, int outputStride)
    {
        // Every row, the last one below the block included, is read as 16 bytes from origin + 1.
        if (origin < 0 || origin + (height * ws) + 17 > window.Length || ((height - 1) * outputStride) + width > output.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        ref byte source = ref MemoryMarshal.GetReference(window);
        ref byte dest = ref MemoryMarshal.GetReference(output);
        var left = Vector128.Create((short)(8 - xFrac));
        var right = Vector128.Create((short)xFrac);
        var upper = Vector128.Create((short)(8 - yFrac));
        var lower = Vector128.Create((short)yFrac);
        var pairWeights = Vector128.Create((ushort)((8 - xFrac) | (xFrac << 8))).AsSByte();
        var above = Across(ref source, origin, left, right, pairWeights);
        for (int j = 0; j < height; j++)
        {
            var below = Across(ref source, origin + ((j + 1) * ws), left, right, pairWeights);
            var sum = (above * upper) + (below * lower);
            var rounded = ((sum + Vector128.Create((short)32)) >> 6).AsUInt16();
            StorePartial(Vector128.Narrow(rounded, rounded), ref Unsafe.Add(ref dest, j * outputStride), width);
            above = below;
        }
    }

    /// <summary>
    /// A row filtered across, (8 - xFrac) A + xFrac B, for its first eight samples: with SSSE3 one load, its samples
    /// paired with their right-hand neighbours by a shuffle, and one multiply-add of the pairs with the weights
    /// (<paramref name="pairWeights"/>: 8 - xFrac and xFrac in turn); else both samples widened and multiplied.
    /// </summary>
    private static Vector128<short> Across(ref byte source, int offset, Vector128<short> left, Vector128<short> right, Vector128<sbyte> pairWeights)
    {
        if (Ssse3.IsSupported)
        {
            var pairs = Ssse3.Shuffle(Load(ref source, offset), Vector128.Create((byte)0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8));
            return Ssse3.MultiplyAddAdjacent(pairs, pairWeights);
        }

        return (Lower(Load(ref source, offset)) * left) + (Lower(Load(ref source, offset + 1)) * right);
    }

    /// <summary>Writes the first <paramref name="count"/> (2, 4, 8 or 16) bytes of a vector, unchecked.</summary>
    private static void StorePartial(Vector128<byte> value, ref byte destination, int count)
    {
        switch (count)
        {
            case 16:
                value.StoreUnsafe(ref destination);
                break;
            case 8:
                Unsafe.WriteUnaligned(ref destination, value.AsUInt64().ToScalar());
                break;
            case 4:
                Unsafe.WriteUnaligned(ref destination, value.AsUInt32().ToScalar());
                break;
            default:
                Unsafe.WriteUnaligned(ref destination, value.AsUInt16().ToScalar());
                break;
        }
    }

    /// <summary>
    /// Copies the <paramref name="width"/> x <paramref name="height"/> block at (<paramref name="x"/>,
    /// <paramref name="y"/>) to the output if it lies inside the picture (with room for a 16-byte
    /// load from its last row); false, and nothing written, if not.
    /// </summary>
    private static bool CopyInside(ReadOnlySpan<byte> plane, int stride, int maxX, int maxY, int x, int y, int width, int height, Span<byte> output, int outputStride)
    {
        if (x < 0 || y < 0 || x + width - 1 > maxX || y + height - 1 > maxY || ((y + height - 1) * stride) + x + 16 > plane.Length)
        {
            return false;
        }

        if (((height - 1) * outputStride) + width > output.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(output));
        }

        CopyRows(ref Unsafe.Add(ref MemoryMarshal.GetReference(plane), (y * stride) + x), stride, width, height, ref MemoryMarshal.GetReference(output), outputStride);
        return true;
    }

    /// <summary>
    /// Copies a <paramref name="width"/> x <paramref name="height"/> area at (<paramref name="x"/>,
    /// <paramref name="y"/>) into the window, clamping coordinates into the picture.
    /// </summary>
    /// <remarks>
    /// Each row is the samples left of the picture (its first sample repeated), those inside it (copied), then those
    /// right of it (its last sample repeated); <see cref="FetchWindowReference"/> clamps sample by sample.
    /// </remarks>
    internal static void FetchWindow(ReadOnlySpan<byte> plane, int stride, int maxX, int maxY, int x, int y, int width, int height, Span<byte> window, int windowStride = WindowStride)
    {
        int left = Math.Clamp(-x, 0, width);
        int right = Math.Clamp(x + width - 1 - maxX, 0, width);
        int inside = width - left - right;
        for (int j = 0; j < height; j++)
        {
            var row = window.Slice(j * windowStride, width);
            int sy = Math.Clamp(y + j, 0, maxY) * stride;
            row[..left].Fill(plane[sy]);
            if (inside > 0)
            {
                plane.Slice(sy + x + left, inside).CopyTo(row[left..]);
            }

            row[(left + inside)..].Fill(plane[sy + maxX]);
        }
    }

    /// <summary><see cref="FetchWindow"/> by the definition: every sample's coordinates clamped into the picture.</summary>
    internal static void FetchWindowReference(ReadOnlySpan<byte> plane, int stride, int maxX, int maxY, int x, int y, int width, int height, Span<byte> window, int windowStride)
    {
        for (int j = 0; j < height; j++)
        {
            int sy = Math.Clamp(y + j, 0, maxY) * stride;
            for (int i = 0; i < width; i++)
            {
                window[(j * windowStride) + i] = plane[sy + Math.Clamp(x + i, 0, maxX)];
            }
        }
    }

    // The filters below write whole 16-byte rows to their output (rows 16 apart, so it needs
    // height * 16 bytes): lanes past the block's width hold anything. They read the window
    // through one bounds check a block, for the furthest 16-byte load.

    /// <summary>b: Clip1((b1 + 16) >> 5) for the half positions right of each G.</summary>
    /// <remarks>Each row loads 16 bytes from its samples -2 .. 3, the last from origin + (height - 1) * ws + 3.</remarks>
    private static void HalfHorizontal(ReadOnlySpan<byte> window, int ws, int origin, int width, int height, Span<byte> output)
    {
        CheckBounds(window, origin - 2, origin + ((height - 1) * ws) + 3 + 16, output, height);
        ref byte source = ref MemoryMarshal.GetReference(window);
        ref byte dest = ref MemoryMarshal.GetReference(output);
        for (int j = 0; j < height; j++)
        {
            int p = origin + (j * ws) - 2;
            var s0 = Load(ref source, p);
            var s1 = Load(ref source, p + 1);
            var s2 = Load(ref source, p + 2);
            var s3 = Load(ref source, p + 3);
            var s4 = Load(ref source, p + 4);
            var s5 = Load(ref source, p + 5);
            var low = Tap(Lower(s0), Lower(s1), Lower(s2), Lower(s3), Lower(s4), Lower(s5));
            var high = width > 8 ? Tap(Upper(s0), Upper(s1), Upper(s2), Upper(s3), Upper(s4), Upper(s5)) : low;
            RoundHalf(low, high).StoreUnsafe(ref dest, (nuint)(j * 16));
        }
    }

    /// <summary>h: Clip1((h1 + 16) >> 5) for the half positions below each G.</summary>
    /// <remarks>
    /// Rows -2 .. height + 2, 16 bytes each, the last from origin + (height + 2) * ws; the six
    /// rows of a tap slide down, one new load a row.
    /// </remarks>
    private static void HalfVertical(ReadOnlySpan<byte> window, int ws, int origin, int width, int height, Span<byte> output)
    {
        CheckBounds(window, origin - (2 * ws), origin + ((height + 2) * ws) + 16, output, height);
        ref byte source = ref MemoryMarshal.GetReference(window);
        ref byte dest = ref MemoryMarshal.GetReference(output);
        int p = origin - (2 * ws);
        var r0 = Load(ref source, p);
        var r1 = Load(ref source, p + ws);
        var r2 = Load(ref source, p + (2 * ws));
        var r3 = Load(ref source, p + (3 * ws));
        var r4 = Load(ref source, p + (4 * ws));
        for (int j = 0; j < height; j++)
        {
            var r5 = Load(ref source, p + ((j + 5) * ws));
            var low = Tap(Lower(r0), Lower(r1), Lower(r2), Lower(r3), Lower(r4), Lower(r5));
            var high = width > 8 ? Tap(Upper(r0), Upper(r1), Upper(r2), Upper(r3), Upper(r4), Upper(r5)) : low;
            RoundHalf(low, high).StoreUnsafe(ref dest, (nuint)(j * 16));
            (r0, r1, r2, r3, r4) = (r1, r2, r3, r4, r5);
        }
    }

    /// <summary>j: vertical 6-tap over unrounded horizontal intermediates, Clip1((j1 + 512) >> 10).</summary>
    /// <remarks>Reads what <see cref="HalfHorizontal"/> does for rows -2 .. height + 2.</remarks>
    [SkipLocalsInit]
    private static void Center(ReadOnlySpan<byte> window, int ws, int origin, int width, int height, Span<byte> output)
    {
        CheckBounds(window, origin - (2 * ws) - 2, origin + ((height + 2) * ws) + 3 + 16, output, height);
        ref byte source = ref MemoryMarshal.GetReference(window);
        ref byte dest = ref MemoryMarshal.GetReference(output);

        // b1 for rows -2 .. height + 2, 16 a row; it fits in 16 bits (-2550 .. 10710).
        Span<short> b1 = stackalloc short[16 * (16 + 5)];
        ref short intermediate = ref MemoryMarshal.GetReference(b1);
        for (int j = 0; j < height + 5; j++)
        {
            int p = origin + ((j - 2) * ws) - 2;
            var s0 = Load(ref source, p);
            var s1 = Load(ref source, p + 1);
            var s2 = Load(ref source, p + 2);
            var s3 = Load(ref source, p + 3);
            var s4 = Load(ref source, p + 4);
            var s5 = Load(ref source, p + 5);
            Tap(Lower(s0), Lower(s1), Lower(s2), Lower(s3), Lower(s4), Lower(s5)).StoreUnsafe(ref intermediate, (nuint)(j * 16));
            if (width > 8)
            {
                Tap(Upper(s0), Upper(s1), Upper(s2), Upper(s3), Upper(s4), Upper(s5)).StoreUnsafe(ref intermediate, (nuint)((j * 16) + 8));
            }
        }

        // The vertical pass, eight columns at a time down sliding rows. The pair sums still fit
        // in 16 bits (at most 21420); the weighted sum needs 32.
        for (int x = 0; x < width; x += 8)
        {
            var r0 = Vector128.LoadUnsafe(ref intermediate, (nuint)x);
            var r1 = Vector128.LoadUnsafe(ref intermediate, (nuint)(16 + x));
            var r2 = Vector128.LoadUnsafe(ref intermediate, (nuint)(32 + x));
            var r3 = Vector128.LoadUnsafe(ref intermediate, (nuint)(48 + x));
            var r4 = Vector128.LoadUnsafe(ref intermediate, (nuint)(64 + x));
            for (int j = 0; j < height; j++)
            {
                var r5 = Vector128.LoadUnsafe(ref intermediate, (nuint)(((j + 5) * 16) + x));
                var outer = r0 + r5;
                var middle = r1 + r4;
                var inner = r2 + r3;

                // outer - 5 middle + 20 inner = outer + 5 (4 inner - middle)
                var low = WeightedSum(Vector128.WidenLower(outer), Vector128.WidenLower(middle), Vector128.WidenLower(inner));
                var high = WeightedSum(Vector128.WidenUpper(outer), Vector128.WidenUpper(middle), Vector128.WidenUpper(inner));
                var clamped = Vector128.Narrow(Clamp(low), Clamp(high)).AsUInt16();
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dest, (j * 16) + x), Vector128.Narrow(clamped, clamped).AsUInt64().ToScalar());
                (r0, r1, r2, r3, r4) = (r1, r2, r3, r4, r5);
            }
        }
    }

    /// <summary>(outer + 5 (4 inner - middle) + 512) >> 10.</summary>
    private static Vector128<int> WeightedSum(Vector128<int> outer, Vector128<int> middle, Vector128<int> inner)
    {
        var d = (inner << 2) - middle;
        return (outer + (d << 2) + d + Vector128.Create(512)) >> 10;
    }

    /// <summary>
    /// Throws unless window[<paramref name="first"/> .. <paramref name="end"/>) and a 16 x
    /// <paramref name="height"/> output are there, which the unchecked loads and stores rely on.
    /// </summary>
    private static void CheckBounds(ReadOnlySpan<byte> window, int first, int end, Span<byte> output, int height)
    {
        if (first < 0 || end > window.Length || height * 16 > output.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }
    }

    private static Vector128<byte> Load(ref byte data, int offset) => Vector128.LoadUnsafe(ref data, (nuint)offset);

    private static Vector128<short> Lower(Vector128<byte> value) => Vector128.WidenLower(value).AsInt16();

    private static Vector128<short> Upper(Vector128<byte> value) => Vector128.WidenUpper(value).AsInt16();

    // The row copies and averages below check their bounds once a block (CheckRows: 16 readable bytes from each source
    // row's start, the block's width in each output row) and pick the row store for the block's width once, through
    // the type parameter, rather than in every row.

    private static void Store(ReadOnlySpan<byte> values, int width, int height, Span<byte> output, int stride)
    {
        CheckRows(values, 0, 16, height, output, stride, width);
        CopyRows(ref MemoryMarshal.GetReference(values), 16, width, height, ref MemoryMarshal.GetReference(output), stride);
    }

    private static void Average(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height, Span<byte> output, int stride)
    {
        CheckRows(a, 0, 16, height, output, stride, width);
        CheckRows(b, 0, 16, height, output, stride, width);
        AverageRows(ref MemoryMarshal.GetReference(a), 16, ref MemoryMarshal.GetReference(b), 16, width, height, ref MemoryMarshal.GetReference(output), stride);
    }

    private static void AverageWithWindow(ReadOnlySpan<byte> a, ReadOnlySpan<byte> window, int ws, int origin, int width, int height, Span<byte> output, int stride)
    {
        CheckRows(a, 0, 16, height, output, stride, width);
        CheckRows(window, origin, ws, height, output, stride, width);
        AverageRows(ref MemoryMarshal.GetReference(a), 16, ref Unsafe.Add(ref MemoryMarshal.GetReference(window), origin), ws, width, height, ref MemoryMarshal.GetReference(output), stride);
    }

    /// <summary>
    /// Throws unless <paramref name="height"/> rows of 16 bytes, <paramref name="sourceStride"/> apart from
    /// <paramref name="origin"/>, are in <paramref name="source"/>, and as many rows of <paramref name="width"/> bytes,
    /// <paramref name="outputStride"/> apart, in <paramref name="output"/>.
    /// </summary>
    private static void CheckRows(ReadOnlySpan<byte> source, int origin, int sourceStride, int height, Span<byte> output, int outputStride, int width)
    {
        if (origin < 0 || origin + ((height - 1) * sourceStride) + 16 > source.Length || ((height - 1) * outputStride) + width > output.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }
    }

    /// <summary>Copies the first <paramref name="width"/> (2, 4, 8 or 16) bytes of each row, unchecked.</summary>
    private static void CopyRows(ref byte source, int sourceStride, int width, int height, ref byte output, int outputStride)
    {
        switch (width)
        {
            case 16:
                CopyRows<Width16>(ref source, sourceStride, height, ref output, outputStride);
                break;
            case 8:
                CopyRows<Width8>(ref source, sourceStride, height, ref output, outputStride);
                break;
            case 4:
                CopyRows<Width4>(ref source, sourceStride, height, ref output, outputStride);
                break;
            default:
                CopyRows<Width2>(ref source, sourceStride, height, ref output, outputStride);
                break;
        }
    }

    private static void CopyRows<TWidth>(ref byte source, int sourceStride, int height, ref byte output, int outputStride)
        where TWidth : IRowWidth
    {
        for (int j = 0; j < height; j++)
        {
            TWidth.Write(Load(ref source, j * sourceStride), ref Unsafe.Add(ref output, j * outputStride));
        }
    }

    /// <summary>The rounded averages of two blocks' rows, their first <paramref name="width"/> (2, 4, 8 or 16) bytes, unchecked.</summary>
    private static void AverageRows(ref byte a, int aStride, ref byte b, int bStride, int width, int height, ref byte output, int outputStride)
    {
        switch (width)
        {
            case 16:
                AverageRows<Width16>(ref a, aStride, ref b, bStride, height, ref output, outputStride);
                break;
            case 8:
                AverageRows<Width8>(ref a, aStride, ref b, bStride, height, ref output, outputStride);
                break;
            case 4:
                AverageRows<Width4>(ref a, aStride, ref b, bStride, height, ref output, outputStride);
                break;
            default:
                AverageRows<Width2>(ref a, aStride, ref b, bStride, height, ref output, outputStride);
                break;
        }
    }

    private static void AverageRows<TWidth>(ref byte a, int aStride, ref byte b, int bStride, int height, ref byte output, int outputStride)
        where TWidth : IRowWidth
    {
        for (int j = 0; j < height; j++)
        {
            TWidth.Write(RoundingAverage(Load(ref a, j * aStride), Load(ref b, j * bStride)), ref Unsafe.Add(ref output, j * outputStride));
        }
    }

    /// <summary>A block's width: how many of a row's bytes are written.</summary>
    private interface IRowWidth
    {
        static abstract void Write(Vector128<byte> value, ref byte destination);
    }

    private readonly struct Width16 : IRowWidth
    {
        public static void Write(Vector128<byte> value, ref byte destination) => value.StoreUnsafe(ref destination);
    }

    private readonly struct Width8 : IRowWidth
    {
        public static void Write(Vector128<byte> value, ref byte destination) => Unsafe.WriteUnaligned(ref destination, value.AsUInt64().ToScalar());
    }

    private readonly struct Width4 : IRowWidth
    {
        public static void Write(Vector128<byte> value, ref byte destination) => Unsafe.WriteUnaligned(ref destination, value.AsUInt32().ToScalar());
    }

    private readonly struct Width2 : IRowWidth
    {
        public static void Write(Vector128<byte> value, ref byte destination) => Unsafe.WriteUnaligned(ref destination, value.AsUInt16().ToScalar());
    }

    /// <summary>
    /// Default weighted bi-prediction (8.4.2.3.1): the rounded average of two predictions of a
    /// <paramref name="width"/> x <paramref name="height"/> block, rows <paramref name="stride"/>
    /// apart in both (each buffer needs 16 readable bytes from every row start).
    /// </summary>
    public static void Average(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int stride, int width, int height, Span<byte> output, int outputStride)
    {
        CheckRows(a, 0, stride, height, output, outputStride, width);
        CheckRows(b, 0, stride, height, output, outputStride, width);
        AverageRows(ref MemoryMarshal.GetReference(a), stride, ref MemoryMarshal.GetReference(b), stride, width, height, ref MemoryMarshal.GetReference(output), outputStride);
    }

    /// <summary>(a + b + 1) >> 1 per byte, without widening.</summary>
    private static Vector128<byte> RoundingAverage(Vector128<byte> a, Vector128<byte> b) => (a | b) - ((a ^ b) >> 1);

    /// <summary>E - 5F + 20G + 20H - 5I + J, as (E + J) + 5 (4 (G + H) - (F + I)).</summary>
    private static Vector128<short> Tap(Vector128<short> e, Vector128<short> f, Vector128<short> g, Vector128<short> h, Vector128<short> i, Vector128<short> j)
    {
        var d = ((g + h) << 2) - (f + i);
        return (e + j) + (d << 2) + d;
    }

    /// <summary>Clip1((v + 16) >> 5) for two vectors of eight 16-bit values, as sixteen bytes.</summary>
    private static Vector128<byte> RoundHalf(Vector128<short> low, Vector128<short> high) =>
        Vector128.Narrow(RoundHalfWide(low), RoundHalfWide(high));

    private static Vector128<ushort> RoundHalfWide(Vector128<short> tap) =>
        Vector128.Min(Vector128.Max((tap + Vector128.Create((short)16)) >> 5, Vector128<short>.Zero), Vector128.Create((short)255)).AsUInt16();

    private static Vector128<int> Clamp(Vector128<int> value) =>
        Vector128.Min(Vector128.Max(value, Vector128<int>.Zero), Vector128.Create(255));

    private static byte Clip8(int value) => (byte)Math.Clamp(value, 0, 255);
}
#endif
