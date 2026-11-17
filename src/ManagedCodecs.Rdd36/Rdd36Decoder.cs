using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ManagedCodecs.Rdd36;

/// <summary>
/// Decoder for SMPTE RDD 36 video: 4:2:2, progressive, to 10-bit frames (<see cref="PixelFormat.Yuv422P10"/>). Every
/// frame stands alone (intra-only), so each packet gives one frame.
/// </summary>
/// <remarks>
/// Implemented from SMPTE RDD 36:2022 (https://pub.smpte.org/doc/rdd36/). RDD 36 qualifies an inverse transform's
/// accuracy rather than fixing its arithmetic; this decoder's follows FFmpeg's, found by measuring its output, so
/// that decoded frames are bit-exact with it. Interlaced frames, 4:4:4 sampling and alpha are refused with
/// <see cref="NotSupportedException"/>.
/// </remarks>
public sealed class Rdd36Decoder : IVideoDecoder
{
    // slice_header() without coded_size_of_cr_data, which only a frame with alpha has.
    private const int MinimumSliceHeaderSize = 6;

    private const int MinimumPictureHeaderSize = 8;

    // The largest slice: 8 macroblocks of four luma blocks.
    private const int MaxBlocksInSlice = 32;

    private readonly VideoFramePool _framePool = new();

    // The container's colour properties and aspect ratio, for what the frame header leaves unspecified; null without one.
    private readonly VideoStreamInfo? _stream;

    // A slice's blocks of one component, cleared before each, and an edge block's samples before the part of them
    // inside the picture is copied out.
    private readonly short[] _blocks = new short[MaxBlocksInSlice * 64];
    private readonly ushort[] _edgeBlock = new ushort[64];
    private readonly int[] _lumaScale = new int[64];
    private readonly int[] _chromaScale = new int[64];

    /// <summary>A decoder that knows nothing of the container: colour properties a frame's header leaves unspecified stay so.</summary>
    public Rdd36Decoder()
    {
    }

    /// <summary>
    /// A decoder for a container's stream: its colour properties are the frames' where a frame's header leaves them
    /// unspecified, and its sample aspect ratio where it has one.
    /// </summary>
    public Rdd36Decoder(VideoStreamInfo stream) => _stream = stream;

    /// <summary>
    /// Decodes one packet, a whole frame, so exactly one frame comes out; none for a <see cref="Packet.Discard"/>
    /// packet (which, every frame standing alone, is not decoded at all).
    /// </summary>
    /// <exception cref="InvalidDataException">The packet is not an RDD 36 frame, or is damaged.</exception>
    /// <exception cref="NotSupportedException">An interlaced frame, 4:4:4 sampling, alpha, or a bitstream version after 1.</exception>
    public IReadOnlyList<VideoFrame> Decode(Packet packet)
    {
        if (packet.Discard)
        {
            return [];
        }

        var frame = Decode(packet.Data.Span);
        frame.Pts = packet.Pts;
        return [frame];
    }

    /// <summary>The decoder holds no frames back.</summary>
    public IReadOnlyList<VideoFrame> Flush() => [];

