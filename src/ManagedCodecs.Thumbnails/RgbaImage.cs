using System.Runtime.InteropServices;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Thumbnails;

/// <summary>
/// A decoded frame as 8-bit RGBA at a smaller size, turned upright: the frame converted to RGB at its own size by
/// <see cref="RgbaConverter"/>, scaled down by area (each output pixel the mean of the source area it covers, partly
/// covered pixels weighted by their share), then rotated. That is the order of FFmpeg's
/// <c>-vf format=rgba,scale=W:H:flags=area</c> after its autorotate, the outside check (docs/conformance.md); scaling the
/// YUV planes first would be cheaper but blends chroma before the matrix, which differs visibly at colour edges. The
/// rows are converted and scaled one at a time, so no full-size RGBA image is held.
/// </summary>
internal static class RgbaImage
{
    /// <summary>
    /// <paramref name="frame"/> as <paramref name="width"/> x <paramref name="height"/> RGBA pixels (4 bytes each, R first,
    /// alpha 255), the size as shown: after <paramref name="rotation"/>, so a 90 or 270 degree turn takes the frame's
    /// height to the width. Neither side may exceed the frame's (turned) side: this scales down only.
    /// </summary>
    /// <param name="rotation">How far to turn the frame counter-clockwise: 0, 90, 180 or 270, as <see cref="VideoInfo.Rotation"/>.</param>
    public static byte[] Render(VideoFrame frame, int width, int height, int rotation, RgbaConverter converter)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(converter);
        bool turned = rotation switch
        {
            0 or 180 => false,
            90 or 270 => true,
            _ => throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "0, 90, 180 or 270."),
        };
        var (scaledWidth, scaledHeight) = turned ? (height, width) : (width, height);
        ArgumentOutOfRangeException.ThrowIfLessThan(scaledWidth, 1, turned ? nameof(height) : nameof(width));
        ArgumentOutOfRangeException.ThrowIfLessThan(scaledHeight, 1, turned ? nameof(width) : nameof(height));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scaledWidth, frame.Width, turned ? nameof(height) : nameof(width));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scaledHeight, frame.Height, turned ? nameof(width) : nameof(height));

        var scaled = new byte[scaledWidth * scaledHeight * 4];
        var scaler = new AreaScaler(frame.Width, frame.Height, scaledWidth, scaledHeight, scaled);
        var row = new byte[frame.Width * 4];
        for (int y = 0; y < frame.Height; y++)
        {
            converter.ConvertRow(frame, y, row);
            scaler.AddRow(row);
        }

        return Rotate(scaled, scaledWidth, scaledHeight, rotation);
    }

    /// <summary>
    /// <paramref name="rgba"/> (<paramref name="width"/> x <paramref name="height"/>) turned counter-clockwise by
    /// <paramref name="rotation"/> degrees: at 90 the source's top row becomes the left column, read bottom to top.
    /// </summary>
    internal static byte[] Rotate(byte[] rgba, int width, int height, int rotation)
    {
        if (rotation == 0)
        {
            return rgba;
        }

        var turned = new byte[rgba.Length];
        var source = MemoryMarshalCast(rgba);
        var target = MemoryMarshalCast(turned);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // (x, y) moves to: 90 -> (y, width-1-x) in a height-wide image; 180 -> (width-1-x, height-1-y);
                // 270 -> (height-1-y, x) in a height-wide image.
                int to = rotation switch
                {
                    90 => ((width - 1 - x) * height) + y,
                    180 => ((height - 1 - y) * width) + (width - 1 - x),
                    _ => (x * height) + (height - 1 - y),
                };
                target[to] = source[(y * width) + x];
            }
        }

        return turned;
    }

    private static Span<uint> MemoryMarshalCast(byte[] rgba) => MemoryMarshal.Cast<byte, uint>(rgba.AsSpan());
}

