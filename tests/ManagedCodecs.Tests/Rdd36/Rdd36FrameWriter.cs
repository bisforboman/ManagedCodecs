using System.Buffers.Binary;
using ManagedCodecs.Rdd36;

namespace ManagedCodecs.Tests.Rdd36;

/// <summary>
/// Writes SMPTE RDD 36 frames for the tests, from the syntax tables of RDD 36:2022 (5.1 to 5.3) and the encoding side
/// of 7.1.1, on its own tables (typed from the document, not the decoder's): what FFmpeg's encoders never write, and
/// frames damaged in chosen places. A picture is given as quantized coefficients, a block at a time.
/// </summary>
internal sealed class Rdd36FrameWriter
{
    /// <summary>Figure 4 as printed: the scanned frequency index of the coefficient at [v][u].</summary>
    public static readonly int[][] ProgressiveScan =
    [
        [0, 1, 4, 5, 16, 17, 21, 22],
        [2, 3, 6, 7, 18, 20, 23, 28],
        [8, 9, 12, 13, 19, 24, 27, 29],
        [10, 11, 14, 15, 25, 26, 30, 31],
        [32, 33, 37, 38, 45, 46, 53, 54],
        [34, 36, 39, 44, 47, 52, 55, 60],
        [35, 40, 43, 48, 51, 56, 59, 61],
        [41, 42, 49, 50, 57, 58, 62, 63],
    ];

    // Tables 9, 10 and 11 as printed: an exponential-Golomb order alone, or lastRiceQ, kRice and kExp.
    private static readonly int[][] DcCodes = [[0], [1], [1, 2, 3], [3]];
    private static readonly int[][] RunCodes = [[2, 0, 1], [2, 0, 1], [1, 0, 1], [1, 0, 1], [0], [1, 1, 2], [1, 1, 2], [1, 1, 2], [1, 1, 2], [1], [1], [1], [1], [1], [1], [2]];
    private static readonly int[][] LevelCodes = [[2, 0, 2], [1, 0, 1], [2, 0, 1], [0], [1], [1], [1], [1], [2]];