    /// <summary>The decoder works on the calling thread and never waits: <see cref="Decode(Packet)"/>, as a completed task.</summary>
    public ValueTask<IReadOnlyList<VideoFrame>> DecodeAsync(Packet packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(Decode(packet));
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<VideoFrame>> FlushAsync(CancellationToken cancellationToken = default) => new(Flush());

    /// <summary>
    /// Decodes one frame: frame_size, the 'icpf' identifier, the frame header and one picture. Bytes after frame_size
    /// are ignored. Disposing the frame when done with it gives its planes back for a later frame.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not an RDD 36 frame, or is damaged.</exception>
    /// <exception cref="NotSupportedException">An interlaced frame, 4:4:4 sampling, alpha, or a bitstream version after 1.</exception>
    public VideoFrame Decode(ReadOnlySpan<byte> packet)
    {
        var header = Rdd36FrameHeader.Parse(packet);
        if (header.InterlaceMode != 0)
        {
            throw new NotSupportedException("Interlaced RDD 36 frames are not supported.");
        }

        if (header.ChromaFormat != 2)
        {
            throw new NotSupportedException("RDD 36 frames with 4:4:4 sampling are not supported.");
        }

        if (header.AlphaChannelType != 0)
        {
            throw new NotSupportedException("RDD 36 frames with an alpha channel are not supported.");
        }

        var picture = packet[header.PictureOffset..header.FrameSize];
        var layout = PictureLayout.Read(picture, header.Width, header.Height);

        var frame = _framePool.Rent(header.Width, header.Height, PixelFormat.Yuv422P10);
        try
        {
            DecodeSlices(picture, layout, header, frame);
        }
        catch
        {
            frame.Dispose();
            throw;
        }

        frame.PictureType = PictureType.I;
        SetColor(frame, header);
        return frame;
    }

    /// <summary>
    /// qScale by quantization_index (Table 15): the index up to 128, then 128 + 4 * (index - 128). The index's
    /// permissible values are 1 to 224; the reserved ones are taken as FFmpeg takes them (measured): 0 as 1, those
    /// above 224 as 224.
    /// </summary>
    internal static int QuantizationScale(int quantizationIndex)
    {
        int index = Math.Clamp(quantizationIndex, 1, 224);
        return index <= 128 ? index : 128 + (4 * (index - 128));
    }

    /// <summary>
    /// slice_size_in_mb (6.2): slices of the desired size from the left of a macroblock row while they fit, then of
    /// half that size, and so on to the row's end. Returns the count of slices.
    /// </summary>
    internal static int SliceSizes(int widthInMacroblocks, int log2DesiredSliceSize, Span<int> sizes)
    {
        int count = 0;
        int sliceSize = 1 << log2DesiredSliceSize;
        int remaining = widthInMacroblocks;
        do
        {
            while (remaining >= sliceSize)
            {
                if (count < sizes.Length)
                {
                    sizes[count] = sliceSize;
                }

                count++;
                remaining -= sliceSize;
            }

            sliceSize /= 2;
        }
        while (remaining > 0);
        return count;
    }

    /// <summary>
    /// The frame's colour properties and aspect ratio. A frame header's colour codes are those of ITU-T H.273 (the
    /// notes of 6.1.1); where a code is 0 or 2, both unknown/unspecified in RDD 36, the container's value stands, if
    /// the decoder has a stream. The range is limited (7.5.1: black at 64 and nominal peak at 940 of 10 bits). The
    /// aspect ratio is the container's where it has one (as ffprobe shows frames), else the header's
    /// aspect_ratio_information: square pixels, or the pixel shape that makes the picture 4:3 or 16:9.
    /// </summary>
    private void SetColor(VideoFrame frame, Rdd36FrameHeader header)
    {
        frame.ColorRange = ColorRange.Limited;
        frame.ColorPrimaries = Specified(header.ColorPrimaries) ? ColorCodes.PrimariesOf(header.ColorPrimaries) : _stream?.ColorPrimaries ?? ColorPrimaries.Unspecified;
        frame.ColorTransfer = Specified(header.TransferCharacteristic) ? ColorCodes.TransferOf(header.TransferCharacteristic) : _stream?.ColorTransfer ?? ColorTransfer.Unspecified;
        frame.ColorMatrix = Specified(header.MatrixCoefficients) ? ColorCodes.MatrixOf(header.MatrixCoefficients) : _stream?.ColorMatrix ?? ColorMatrix.Unspecified;
        frame.SampleAspectRatio = _stream is { SampleAspectRatio.Numerator: > 0 }
            ? _stream.SampleAspectRatio
            : header.AspectRatioInformation switch
            {
                1 => new Rational(1, 1),
                2 => ColorCodes.AspectRatio(4L * header.Height, 3L * header.Width),
                3 => ColorCodes.AspectRatio(16L * header.Height, 9L * header.Width),
                _ => new Rational(0, 1),
            };
    }

    private static bool Specified(int code) => code is not (0 or 2);

    private void DecodeSlices(ReadOnlySpan<byte> picture, PictureLayout layout, Rdd36FrameHeader header, VideoFrame frame)
    {
        // A row of 4096 macroblocks (the widest a 16-bit horizontal_size gives) has 512 slices of 8 or 4096 of one.
        Span<int> sliceSizes = stackalloc int[layout.SlicesInRow];
        SliceSizes(layout.WidthInMacroblocks, layout.Log2DesiredSliceSize, sliceSizes);

        var luma = MemoryMarshal.Cast<byte, ushort>(frame.Planes[0].AsSpan());
        var cb = MemoryMarshal.Cast<byte, ushort>(frame.Planes[1].AsSpan());
        var cr = MemoryMarshal.Cast<byte, ushort>(frame.Planes[2].AsSpan());
        int chromaWidth = (header.Width + 1) / 2;

        int table = layout.SliceTableOffset;
        int at = layout.FirstSliceOffset;
        int lastScale = 0;
        for (int row = 0; row < layout.HeightInMacroblocks; row++)
        {
            int macroblock = 0;
            for (int j = 0; j < sliceSizes.Length; j++)
            {
                int codedSize = BinaryPrimitives.ReadUInt16BigEndian(picture[table..]);
                table += 2;
                var slice = picture.Slice(at, codedSize);
                at += codedSize;

                // slice_header(): its size (5 bits), reserved bits, quantization_index, and the sizes of the luma and
                // Cb data; the Cr data is the rest of the slice (5.3).
                int headerSize = slice.Length == 0 ? 0 : slice[0] >> 3;
                if (headerSize < MinimumSliceHeaderSize || headerSize > slice.Length)
                {
                    throw new InvalidDataException($"The slice at macroblock row {row} has a header of {headerSize} bytes in {slice.Length}.");
                }

                int scale = QuantizationScale(slice[1]);
                int lumaSize = BinaryPrimitives.ReadUInt16BigEndian(slice[2..]);
                int cbSize = BinaryPrimitives.ReadUInt16BigEndian(slice[4..]);
                if (headerSize + lumaSize + cbSize > slice.Length)
                {
                    throw new InvalidDataException($"The slice at macroblock row {row} is too short for its components' data.");
                }

                if (scale != lastScale)
                {
                    for (int i = 0; i < 64; i++)
                    {
                        _lumaScale[i] = header.LumaMatrix[i] * scale;
                        _chromaScale[i] = header.ChromaMatrix[i] * scale;
                    }

                    lastScale = scale;
                }

                int macroblocks = sliceSizes[j];
                var data = slice[headerSize..];
                DecodeComponent(data[..lumaSize], macroblocks * 4, _lumaScale);
                PutLuma(luma, frame.Strides[0] / 2, header.Width, header.Height, macroblock * 16, row * 16, macroblocks);
                DecodeComponent(data.Slice(lumaSize, cbSize), macroblocks * 2, _chromaScale);
                PutChroma(cb, frame.Strides[1] / 2, chromaWidth, header.Height, macroblock * 8, row * 16, macroblocks);
                DecodeComponent(data[(lumaSize + cbSize)..], macroblocks * 2, _chromaScale);
                PutChroma(cr, frame.Strides[2] / 2, chromaWidth, header.Height, macroblock * 8, row * 16, macroblocks);
                macroblock += macroblocks;
            }
        }
    }

    /// <summary>
    /// scanned_coefficients() (5.3.2, 7.1.1) of one component of a slice into <c>_blocks</c>, each coefficient at its
    /// place in its block (inverse slice and block scanning, 7.2) and times its quantization weight and the slice's
    /// scale (7.3, without the division by 8, which the inverse transform's scaling has).
    /// </summary>
    private void DecodeComponent(ReadOnlySpan<byte> data, int blockCount, int[] scale)
    {
        var blocks = _blocks.AsSpan(0, blockCount * 64);
        blocks.Clear();
        var reader = new Rdd36BitReader(data);

        // The DC coefficients: the first by an exponential-Golomb code of order 5, the others as differences, each by
        // the codebook the previous difference's size picks, and negated where the previous difference was negative.
        int dcScale = scale[0];
        int dc = Rdd36BitReader.ToSigned(reader.ReadCode(Rdd36Codebook.ExpGolomb(5)));
        blocks[0] = Dequantize(dc, dcScale);
        int previousDifference = 3;
        var dcCodebooks = Rdd36Tables.DcDifference;
        for (int block = 1; block < blockCount; block++)
        {
            int difference = Rdd36BitReader.ToSigned(reader.ReadCode(dcCodebooks[Math.Min(Math.Abs(previousDifference), 3)]));
            if (previousDifference < 0)
            {
                difference = -difference;
            }

            dc = unchecked(dc + difference);
            blocks[block * 64] = Dequantize(dc, dcScale);
            previousDifference = difference;
        }

        // The AC coefficients: runs of zeros and levels, through all blocks' coefficients of one frequency before the
        // next frequency's. The scanned array's index is blockCount * frequency + block, blockCount a power of two.
        int log2Blocks = System.Numerics.BitOperations.Log2((uint)blockCount);
        int blockMask = blockCount - 1;
        int coefficients = blockCount * 64;
        int position = blockCount;
        int previousRun = 4;
        int previousLevel = 1;
        var runCodebooks = Rdd36Tables.Run;
        var levelCodebooks = Rdd36Tables.Level;
        var scan = Rdd36Tables.ProgressiveScan;
        while (!reader.EndOfData)
        {
            int run = reader.ReadCode(runCodebooks[Math.Min(previousRun, 15)]);
            int levelSymbol = reader.ReadCode(levelCodebooks[Math.Min(previousLevel, 8)]);
            int sign = reader.ReadBit();
            position += run;
            if ((uint)position >= (uint)coefficients)
            {
                throw new InvalidDataException("A run of zero coefficients goes past the slice's last coefficient.");
            }

            int place = scan[position >> log2Blocks];
            int level = levelSymbol + 1;
            blocks[((position & blockMask) * 64) + place] = Dequantize(sign != 0 ? -level : level, scale[place]);
            position++;
            previousRun = run;
            previousLevel = levelSymbol;
        }
    }

    // A coefficient as the inverse transform takes it: 16 bits, wrapping beyond (no picture's coefficients do).
    private static short Dequantize(int level, int scale) => unchecked((short)(level * scale));

    /// <summary>The luma blocks of a slice's macroblocks, four each (Figure 6: 0 1 over 2 3), into the plane.</summary>
    private void PutLuma(Span<ushort> plane, int stride, int width, int height, int x, int y, int macroblocks)
    {
        for (int block = 0; block < macroblocks * 4; block++)
        {
            PutBlock(plane, stride, width, height, x + ((block >> 2) * 16) + ((block & 1) * 8), y + ((block & 2) * 4), block);
        }
    }

    /// <summary>The blocks of one chroma component of a slice's macroblocks, two each (Figure 7: 0 over 1), into the plane.</summary>
    private void PutChroma(Span<ushort> plane, int stride, int width, int height, int x, int y, int macroblocks)
    {
        for (int block = 0; block < macroblocks * 2; block++)
        {
            PutBlock(plane, stride, width, height, x + ((block >> 1) * 8), y + ((block & 1) * 8), block);
        }
    }

    /// <summary>
    /// One block's samples at (<paramref name="x"/>, <paramref name="y"/>) of a plane <paramref name="width"/> by
    /// <paramref name="height"/>: the samples past the picture's right and bottom edges, which the encoder appended to
    /// fill the macroblocks, are discarded (7.5.3).
    /// </summary>
    private void PutBlock(Span<ushort> plane, int stride, int width, int height, int x, int y, int block)
    {
        if (x >= width || y >= height)
        {
            return;
        }

        var coefficients = _blocks.AsSpan(block * 64, 64);
        if (x + 8 <= width && y + 8 <= height)
        {
            Rdd36Idct.Put(coefficients, plane[((y * stride) + x)..], stride);
            return;
        }

        Rdd36Idct.Put(coefficients, _edgeBlock, 8);
        int columns = Math.Min(8, width - x);
        int rows = Math.Min(8, height - y);
        for (int row = 0; row < rows; row++)
        {
            _edgeBlock.AsSpan(row * 8, columns).CopyTo(plane[(((y + row) * stride) + x)..]);
        }
    }

    /// <summary>The picture_header() and slice_table() of a frame's one picture (5.2), checked against the data there is.</summary>
    private readonly struct PictureLayout
    {
        private PictureLayout(int widthInMacroblocks, int heightInMacroblocks, int log2DesiredSliceSize, int slicesInRow, int sliceTableOffset, int firstSliceOffset)
        {
            SlicesInRow = slicesInRow;
            WidthInMacroblocks = widthInMacroblocks;
            HeightInMacroblocks = heightInMacroblocks;
            Log2DesiredSliceSize = log2DesiredSliceSize;
            SliceTableOffset = sliceTableOffset;
            FirstSliceOffset = firstSliceOffset;
        }

        public int WidthInMacroblocks { get; }

        public int HeightInMacroblocks { get; }

        public int Log2DesiredSliceSize { get; }

        /// <summary>number_of_slices_per_mb_row (6.2).</summary>
        public int SlicesInRow { get; }

        public int SliceTableOffset { get; }

        public int FirstSliceOffset { get; }

        /// <summary>
        /// Reads the picture header and checks that the slice table and every slice it lists lie inside the picture,
        /// before anything is allocated for a picture of the header's size: a few bytes cannot ask for a large frame.
        /// </summary>
        public static PictureLayout Read(ReadOnlySpan<byte> picture, int width, int height)
        {
            if (picture.Length < MinimumPictureHeaderSize)
            {
                throw new InvalidDataException("The frame is too short for a picture header.");
            }

            int headerSize = picture[0] >> 3;
            uint pictureSize = BinaryPrimitives.ReadUInt32BigEndian(picture[1..]);
            if (headerSize < MinimumPictureHeaderSize || pictureSize > (uint)picture.Length || headerSize > pictureSize)
            {
                throw new InvalidDataException($"The picture header (picture_header_size {headerSize}, picture_size {pictureSize}) does not fit the frame's {picture.Length} bytes.");
            }

            int log2DesiredSliceSize = (picture[7] >> 4) & 3;
            int widthInMacroblocks = (width + 15) / 16;
            int heightInMacroblocks = (height + 15) / 16;
            int slicesInRow = SliceSizes(widthInMacroblocks, log2DesiredSliceSize, []);
            long slices = (long)slicesInRow * heightInMacroblocks;
            long tableEnd = headerSize + (2 * slices);
            if (tableEnd > pictureSize)
            {
                throw new InvalidDataException($"The picture's {pictureSize} bytes are too few for the slice table of a {width}x{height} picture.");
            }

            long end = tableEnd;
            for (int i = 0; i < slices; i++)
            {
                end += BinaryPrimitives.ReadUInt16BigEndian(picture[(headerSize + (2 * i))..]);
            }

            if (end > pictureSize)
            {
                throw new InvalidDataException("The slice table's sizes add up to more than the picture's.");
            }

            return new PictureLayout(widthInMacroblocks, heightInMacroblocks, log2DesiredSliceSize, slicesInRow, headerSize, (int)tableEnd);
        }
    }
}