/// <summary>
/// Scales RGBA rows down by area, fed one source row at a time. In units where a source pixel is
/// <c>targetWidth</c> wide and an output pixel <c>sourceWidth</c> wide, every overlap is a whole number, so each output
/// value is the exact mean <c>Σ overlap · value / (sourceWidth · sourceHeight)</c>, rounded half up: no weight is
/// rounded, and the result is the same as FFmpeg's area scaler's where that is exact (docs/conformance.md).
/// </summary>
internal sealed class AreaScaler
{
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly int _targetWidth;
    private readonly int _targetHeight;
    private readonly byte[] _target;
    private readonly int[] _columnStart; // per source column: the output column it starts in
    private readonly int[] _columnSplit; // its overlap with that column (the rest, up to targetWidth, goes to the next)
    private readonly int[] _horizontal;
    private readonly long[] _current;
    private readonly long[] _next;
    private int _y;

    public AreaScaler(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, byte[] target)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetHeight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetWidth, sourceWidth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetHeight, sourceHeight);
        ArgumentOutOfRangeException.ThrowIfLessThan(target.Length, targetWidth * targetHeight * 4);
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _targetWidth = targetWidth;
        _targetHeight = targetHeight;
        _target = target;
        _columnStart = new int[sourceWidth];
        _columnSplit = new int[sourceWidth];
        for (int x = 0; x < sourceWidth; x++)
        {
            // Source column x covers [x·tw, (x+1)·tw); output column c covers [c·sw, (c+1)·sw). Downscaling, a source
            // column overlaps one output column or two.
            long start = (long)x * targetWidth;
            int column = (int)(start / sourceWidth);
            long columnEnd = (long)(column + 1) * sourceWidth;
            _columnStart[x] = column;
            _columnSplit[x] = (int)Math.Min(targetWidth, columnEnd - start);
        }

        _horizontal = new int[targetWidth * 3];
        _current = new long[targetWidth * 3];
        _next = new long[targetWidth * 3];
    }

    /// <summary>Adds the next source row (sourceWidth RGBA pixels; alpha ignored, written as 255).</summary>
    public void AddRow(ReadOnlySpan<byte> row)
    {
        if (_y >= _sourceHeight)
        {
            throw new InvalidOperationException("All rows were added.");
        }

        // The row across: each value into the output columns it overlaps, weighted by the overlap (at most
        // 255 · sourceWidth an output column, an int).
        var horizontal = _horizontal.AsSpan();
        horizontal.Clear();
        for (int x = 0; x < _sourceWidth; x++)
        {
            int column = _columnStart[x];
            int first = _columnSplit[x];
            int second = _targetWidth - first;
            int to = column * 3;
            for (int c = 0; c < 3; c++)
            {
                int value = row[(x * 4) + c];
                horizontal[to + c] += first * value;
                if (second > 0)
                {
                    horizontal[to + 3 + c] += second * value;
                }
            }
        }

        // The row down: the same split between the output row it starts in and the next.
        long start = (long)_y * _targetHeight;
        int outputRow = (int)(start / _sourceHeight);
        int firstWeight = (int)Math.Min(_targetHeight, ((long)(outputRow + 1) * _sourceHeight) - start);
        int secondWeight = _targetHeight - firstWeight;
        for (int i = 0; i < horizontal.Length; i++)
        {
            _current[i] += (long)firstWeight * horizontal[i];
            _next[i] += (long)secondWeight * horizontal[i];
        }

        _y++;

        // The output row is complete once this source row reaches its end (always at the last source row).
        if ((long)_y * _targetHeight >= (long)(outputRow + 1) * _sourceHeight)
        {
            Write(outputRow);
            _next.AsSpan().CopyTo(_current);
            _next.AsSpan().Clear();
        }
    }

    private void Write(int outputRow)
    {
        long area = (long)_sourceWidth * _sourceHeight;
        var target = _target.AsSpan(outputRow * _targetWidth * 4, _targetWidth * 4);
        for (int x = 0; x < _targetWidth; x++)
        {
            for (int c = 0; c < 3; c++)
            {
                target[(x * 4) + c] = (byte)(((2 * _current[(x * 3) + c]) + area) / (2 * area));
            }

            target[(x * 4) + 3] = 255;
        }
    }
}