    public Rdd36FrameWriter(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public int WidthInMacroblocks => (Width + 15) / 16;

    public int HeightInMacroblocks => (Height + 15) / 16;

    public int Log2SliceSize { get; init; } = 3;

    public int Version { get; init; }

    public int ChromaFormat { get; init; } = 2;

    public int InterlaceMode { get; init; }

    public int AlphaChannelType { get; init; }

    public int AspectRatioInformation { get; init; }

    public int ColorPrimaries { get; init; } = 2;

    public int TransferCharacteristic { get; init; } = 2;

    public int MatrixCoefficients { get; init; } = 2;

    /// <summary>The frame header's luma matrix at 8 * v + u, or null for none (the default matrix, 4 everywhere).</summary>
    public byte[]? LumaMatrix { get; init; }

    /// <summary>The frame header's chroma matrix, or null for none (the luma matrix).</summary>
    public byte[]? ChromaMatrix { get; init; }

    /// <summary>Bytes after the syntax elements of each frame header, picture header and slice header, counted in their sizes.</summary>
    public (int Frame, int Picture, int Slice) ExtraHeaderBytes { get; init; }

    /// <summary>Every reserved bit set (6.1.1: a decoder ignores them).</summary>
    public bool ReservedOnes { get; init; }

    /// <summary>Zero bytes after each component's coefficients, up to 3 (5.3.2).</summary>
    public int ZeroBytes { get; init; }

    /// <summary>Stuffing after the picture (5.1.2), counted in frame_size.</summary>
    public int Stuffing { get; init; }

    /// <summary>Each slice's quantization_index, by the slice's number in the picture; 4 where null.</summary>
    public Func<int, int>? QuantizationIndex { get; init; }

    /// <summary>The luma weights in effect.</summary>
    public byte[] EffectiveLumaMatrix => LumaMatrix ?? Enumerable.Repeat((byte)4, 64).ToArray();

    /// <summary>The chroma weights in effect.</summary>
    public byte[] EffectiveChromaMatrix => ChromaMatrix ?? EffectiveLumaMatrix;

    /// <summary>slice_size_in_mb (6.2) for a row of this picture.</summary>
    public List<int> SliceSizes()
    {
        var sizes = new List<int>();
        int size = 1 << Log2SliceSize;
        int left = WidthInMacroblocks;
        do
        {
            while (left >= size)
            {
                sizes.Add(size);
                left -= size;
            }

            size /= 2;
        }
        while (left > 0);
        return sizes;
    }

    /// <summary>
    /// The frame for <paramref name="picture"/>: for component 0, 1 or 2, the 64 quantized coefficients (at 8 * v + u)
    /// of the block whose top left sample is at (8 * blockX, 8 * blockY) of the component's plane.
    /// </summary>
    public byte[] Write(Func<int, int, int, int[]> picture)
    {
        var sizes = SliceSizes();
        var slices = new List<byte[]>();
        for (int row = 0; row < HeightInMacroblocks; row++)
        {
            int macroblock = 0;
            foreach (int size in sizes)
            {
                slices.Add(Slice(picture, row, macroblock, size, QuantizationIndex?.Invoke(slices.Count) ?? 4));
                macroblock += size;
            }
        }

        var header = new List<byte>();
        header.AddRange([0, 0]); // frame_header_size, below
        header.Add((byte)(ReservedOnes ? 0xFF : 0));
        header.Add((byte)Version);
        header.AddRange("test"u8.ToArray());
        header.AddRange([(byte)(Width >> 8), (byte)Width, (byte)(Height >> 8), (byte)Height]);
        header.Add((byte)((ChromaFormat << 6) | (InterlaceMode << 2) | (ReservedOnes ? 0x33 : 0)));
        header.Add((byte)(AspectRatioInformation << 4));
        header.AddRange([(byte)ColorPrimaries, (byte)TransferCharacteristic, (byte)MatrixCoefficients]);
        header.Add((byte)(AlphaChannelType | (ReservedOnes ? 0xF0 : 0)));
        header.Add((byte)(ReservedOnes ? 0xFF : 0));
        header.Add((byte)((LumaMatrix is null ? 0 : 2) | (ChromaMatrix is null ? 0 : 1) | (ReservedOnes ? 0xFC : 0)));
        header.AddRange(LumaMatrix ?? []);
        header.AddRange(ChromaMatrix ?? []);
        header.AddRange(Enumerable.Repeat((byte)0xA5, ExtraHeaderBytes.Frame));
        header[0] = (byte)(header.Count >> 8);
        header[1] = (byte)header.Count;

        int pictureHeaderSize = 8 + ExtraHeaderBytes.Picture;
        int pictureSize = pictureHeaderSize + (2 * slices.Count) + slices.Sum(s => s.Length);
        var frame = new List<byte>();
        frame.AddRange(new byte[4]); // frame_size, below
        frame.AddRange("icpf"u8.ToArray());
        frame.AddRange(header);
        frame.Add((byte)((pictureHeaderSize << 3) | (ReservedOnes ? 7 : 0)));
        frame.AddRange([(byte)(pictureSize >> 24), (byte)(pictureSize >> 16), (byte)(pictureSize >> 8), (byte)pictureSize]);
        frame.AddRange([(byte)(slices.Count >> 8), (byte)slices.Count]);
        frame.Add((byte)((Log2SliceSize << 4) | (ReservedOnes ? 0xCF : 0)));
        frame.AddRange(Enumerable.Repeat((byte)0xA5, ExtraHeaderBytes.Picture));
        foreach (var slice in slices)
        {
            frame.AddRange([(byte)(slice.Length >> 8), (byte)slice.Length]);
        }

        foreach (var slice in slices)
        {
            frame.AddRange(slice);
        }

        frame.AddRange(new byte[Stuffing]);
        var bytes = frame.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        return bytes;
    }

    /// <summary>
    /// The samples a decoder gives for <paramref name="picture"/> written by this writer: each block's coefficients
    /// times its weight and the slice's qScale (7.3; 16 bits, as the decoder takes them), through
    /// <see cref="Rdd36Idct.PutReference"/>, at its place (7.5.3), the samples past the picture's edges left out.
    /// </summary>
    public ushort[][] Expected(Func<int, int, int, int[]> picture)
    {
        int chromaWidth = (Width + 1) / 2;
        ushort[][] planes = [new ushort[Width * Height], new ushort[chromaWidth * Height], new ushort[chromaWidth * Height]];
        var sizes = SliceSizes();
        var block = new short[64];
        var samples = new ushort[64];
        for (int component = 0; component < 3; component++)
        {
            int planeWidth = component == 0 ? Width : chromaWidth;
            int blocksInMacroblock = component == 0 ? 2 : 1;
            var matrix = component == 0 ? EffectiveLumaMatrix : EffectiveChromaMatrix;
            for (int blockY = 0; blockY < HeightInMacroblocks * 2; blockY++)
            {
                for (int blockX = 0; blockX < WidthInMacroblocks * blocksInMacroblock; blockX++)
                {
                    int macroblock = blockX / blocksInMacroblock;
                    int slice = ((blockY / 2) * sizes.Count) + SliceOf(sizes, macroblock);
                    int scale = Rdd36Decoder.QuantizationScale(QuantizationIndex?.Invoke(slice) ?? 4);
                    var levels = picture(component, blockX, blockY);
                    for (int i = 0; i < 64; i++)
                    {
                        block[i] = unchecked((short)(levels[i] * matrix[i] * scale));
                    }

                    Rdd36Idct.PutReference(block, samples, 8);
                    for (int y = 0; y < 8 && (blockY * 8) + y < Height; y++)
                    {
                        for (int x = 0; x < 8 && (blockX * 8) + x < planeWidth; x++)
                        {
                            planes[component][((((blockY * 8) + y) * planeWidth) + (blockX * 8)) + x] = samples[(y * 8) + x];
                        }
                    }
                }
            }
        }

        return planes;
    }

    /// <summary>scanned_coefficients() (5.3.2) for a scanned array of <paramref name="blocks"/> * 64 coefficients, no zero bytes after.</summary>
    public static byte[] ScannedCoefficients(int[] scanned, int blocks)
    {
        var bits = new BitWriter();
        bits.Code(SymbolOf(scanned[0]), [5]);
        int previous = 3;
        for (int n = 1; n < blocks; n++)
        {
            int difference = scanned[n] - scanned[n - 1];
            bits.Code(SymbolOf(previous >= 0 ? difference : -difference), DcCodes[Math.Min(Math.Abs(previous), 3)]);
            previous = difference;
        }

        int previousRun = 4;
        int previousLevel = 1;
        int run = 0;
        for (int n = blocks; n < scanned.Length; n++)
        {
            if (scanned[n] == 0)
            {
                run++;
                continue;
            }

            int level = Math.Abs(scanned[n]) - 1;
            bits.Code(run, RunCodes[Math.Min(previousRun, 15)]);
            bits.Code(level, LevelCodes[Math.Min(previousLevel, 8)]);
            bits.Bits(scanned[n] < 0 ? 1u : 0u, 1);
            previousRun = run;
            previousLevel = level;
            run = 0;
        }

        return bits.ToArray();
    }

    /// <summary>The signed integer-to-symbol mapping S(n) (7.1.1.2).</summary>
    public static int SymbolOf(int n) => n >= 0 ? 2 * n : (-2 * n) - 1;

    private static int SliceOf(List<int> sizes, int macroblock)
    {
        int slice = 0;
        for (int start = 0; start + sizes[slice] <= macroblock; slice++)
        {
            start += sizes[slice];
        }

        return slice;
    }

    private byte[] Slice(Func<int, int, int, int[]> picture, int row, int firstMacroblock, int macroblocks, int quantizationIndex)
    {
        var parts = new byte[3][];
        for (int component = 0; component < 3; component++)
        {
            // Slice scanning (7.2.1): scanned[nB * sliceSizeInMb * n + nB * m + b], the luma blocks of a macroblock
            // 0 1 over 2 3, the chroma blocks 0 over 1 (Figures 6 and 7).
            int blocksInMacroblock = component == 0 ? 4 : 2;
            int blocks = blocksInMacroblock * macroblocks;
            var scanned = new int[blocks * 64];
            for (int m = 0; m < macroblocks; m++)
            {
                for (int b = 0; b < blocksInMacroblock; b++)
                {
                    int blockX = component == 0 ? ((firstMacroblock + m) * 2) + (b & 1) : firstMacroblock + m;
                    int blockY = (row * 2) + (component == 0 ? b >> 1 : b);
                    var levels = picture(component, blockX, blockY);
                    for (int v = 0; v < 8; v++)
                    {
                        for (int u = 0; u < 8; u++)
                        {
                            scanned[(blocks * ProgressiveScan[v][u]) + (blocksInMacroblock * m) + b] = levels[(8 * v) + u];
                        }
                    }
                }
            }

            parts[component] = [.. ScannedCoefficients(scanned, blocks), .. new byte[ZeroBytes]];
        }

        int headerSize = 6 + ExtraHeaderBytes.Slice;
        var slice = new List<byte>
        {
            (byte)((headerSize << 3) | (ReservedOnes ? 7 : 0)),
            (byte)quantizationIndex,
            (byte)(parts[0].Length >> 8),
            (byte)parts[0].Length,
            (byte)(parts[1].Length >> 8),
            (byte)parts[1].Length,
        };
        slice.AddRange(Enumerable.Repeat((byte)0xA5, ExtraHeaderBytes.Slice));
        slice.AddRange(parts[0]);
        slice.AddRange(parts[1]);
        slice.AddRange(parts[2]);
        return [.. slice];
    }

    /// <summary>Bits most significant first, and the codes of 7.1.1.1 as the document describes encoding them.</summary>
    internal sealed class BitWriter
    {
        private readonly List<bool> _bits = [];

        public int Length => _bits.Count;

        public void Bits(uint value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                _bits.Add(((value >> i) & 1) != 0);
            }
        }

        /// <summary>A codeword of the exponential-Golomb code of order code[0], or of the combination code lastRiceQ, kRice, kExp.</summary>
        public void Code(int symbol, int[] code)
        {
            if (code.Length == 1)
            {
                ExpGolomb(symbol, code[0]);
                return;
            }

            int lastRiceQ = code[0];
            int rice = code[1];
            if (symbol < (lastRiceQ + 1) << rice)
            {
                // Golomb-Rice: q zero bits, a one bit, the remainder in kRice bits.
                Bits(1, (symbol >> rice) + 1);
                Bits((uint)(symbol & ((1 << rice) - 1)), rice);
            }
            else
            {
                Bits(0, lastRiceQ + 1);
                ExpGolomb(symbol - ((lastRiceQ + 1) << rice), code[2]);
            }
        }

        /// <summary>The binary representation of symbol + 2^k, zero-extended by q = floor(log2(symbol + 2^k)) - k bits.</summary>
        public void ExpGolomb(int symbol, int order)
        {
            uint value = (uint)symbol + (1u << order);

            // The value's bits, counted by hand: under the Unity build (-p:UnityBuild=true) BitOperations is both the
            // runtime's and that assembly's polyfill.
            int length = 0;
            for (uint rest = value; rest != 0; rest >>= 1)
            {
                length++;
            }

            Bits(0, length - order - 1);
            Bits(value, length);
        }

        public byte[] ToArray()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (int i = 0; i < _bits.Count; i++)
            {
                if (_bits[i])
                {
                    bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
                }
            }

            return bytes;
        }
    }
}
