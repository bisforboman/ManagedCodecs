using System.Buffers.Binary;

namespace ManagedCodecs.Rdd36;

/// <summary>The frame() container and frame_header() of SMPTE RDD 36 (5.1, 5.1.1, 6.1.1).</summary>
internal sealed class Rdd36FrameHeader
{
    /// <summary>frame_identifier: 'icpf'.</summary>
    public const uint FrameIdentifier = 0x69637066;

    /// <summary>The frame header without quantization matrices, the 8 bytes of frame_size and frame_identifier not counted.</summary>
    public const int MinimumHeaderSize = 20;

    private Rdd36FrameHeader(byte[] lumaMatrix, byte[] chromaMatrix)
    {
        LumaMatrix = lumaMatrix;
        ChromaMatrix = chromaMatrix;
    }

    /// <summary>frame_size, limited to the packet: where the frame's data ends, stuffing included.</summary>
    public int FrameSize { get; private init; }

    /// <summary>Where the first picture starts in the packet: after frame_size, frame_identifier and frame_header_size bytes.</summary>
    public int PictureOffset { get; private init; }

    public int BitstreamVersion { get; private init; }

    /// <summary>horizontal_size: the frame's width in luma samples.</summary>
    public int Width { get; private init; }

    /// <summary>vertical_size: the frame's height in luma samples.</summary>
    public int Height { get; private init; }

    /// <summary>chroma_format: 2 for 4:2:2, 3 for 4:4:4 (Table 1).</summary>
    public int ChromaFormat { get; private init; }

    /// <summary>interlace_mode: 0 progressive, 1 and 2 interlaced (Table 2).</summary>
    public int InterlaceMode { get; private init; }

    /// <summary>aspect_ratio_information (Table 3).</summary>
    public int AspectRatioInformation { get; private init; }

    /// <summary>frame_rate_code (Table 4).</summary>
    public int FrameRateCode { get; private init; }

    public int ColorPrimaries { get; private init; }

    public int TransferCharacteristic { get; private init; }

    public int MatrixCoefficients { get; private init; }

    /// <summary>alpha_channel_type: 0 for none (Table 7).</summary>
    public int AlphaChannelType { get; private init; }

    /// <summary>The luma quantization weights W[v][u] at 8 * v + u: the header's, or 4 everywhere (7.3).</summary>
    public byte[] LumaMatrix { get; }

    /// <summary>The chroma quantization weights: the header's, or the luma matrix (7.3).</summary>
    public byte[] ChromaMatrix { get; }

    /// <summary>
    /// Reads a frame's frame_size, frame_identifier and frame header. Reserved bits are ignored, as 6.1.1 requires, and
    /// the picture starts where frame_header_size says, whatever the header's known elements take.
    /// </summary>
    /// <exception cref="InvalidDataException">The packet is not an RDD 36 frame, or its header is cut short or contradicts itself.</exception>
    /// <exception cref="NotSupportedException">A bitstream version after 1, which a decoder shall refuse (6.4).</exception>
    public static Rdd36FrameHeader Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8 + MinimumHeaderSize)
        {
            throw new InvalidDataException("The packet is too short for an RDD 36 frame header.");
        }

        uint frameSize = BinaryPrimitives.ReadUInt32BigEndian(packet);
        if (BinaryPrimitives.ReadUInt32BigEndian(packet[4..]) != FrameIdentifier)
        {
            throw new InvalidDataException("The packet does not start an RDD 36 frame (no 'icpf' frame identifier).");
        }

        if (frameSize > (uint)packet.Length)
        {
            throw new InvalidDataException($"The frame is cut short: frame_size is {frameSize}, the packet has {packet.Length} bytes.");
        }

        var header = packet[8..(int)frameSize];
        int headerSize = header.Length < 2 ? 0 : BinaryPrimitives.ReadUInt16BigEndian(header);
        if (headerSize < MinimumHeaderSize || headerSize > header.Length)
        {
            throw new InvalidDataException($"frame_header_size {headerSize} is not that of a frame header in a frame of {frameSize} bytes.");
        }

        int version = header[3];
        if (version > 1)
        {
            throw new NotSupportedException($"RDD 36 bitstream version {version} is not supported (0 and 1 are).");
        }

        int flags = header[19];
        bool loadLuma = (flags & 2) != 0;
        bool loadChroma = (flags & 1) != 0;
        if (MinimumHeaderSize + (loadLuma ? 64 : 0) + (loadChroma ? 64 : 0) > headerSize)
        {
            throw new InvalidDataException("frame_header_size leaves no room for the quantization matrices the header announces.");
        }

        byte[] luma = new byte[64];
        int at = MinimumHeaderSize;
        if (loadLuma)
        {
            header.Slice(at, 64).CopyTo(luma);
            at += 64;
        }
        else
        {
            luma.AsSpan().Fill(4);
        }

        byte[] chroma = luma;
        if (loadChroma)
        {
            chroma = header.Slice(at, 64).ToArray();
        }

        var parsed = new Rdd36FrameHeader(luma, chroma)
        {
            FrameSize = (int)frameSize,
            PictureOffset = 8 + headerSize,
            BitstreamVersion = version,
            Width = BinaryPrimitives.ReadUInt16BigEndian(header[8..]),
            Height = BinaryPrimitives.ReadUInt16BigEndian(header[10..]),
            ChromaFormat = header[12] >> 6,
            InterlaceMode = (header[12] >> 2) & 3,
            AspectRatioInformation = header[13] >> 4,
            FrameRateCode = header[13] & 15,
            ColorPrimaries = header[14],
            TransferCharacteristic = header[15],
            MatrixCoefficients = header[16],
            AlphaChannelType = header[17] & 15,
        };
        if (parsed.Width == 0 || parsed.Height == 0)
        {
            throw new InvalidDataException("The frame header gives a picture without width or height.");
        }

        if (parsed.ChromaFormat < 2)
        {
            throw new InvalidDataException($"chroma_format {parsed.ChromaFormat} is reserved.");
        }

        if (parsed.InterlaceMode == 3)
        {
            throw new InvalidDataException("interlace_mode 3 is reserved.");
        }

        if (parsed.AlphaChannelType > 2)
        {
            throw new InvalidDataException($"alpha_channel_type {parsed.AlphaChannelType} is reserved.");
        }

        if (version == 0 && (parsed.ChromaFormat != 2 || parsed.AlphaChannelType != 0))
        {
            throw new InvalidDataException("A version 0 bitstream is 4:2:2 without alpha (6.4); this one says otherwise.");
        }

        return parsed;
    }
}