/// <summary>
/// Converts a frame's rows to 8-bit RGBA by a matrix and a range, any of the library's layouts (4:0:0, 4:2:0, 4:2:2,
/// 4:4:4) at any depth, each pixel taking the chroma sample its position falls in (nearest neighbour, as FFmpeg's
/// <c>format=rgba</c> does by default). Fixed point with 20 fractional bits on the raw samples: within one level of the
/// definition in double precision (the tests' reference), rounded half up and clamped. The identity matrix (H.273's 0,
/// 4:4:4 only) carries G, B and R in the three planes, each taken as luma is. The transfer and primaries are not
/// converted: the RGB is the source's, as players show SDR video.
/// </summary>
internal sealed class RgbaConverter
{
    private const int Bits = 20;
    private const int Half = 1 << (Bits - 1);

    private readonly int _depth;
    private readonly int _yScale;
    private readonly int _yOffset;
    private readonly int _chromaOffset;
    private readonly int _crToR;
    private readonly int _cbToG;
    private readonly int _crToG;
    private readonly int _cbToB;

    private RgbaConverter(double kr, double kb, bool fullRange, int depth, bool identity = false)
    {
        // R = Y + 2 (1 - Kr) Cr, B = Y + 2 (1 - Kb) Cb, and G from Y = Kr R + Kg G + Kb B (ITU-T H.273, equations 38 to
        // 40 solved for R, G and B), Y and C first brought to 0..255 and -128..127: limited range is 219 (luma) and 224
        // (chroma) levels from 16 and 128, scaled by the depth (2^(depth - 8)).
        double scale = 1 << (depth - 8);
        double kg = 1 - kr - kb;
        double yScale = (fullRange ? 1 : 255.0 / 219) / scale;
        double cScale = (fullRange ? 1 : 255.0 / 224) / scale;
        _depth = depth;
        _yScale = Fixed(yScale);
        _yOffset = fullRange ? 0 : 16 << (depth - 8);
        _chromaOffset = 128 << (depth - 8);
        _crToR = Fixed(2 * (1 - kr) * cScale);
        _cbToB = Fixed(2 * (1 - kb) * cScale);
        _cbToG = Fixed(2 * (1 - kb) * kb / kg * cScale);
        _crToG = Fixed(2 * (1 - kr) * kr / kg * cScale);
        Kr = kr;
        Kb = kb;
        FullRange = fullRange;
        Identity = identity;
    }

    /// <summary>The matrix's Kr (ITU-T H.273 Table 4).</summary>
    public double Kr { get; }

    /// <summary>The matrix's Kb.</summary>
    public double Kb { get; }

    /// <summary>Whether the samples are full range, else limited.</summary>
    public bool FullRange { get; }

    /// <summary>Whether the planes are G, B and R (the identity matrix), not Y, Cb and Cr.</summary>
    public bool Identity { get; }

    /// <summary>
    /// The converter for <paramref name="frame"/>, by its matrix and range unless <paramref name="matrix"/> or
    /// <paramref name="range"/> say otherwise. The matrices FFmpeg's scaler knows: BT.709, FCC, BT.601 (BT.470BG,
    /// SMPTE 170M), SMPTE 240M and BT.2020 (constant luminance converted as if it were not); any other, or none said,
    /// as BT.601, what FFmpeg's scaler does with them, not a guess by picture size. A range not said is limited, but
    /// full for MJPEG: JPEG's samples are full range (T.81 with JFIF), as FFmpeg converts its grey frames (the colour
    /// ones say full themselves). The identity matrix on a 4:4:4 frame takes the planes as G, B and R (FFmpeg's
    /// <c>gbrp</c>); on subsampled chroma, which H.264 forbids it, it converts as BT.601.
    /// </summary>
    /// <param name="codec">The video's codec (<see cref="VideoStreamInfo.Codec"/>), for the range MJPEG doesn't say.</param>
    public static RgbaConverter For(VideoFrame frame, string codec, ColorMatrix? matrix = null, ColorRange? range = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var said = matrix ?? frame.ColorMatrix;
        var (kr, kb) = Coefficients(said);
        var saidRange = range ?? frame.ColorRange;
        bool full = saidRange == ColorRange.Full || (saidRange == ColorRange.Unspecified && codec == CodecIds.Mjpeg);
        bool identity = said == ColorMatrix.Identity && frame.Format.PlaneCount() == 3
            && frame.Format.PlaneSize(1, frame.Width, frame.Height) == (frame.Width, frame.Height);
        return new RgbaConverter(kr, kb, full, frame.Format.BitDepth(), identity);
    }

