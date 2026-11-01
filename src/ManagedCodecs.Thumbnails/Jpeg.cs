using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Thumbnails;

/// <summary>
/// Writes 8-bit RGBA as a baseline JPEG file (ITU-T T.81, JFIF 1.02): RGB to YCbCr by JFIF's equations (full range),
/// chroma halved both ways (4:2:0, each chroma sample the mean of four), the forward DCT of A.3.3, quantized by Annex
/// K.1's tables scaled by a quality from 1 to 100 (the scale libjpeg made common: 5000 / quality below 50, 200 - 2 x
/// quality above), and Huffman coded with Annex K.3's tables (F.1.2). Alpha is dropped. Everything is integer
/// arithmetic, so every runtime writes the same bytes.
/// </summary>
internal static class Jpeg
{
    /// <summary>Fixed-point bits of <see cref="Cosines"/>.</summary>
    private const int CosineBits = 15;

    /// <summary>Annex K.1, Table K.1: the luminance quantization table, in natural (row-major) order.</summary>
    private static ReadOnlySpan<byte> LuminanceQuantization =>
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ];

    /// <summary>Annex K.1, Table K.2: the chrominance quantization table, in natural order.</summary>
    private static ReadOnlySpan<byte> ChrominanceQuantization =>
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];

    /// <summary>
    /// A.3.3's basis, C(u) / 2 x cos((2x + 1)uπ / 16) at [u * 8 + x], times 2^15 and rounded: no value is within 10^-3
    /// of a rounding tie, so every runtime's cosine rounds it the same way.
    /// </summary>
    private static readonly int[] Cosines = MakeCosines();

    private static readonly HuffmanCode DcLuminance = new(JpegTables.DcLuminanceBits, JpegTables.DcLuminanceValues);
    private static readonly HuffmanCode AcLuminance = new(JpegTables.AcLuminanceBits, JpegTables.AcLuminanceValues);
    private static readonly HuffmanCode DcChrominance = new(JpegTables.DcChrominanceBits, JpegTables.DcChrominanceValues);
    private static readonly HuffmanCode AcChrominance = new(JpegTables.AcChrominanceBits, JpegTables.AcChrominanceValues);

    /// <summary>
    /// Writes <paramref name="rgba"/>, <paramref name="width"/> x <paramref name="height"/> pixels, to
    /// <paramref name="destination"/> at <paramref name="quality"/> (1 to 100).
    /// </summary>
    public static void Write(Stream destination, ReadOnlySpan<byte> rgba, int width, int height, int quality)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 65535); // the frame header's 16 bits
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, 65535);
        ArgumentOutOfRangeException.ThrowIfNotEqual(rgba.Length, width * height * 4, nameof(rgba));
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);

        var luminance = new byte[64];
        var chrominance = new byte[64];
        ScaleQuantization(LuminanceQuantization, quality, luminance);
        ScaleQuantization(ChrominanceQuantization, quality, chrominance);

        var (y, cb, cr, lumaStride, chromaStride, mcuColumns, mcuRows) = ToYCbCr(rgba, width, height);

        WriteHeaders(destination, width, height, luminance, chrominance);
        var writer = new BitWriter(destination);
        Span<int> block = stackalloc int[64];
        Span<int> quantized = stackalloc int[64];
        int dcY = 0;
        int dcCb = 0;
        int dcCr = 0;
        for (int my = 0; my < mcuRows; my++)
        {
            for (int mx = 0; mx < mcuColumns; mx++)
            {
                // A.2.3: an MCU is the four luminance blocks of a 16x16 area, left to right and top to bottom, then one
                // block each of Cb and Cr.
                for (int b = 0; b < 4; b++)
                {
                    Gather(y, lumaStride, (mx * 16) + ((b & 1) * 8), (my * 16) + ((b >> 1) * 8), block);
                    Quantize(block, luminance, quantized);
                    EncodeBlock(ref writer, quantized, ref dcY, DcLuminance, AcLuminance);
                }

                Gather(cb, chromaStride, mx * 8, my * 8, block);
                Quantize(block, chrominance, quantized);
                EncodeBlock(ref writer, quantized, ref dcCb, DcChrominance, AcChrominance);
                Gather(cr, chromaStride, mx * 8, my * 8, block);
                Quantize(block, chrominance, quantized);
                EncodeBlock(ref writer, quantized, ref dcCr, DcChrominance, AcChrominance);
            }
        }

        writer.Flush();
        destination.Write([0xFF, 0xD9]); // EOI
    }

    /// <summary>
    /// <paramref name="table"/> scaled for <paramref name="quality"/>: each entry times 5000 / quality (below 50) or
    /// 200 - 2 x quality (from 50), over 100, rounded, and kept within 1 to 255 (baseline's 8-bit tables).
    /// </summary>
    internal static void ScaleQuantization(ReadOnlySpan<byte> table, int quality, Span<byte> scaled)
    {
        int scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);
        for (int i = 0; i < 64; i++)
        {
            scaled[i] = (byte)Math.Clamp(((table[i] * scale) + 50) / 100, 1, 255);
        }
    }

    /// <summary>
    /// JFIF's YCbCr (full range: Y = 0.299 R + 0.587 G + 0.114 B, Cb and Cr centred on 128), in 16-bit fixed point,
    /// the planes padded to whole MCUs by repeating the last column and row (A.2.4 leaves the padding to the encoder),
    /// and the chroma planes halved both ways, each sample the rounded mean of the four it covers.
    /// </summary>
    internal static (byte[] Y, byte[] Cb, byte[] Cr, int LumaStride, int ChromaStride, int McuColumns, int McuRows) ToYCbCr(ReadOnlySpan<byte> rgba, int width, int height)
    {
        int mcuColumns = (width + 15) / 16;
        int mcuRows = (height + 15) / 16;
        int lumaStride = mcuColumns * 16;
        int lumaHeight = mcuRows * 16;
        var y = new byte[lumaStride * lumaHeight];
        var cbFull = new byte[y.Length];
        var crFull = new byte[y.Length];
        for (int row = 0; row < lumaHeight; row++)
        {
            int sourceRow = Math.Min(row, height - 1);
            for (int column = 0; column < lumaStride; column++)
            {
                int i = ((sourceRow * width) + Math.Min(column, width - 1)) * 4;
                int r = rgba[i];
                int g = rgba[i + 1];
                int b = rgba[i + 2];
                int o = (row * lumaStride) + column;
                y[o] = (byte)(((19595 * r) + (38470 * g) + (7471 * b) + 32768) >> 16);
                cbFull[o] = (byte)Math.Clamp(((-11059 * r) - (21709 * g) + (32768 * b) + (128 << 16) + 32768) >> 16, 0, 255);
                crFull[o] = (byte)Math.Clamp(((32768 * r) - (27439 * g) - (5329 * b) + (128 << 16) + 32768) >> 16, 0, 255);
            }
        }

        int chromaStride = lumaStride / 2;
        var cb = new byte[chromaStride * (lumaHeight / 2)];
        var cr = new byte[cb.Length];
        for (int row = 0; row < lumaHeight / 2; row++)
        {
            for (int column = 0; column < chromaStride; column++)
            {
                int o = (row * 2 * lumaStride) + (column * 2);
                cb[(row * chromaStride) + column] = (byte)((cbFull[o] + cbFull[o + 1] + cbFull[o + lumaStride] + cbFull[o + lumaStride + 1] + 2) >> 2);
                cr[(row * chromaStride) + column] = (byte)((crFull[o] + crFull[o + 1] + crFull[o + lumaStride] + crFull[o + lumaStride + 1] + 2) >> 2);
            }
        }

        return (y, cb, cr, lumaStride, chromaStride, mcuColumns, mcuRows);
    }

    /// <summary>The 8x8 block at (<paramref name="x"/>, <paramref name="y"/>) of a plane, level shifted to -128 to 127 (A.3.1).</summary>
    private static void Gather(byte[] plane, int stride, int x, int y, Span<int> block)
    {
        for (int row = 0; row < 8; row++)
        {
            int o = ((y + row) * stride) + x;
            for (int column = 0; column < 8; column++)
            {
                block[(row * 8) + column] = plane[o + column] - 128;
            }
        }
    }

    /// <summary>A.3.3's forward DCT of a level-shifted block, rows then columns, in fixed point: each coefficient times 2^30.</summary>
    internal static void ForwardDct(ReadOnlySpan<int> block, Span<long> coefficients)
    {
        Span<long> rows = stackalloc long[64];
        for (int y = 0; y < 8; y++)
        {
            for (int u = 0; u < 8; u++)
            {
                long sum = 0;
                for (int x = 0; x < 8; x++)
                {
                    sum += (long)Cosines[(u * 8) + x] * block[(y * 8) + x];
                }

                rows[(y * 8) + u] = sum;
            }
        }

        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                long sum = 0;
                for (int y = 0; y < 8; y++)
                {
                    sum += Cosines[(v * 8) + y] * rows[(y * 8) + u];
                }

                coefficients[(v * 8) + u] = sum;
            }
        }
    }

    /// <summary>
    /// The block's DCT divided by the quantization table and rounded half away from zero (A.3.4), in zig-zag order
    /// (Figure A.6), as F.1.2 codes it.
    /// </summary>
    private static void Quantize(ReadOnlySpan<int> block, ReadOnlySpan<byte> table, Span<int> zigZag)
    {
        Span<long> coefficients = stackalloc long[64];
        ForwardDct(block, coefficients);
        for (int k = 0; k < 64; k++)
        {
            int natural = JpegTables.ZigZag[k];
            long divisor = (long)table[natural] << (2 * CosineBits);
            long value = coefficients[natural];
            long magnitude = ((value < 0 ? -value : value) + (divisor / 2)) / divisor;
            zigZag[k] = (int)(value < 0 ? -magnitude : magnitude);
        }
    }

    /// <summary>F.1.2.1 and F.1.2.2: the DC difference, then the AC coefficients as runs of zeros, ZRL and EOB.</summary>
    private static void EncodeBlock(ref BitWriter writer, ReadOnlySpan<int> zigZag, ref int predictor, HuffmanCode dc, HuffmanCode ac)
    {
        int difference = zigZag[0] - predictor;
        predictor = zigZag[0];
        int size = Category(difference);
        dc.Write(ref writer, size);
        writer.Write(Amplitude(difference, size), size);

        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int value = zigZag[k];
            if (value == 0)
            {
                run++;
                continue;
            }

            for (; run > 15; run -= 16)
            {
                ac.Write(ref writer, 0xF0); // ZRL: sixteen zeros
            }

            size = Category(value);
            ac.Write(ref writer, (run << 4) | size);
            writer.Write(Amplitude(value, size), size);
            run = 0;
        }

        if (run > 0)
        {
            ac.Write(ref writer, 0x00); // EOB
        }
    }

    /// <summary>SSSS (Tables F.1 and F.2): the bits a value's magnitude takes.</summary>
    internal static int Category(int value)
    {
        int magnitude = value < 0 ? -value : value;
        int size = 0;
        while (magnitude > 0)
        {
            size++;
            magnitude >>= 1;
        }

        return size;
    }

    /// <summary>The low <paramref name="size"/> bits that follow a category (F.1.2.1): the value, or for a negative one the value minus one.</summary>
    internal static int Amplitude(int value, int size) => (value < 0 ? value - 1 : value) & ((1 << size) - 1);

    private static void WriteHeaders(Stream destination, int width, int height, ReadOnlySpan<byte> luminance, ReadOnlySpan<byte> chrominance)
    {
        destination.Write([0xFF, 0xD8]); // SOI

        // APP0, JFIF 1.02: no density unit, an aspect ratio of 1:1, no thumbnail.
        destination.Write([0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 0, 0, 1, 0, 1, 0, 0]);

        // DQT (B.2.4.1): two 8-bit tables, in zig-zag order.
        Span<byte> dqt = stackalloc byte[4 + (2 * 65)];
        dqt[0] = 0xFF;
        dqt[1] = 0xDB;
        dqt[2] = 0;
        dqt[3] = 2 + (2 * 65);
        dqt[4] = 0; // Pq 0 (8 bits), Tq 0
        dqt[69] = 1; // Tq 1
        for (int k = 0; k < 64; k++)
        {
            dqt[5 + k] = luminance[JpegTables.ZigZag[k]];
            dqt[70 + k] = chrominance[JpegTables.ZigZag[k]];
        }

        destination.Write(dqt);

        // SOF0 (B.2.2): baseline, 8 bits, three components: Y sampled 2x2 with table 0, Cb and Cr 1x1 with table 1.
        destination.Write(
        [
            0xFF, 0xC0, 0, 17, 8,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
            3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
        ]);

        // DHT (B.2.4.2): Annex K.3's four tables.
        WriteHuffmanTable(destination, 0x00, JpegTables.DcLuminanceBits, JpegTables.DcLuminanceValues);
        WriteHuffmanTable(destination, 0x10, JpegTables.AcLuminanceBits, JpegTables.AcLuminanceValues);
        WriteHuffmanTable(destination, 0x01, JpegTables.DcChrominanceBits, JpegTables.DcChrominanceValues);
        WriteHuffmanTable(destination, 0x11, JpegTables.AcChrominanceBits, JpegTables.AcChrominanceValues);

        // SOS (B.2.3): the three components in one scan, Y with tables 0, Cb and Cr with tables 1; Ss 0, Se 63, Ah Al 0.
        destination.Write([0xFF, 0xDA, 0, 12, 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);
    }

    private static void WriteHuffmanTable(Stream destination, byte classAndId, ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
    {
        int length = 2 + 1 + 16 + values.Length;
        destination.Write([0xFF, 0xC4, (byte)(length >> 8), (byte)length, classAndId]);
        destination.Write(bits);
        destination.Write(values);
    }

    private static int[] MakeCosines()
    {
        var cosines = new int[64];
        for (int u = 0; u < 8; u++)
        {
            double c = u == 0 ? 1 / Math.Sqrt(2) : 1;
            for (int x = 0; x < 8; x++)
            {
                cosines[(u * 8) + x] = (int)Math.Round(c / 2 * Math.Cos(((2 * x) + 1) * u * Math.PI / 16) * (1 << CosineBits));
            }
        }

        return cosines;
    }

    /// <summary>A Huffman table's codes by value (C.2, Figures C.1 to C.3: sizes, then codes counting up within each size).</summary>
    private sealed class HuffmanCode
    {
        private readonly ushort[] _codes = new ushort[256];
        private readonly byte[] _sizes = new byte[256];

        public HuffmanCode(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
        {
            int code = 0;
            int k = 0;
            for (int size = 1; size <= 16; size++)
            {
                for (int i = 0; i < bits[size - 1]; i++)
                {
                    _codes[values[k]] = (ushort)code;
                    _sizes[values[k]] = (byte)size;
                    code++;
                    k++;
                }

                code <<= 1;
            }
        }

        public void Write(ref BitWriter writer, int value) => writer.Write(_codes[value], _sizes[value]);
    }

    /// <summary>The entropy-coded segment's bits, most significant first, a zero byte stuffed after each 0xFF (F.1.2.3).</summary>
    private struct BitWriter(Stream destination)
    {
        private readonly byte[] _buffer = new byte[4096];
        private int _count;
        private uint _bits;
        private int _bitCount;

        public void Write(int value, int size)
        {
            if (size == 0)
            {
                return;
            }

            _bits = (_bits << size) | (uint)(value & ((1 << size) - 1));
            _bitCount += size;
            while (_bitCount >= 8)
            {
                _bitCount -= 8;
                Put((byte)(_bits >> _bitCount));
            }
        }

        /// <summary>Fills the last byte with 1 bits (F.1.2.3) and writes out what is buffered.</summary>
        public void Flush()
        {
            if (_bitCount > 0)
            {
                Write((1 << (8 - _bitCount)) - 1, 8 - _bitCount);
            }

            destination.Write(_buffer, 0, _count);
            _count = 0;
        }

        private void Put(byte value)
        {
            if (_count >= _buffer.Length - 1)
            {
                destination.Write(_buffer, 0, _count);
                _count = 0;
            }

            _buffer[_count++] = value;
            if (value == 0xFF)
            {
                _buffer[_count++] = 0;
            }
        }
    }
}