    /// <summary>A converter by Kr and Kb themselves (the tests' random matrices), or for G, B and R planes.</summary>
    internal static RgbaConverter Of(double kr, double kb, bool fullRange, int depth, bool identity = false) =>
        new(kr, kb, fullRange, depth, identity);

    /// <summary>Kr and Kb of <paramref name="matrix"/>, BT.601's for the matrices FFmpeg's scaler doesn't know.</summary>
    internal static (double Kr, double Kb) Coefficients(ColorMatrix matrix) => matrix switch
    {
        ColorMatrix.Bt709 => (0.2126, 0.0722),
        ColorMatrix.Fcc => (0.30, 0.11),
        ColorMatrix.Smpte240M => (0.212, 0.087),
        ColorMatrix.Bt2020Ncl or ColorMatrix.Bt2020Cl => (0.2627, 0.0593),
        _ => (0.299, 0.114),
    };

    /// <summary>Writes row <paramref name="y"/> of <paramref name="frame"/> into <paramref name="rgba"/> (width x 4 bytes).</summary>
    public void ConvertRow(VideoFrame frame, int y, Span<byte> rgba)
    {
        var format = frame.Format;
        if (format.BitDepth() != _depth)
        {
            throw new ArgumentException($"The converter is for {_depth}-bit samples, the frame's are {format.BitDepth()}-bit.", nameof(frame));
        }

        int width = frame.Width;
        rgba = rgba[..(width * 4)];
        var luma = frame.GetRow(0, y);
        if (format.PlaneCount() == 1)
        {
            if (format.BytesPerSample() == 2)
            {
                Grey(MemoryMarshal.Cast<byte, ushort>(luma), rgba);
            }
            else
            {
                Grey(luma, rgba);
            }

            return;
        }

        if (Identity)
        {
            // G, B and R in the planes' order, written as R, G and B; For takes the identity only with full-size planes.
            if (format.BytesPerSample() == 2)
            {
                Planes(MemoryMarshal.Cast<byte, ushort>(luma), MemoryMarshal.Cast<byte, ushort>(frame.GetRow(1, y)), MemoryMarshal.Cast<byte, ushort>(frame.GetRow(2, y)), rgba);
            }
            else
            {
                Planes(luma, frame.GetRow(1, y), frame.GetRow(2, y), rgba);
            }

            return;
        }

        var (chromaWidth, chromaHeight) = format.PlaneSize(1, frame.Width, frame.Height);
        int shiftX = Shift(width, chromaWidth);
        int chromaY = y >> Shift(frame.Height, chromaHeight);
        var cb = frame.GetRow(1, chromaY);
        var cr = frame.GetRow(2, chromaY);

        // The samples as little-endian words where deeper than 8 bits (the decoders' layout, read in place: .NET's
        // platforms are little-endian).
        if (format.BytesPerSample() == 2)
        {
            Colour(MemoryMarshal.Cast<byte, ushort>(luma), MemoryMarshal.Cast<byte, ushort>(cb), MemoryMarshal.Cast<byte, ushort>(cr), shiftX, rgba);
        }
        else
        {
            Colour(luma, cb, cr, shiftX, rgba);
        }
    }

    private void Grey(ReadOnlySpan<byte> luma, Span<byte> rgba)
    {
        for (int x = 0; x < luma.Length; x++)
        {
            byte value = Clamp((_yScale * (luma[x] - _yOffset)) + Half);
            rgba[x * 4] = value;
            rgba[(x * 4) + 1] = value;
            rgba[(x * 4) + 2] = value;
            rgba[(x * 4) + 3] = 255;
        }
    }

    private void Grey(ReadOnlySpan<ushort> luma, Span<byte> rgba)
    {
        for (int x = 0; x < luma.Length; x++)
        {
            byte value = Clamp((_yScale * (luma[x] - _yOffset)) + Half);
            rgba[x * 4] = value;
            rgba[(x * 4) + 1] = value;
            rgba[(x * 4) + 2] = value;
            rgba[(x * 4) + 3] = 255;
        }
    }

    private void Planes(ReadOnlySpan<byte> g, ReadOnlySpan<byte> b, ReadOnlySpan<byte> r, Span<byte> rgba)
    {
        for (int x = 0; x < g.Length; x++)
        {
            var pixel = rgba.Slice(x * 4, 4);
            pixel[3] = 255;
            pixel[0] = Clamp((_yScale * (r[x] - _yOffset)) + Half);
            pixel[1] = Clamp((_yScale * (g[x] - _yOffset)) + Half);
            pixel[2] = Clamp((_yScale * (b[x] - _yOffset)) + Half);
        }
    }

    private void Planes(ReadOnlySpan<ushort> g, ReadOnlySpan<ushort> b, ReadOnlySpan<ushort> r, Span<byte> rgba)
    {
        for (int x = 0; x < g.Length; x++)
        {
            var pixel = rgba.Slice(x * 4, 4);
            pixel[3] = 255;
            pixel[0] = Clamp((_yScale * (r[x] - _yOffset)) + Half);
            pixel[1] = Clamp((_yScale * (g[x] - _yOffset)) + Half);
            pixel[2] = Clamp((_yScale * (b[x] - _yOffset)) + Half);
        }
    }

    // One loop each for bytes and words (the same arithmetic), so the sample width is not tested per sample.
    private void Colour(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> cbRow, ReadOnlySpan<byte> crRow, int shiftX, Span<byte> rgba)
    {
        for (int x = 0; x < luma.Length; x++)
        {
            int luminance = (_yScale * (luma[x] - _yOffset)) + Half;
            int cb = cbRow[x >> shiftX] - _chromaOffset;
            int cr = crRow[x >> shiftX] - _chromaOffset;
            var pixel = rgba.Slice(x * 4, 4);
            pixel[3] = 255;
            pixel[0] = Clamp(luminance + (_crToR * cr));
            pixel[1] = Clamp(luminance - (_cbToG * cb) - (_crToG * cr));
            pixel[2] = Clamp(luminance + (_cbToB * cb));
        }
    }

    private void Colour(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> cbRow, ReadOnlySpan<ushort> crRow, int shiftX, Span<byte> rgba)
    {
        for (int x = 0; x < luma.Length; x++)
        {
            int luminance = (_yScale * (luma[x] - _yOffset)) + Half;
            int cb = cbRow[x >> shiftX] - _chromaOffset;
            int cr = crRow[x >> shiftX] - _chromaOffset;
            var pixel = rgba.Slice(x * 4, 4);
            pixel[3] = 255;
            pixel[0] = Clamp(luminance + (_crToR * cr));
            pixel[1] = Clamp(luminance - (_cbToG * cb) - (_crToG * cr));
            pixel[2] = Clamp(luminance + (_cbToB * cb));
        }
    }

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

    private static byte Clamp(int value)
    {
        value >>= Bits;
        return (byte)((uint)value <= 255 ? value : value < 0 ? 0 : 255);
    }

    private static int Fixed(double value) => (int)Math.Round(value * (1 << Bits));
}
