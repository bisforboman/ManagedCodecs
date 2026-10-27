namespace ManagedCodecs.Mjpeg;

/// <summary>Baseline (sequential, Huffman) JPEG decoder for MJPEG frames.</summary>
/// <remarks>
/// Implemented from the JPEG spec (ITU-T T.81) rather than by copying FFmpeg's mjpegdec.c.
/// Only the inverse DCT follows FFmpeg's arithmetic, so output is bit-exact with it.
/// Quantization and Huffman tables persist from frame to frame, as many MJPEG streams send
/// them once or not at all; until a DHT arrives the standard tables of T.81 Annex K.3 apply.
/// Interlaced MJPEG (AVI1, as capture cards write it) holds a frame's two fields as two JPEG
/// images in one packet; constructed with the stream's <see cref="VideoStreamInfo"/>, the decoder
/// recognizes them as FFmpeg does, by an image less than 3/4 of the stream's height, and weaves
/// them into one frame of twice the image's height.
/// </remarks>
public sealed class MjpegDecoder : IVideoDecoder
{
    // The DC predictor works on dequantized values. Starting it at 128 * 8 applies the
    // level shift of T.81 A.3.1 inside the IDCT, which is what keeps us bit-exact with FFmpeg.
    private const int DcPredictorReset = 128 * 8;

    private readonly ushort[]?[] _quantTables = new ushort[4][];
    private readonly HuffmanTable?[] _dcTables = new HuffmanTable[4];
    private readonly HuffmanTable?[] _acTables = new HuffmanTable[4];

    // Sample buffers of the previous frame, reused while the layout stays the same.
    private FrameState? _previous;

    // The planes of output frames, back from frames the caller disposed.
    private readonly VideoFramePool _framePool = new();

    // The stream's height, from the container; 0 when unknown (no field detection).
    private readonly int _streamHeight;

    /// <summary>A decoder that knows nothing of the stream's height, so it takes every image as a whole frame, never as a field.</summary>
    public MjpegDecoder()
    {
        _dcTables[0] = new HuffmanTable(JpegTables.DcLuminanceBits, JpegTables.DcLuminanceValues);
        _dcTables[1] = new HuffmanTable(JpegTables.DcChrominanceBits, JpegTables.DcChrominanceValues);
        _acTables[0] = new HuffmanTable(JpegTables.AcLuminanceBits, JpegTables.AcLuminanceValues);
        _acTables[1] = new HuffmanTable(JpegTables.AcChrominanceBits, JpegTables.AcChrominanceValues);
    }

    /// <summary>
    /// A decoder that knows the stream's height, so that it recognizes interlaced packets: two
    /// JPEG images of less than 3/4 of that height each, the fields of one frame.
    /// </summary>
    public MjpegDecoder(VideoStreamInfo stream)
        : this()
    {
        _streamHeight = stream.Height;
    }

    /// <summary>
    /// Put the first field of an interlaced packet on the frame's top (even) rows. By default it
    /// goes on the bottom (odd) rows, as FFmpeg does when it knows no field order, which AVI does
    /// not tell (FFmpeg's <c>-field_order tt</c> gives this option's order). The AVI1 segment's
    /// field byte is ignored, as by FFmpeg.
    /// </summary>
    public bool FirstFieldOnTop { get; init; }

    /// <summary>
    /// Decode each image straight into the planes of its output frame, saving the copy out of the decoder's buffers.
    /// The frame's planes are then a whole number of MCUs wide and high: read its rows through
    /// <see cref="VideoFrame.GetRow"/>, or with its <see cref="VideoFrame.Strides"/>. The frame owns them as any frame
    /// does (unlike H.264's borrowed frames, it stays valid). Interlaced packets are woven into a frame all the same.
    /// </summary>
    /// <remarks>
    /// Either way, disposing a frame when done with it gives its planes back for a later frame, which saves allocating
    /// them.
    /// </remarks>
    public bool ZeroCopy { get; init; }

    /// <summary>
    /// Decodes one MJPEG packet; every packet is a complete frame, so exactly one comes out, none for a
    /// <see cref="Packet.Discard"/> packet (which, every image standing alone, is not decoded at all).
    /// </summary>
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

    /// <summary>MJPEG holds no frames back.</summary>
    public IReadOnlyList<VideoFrame> Flush() => [];

    /// <summary>MJPEG decodes on the calling thread and never waits: <see cref="Decode(Packet)"/>, as a completed task.</summary>
    public ValueTask<IReadOnlyList<VideoFrame>> DecodeAsync(Packet packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(Decode(packet));
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<VideoFrame>> FlushAsync(CancellationToken cancellationToken = default) => new(Flush());

    /// <summary>
    /// Decodes one MJPEG packet's data: one JPEG image, or with the stream's height known, the two
    /// fields of an interlaced frame woven together. Data after the image (or the second field) is
    /// ignored.
    /// </summary>
    public VideoFrame Decode(ReadOnlySpan<byte> packet)
    {
        var first = DecodeImage(packet, out int end);
        int height = first.Header.Height;
        if (!IsField(height))
        {
            return first.Rented ? new VideoFrame(first.Header.Width, height, first.Format, first.Buffers, first.BufferStrides, _framePool) : first.ToVideoFrame(_framePool);
        }

        // A field: weave it into a frame of twice its height (each field writes every other row of it), then decode the
        // second field (into the same buffers, so the first is copied out before).
        var frame = _framePool.Rent(first.Header.Width, 2 * height, first.Format);
        for (int plane = 0; plane < frame.Planes.Length; plane++)
        {
            if (first.Format.PlaneSize(plane, frame.Width, frame.Height).Height != 2 * first.Format.PlaneSize(plane, frame.Width, height).Height)
            {
                throw new NotSupportedException("Interlaced fields of an odd height with vertically subsampled chroma are not supported.");
            }
        }

        first.WriteField(frame, FirstFieldOnTop ? 0 : 1);
        int second = packet[end..].IndexOf(SecondImageStart);
        if (second < 0)
        {
            throw new NotSupportedException("An interlaced packet with one field (the second field is not in the same packet) is not supported.");
        }

        var other = DecodeImage(packet[(end + second)..], out _);
        if (other.Header.Width != first.Header.Width || other.Header.Height != height || other.Format != first.Format)
        {
            throw new InvalidDataException("The two fields of an interlaced packet differ in size or sampling.");
        }

        other.WriteField(frame, FirstFieldOnTop ? 1 : 0);
        return frame;
    }

    private static ReadOnlySpan<byte> SecondImageStart => [0xFF, JpegMarker.Soi];

    // An image of less than 3/4 of the stream's height is a field, as FFmpeg tells them.
    private bool IsField(int height) => _streamHeight != 0 && height < _streamHeight * 3 / 4;

    /// <summary>Decodes one JPEG image into the sample buffers; <paramref name="end"/> is the offset just past its EOI.</summary>
    private FrameState DecodeImage(ReadOnlySpan<byte> packet, out int end)
    {
        var segments = JpegMarkerReader.ReadSegments(packet);
        FrameState? frame = null;
        int restartInterval = 0;
        bool sawScan = false;

        foreach (var segment in segments)
        {
            var payload = packet.Slice(segment.PayloadOffset, segment.PayloadLength);
            switch (segment.Marker)
            {
                case JpegMarker.Dqt:
                    ParseQuantizationTables(payload);
                    break;
                case JpegMarker.Dht:
                    ParseHuffmanTables(payload);
                    break;
                case JpegMarker.Dri:
                    if (payload.Length < 2)
                    {
                        throw new InvalidDataException("DRI segment too short.");
                    }

                    restartInterval = (payload[0] << 8) | payload[1];
                    break;
                case JpegMarker.Sof0 or JpegMarker.Sof1:
                    if (frame is not null)
                    {
                        throw new InvalidDataException("More than one SOF segment.");
                    }

                    {
                        // A frame decoded in place takes planes of its own from the pool; else the decoder's buffers.
                        var header = JpegFrameHeader.Parse(segment.Marker, payload);
                        frame = new FrameState(header, _previous, ZeroCopy && !IsField(header.Height) ? _framePool : null);
                        _previous = frame.Rented ? null : frame;
                    }

                    break;
                case JpegMarker.Sos:
                    if (frame is null)
                    {
                        throw new InvalidDataException("SOS before SOF.");
                    }

                    DecodeScan(frame, payload, packet.Slice(segment.EntropyDataOffset, segment.EntropyDataLength), restartInterval);
                    sawScan = true;
                    break;
                default:
                    if (JpegMarker.IsUnsupportedFrame(segment.Marker))
                    {
                        throw new NotSupportedException($"JPEG frame type 0xFF{segment.Marker:X2} is not supported (only baseline/extended Huffman).");
                    }

                    break;
            }
        }

        if (frame is null)
        {
            throw new InvalidDataException("No SOF segment found.");
        }

        if (!sawScan)
        {
            throw new InvalidDataException("No SOS segment found.");
        }

        end = segments[^1].PayloadOffset; // just past the EOI marker
        return frame;
    }

    private void ParseQuantizationTables(ReadOnlySpan<byte> payload)
    {
        while (payload.Length > 0)
        {
            int precision = payload[0] >> 4;
            int id = payload[0] & 0x0F;
            int size = precision == 0 ? 64 : 128;
            if (precision > 1 || id > 3 || payload.Length < 1 + size)
            {
                throw new InvalidDataException("Invalid DQT segment.");
            }

            var table = new ushort[64];
            for (int i = 0; i < 64; i++)
            {
                table[i] = precision == 0
                    ? payload[1 + i]
                    : (ushort)((payload[1 + (2 * i)] << 8) | payload[2 + (2 * i)]);
            }

            _quantTables[id] = table;
            payload = payload[(1 + size)..];
        }
    }

    private void ParseHuffmanTables(ReadOnlySpan<byte> payload)
    {
        while (payload.Length > 0)
        {
            int tableClass = payload[0] >> 4;
            int id = payload[0] & 0x0F;
            if (tableClass > 1 || id > 3)
            {
                throw new InvalidDataException("Invalid DHT segment.");
            }

            var table = HuffmanTable.Parse(payload, out int consumed);
            (tableClass == 0 ? _dcTables : _acTables)[id] = table;
            payload = payload[consumed..];
        }
    }

    private void DecodeScan(FrameState frame, ReadOnlySpan<byte> header, ReadOnlySpan<byte> data, int restartInterval)
    {
        int count = header.Length > 0 ? header[0] : 0;
        if (count is < 1 or > 4 || header.Length < 4 + (2 * count))
        {
            throw new InvalidDataException("Invalid SOS segment.");
        }

        var scan = new ScanComponent[count];
        for (int i = 0; i < count; i++)
        {
            byte id = header[1 + (2 * i)];
            int index = frame.IndexOf(id);
            int dc = header[2 + (2 * i)] >> 4;
            int ac = header[2 + (2 * i)] & 0x0F;
            var component = frame.Header.Components[index];
            scan[i] = new ScanComponent(
                index,
                (dc < 4 ? _dcTables[dc] : null) ?? throw new InvalidDataException($"Missing DC Huffman table {dc}."),
                (ac < 4 ? _acTables[ac] : null) ?? throw new InvalidDataException($"Missing AC Huffman table {ac}."),
                (component.QuantizationTableId < 4 ? _quantTables[component.QuantizationTableId] : null)
                    ?? throw new InvalidDataException($"Missing quantization table {component.QuantizationTableId}."));
        }

        int p = 1 + (2 * count);
        int spectralStart = header[p];
        int spectralEnd = header[p + 1];
        int approximation = header[p + 2];
        if (spectralStart != 0 || spectralEnd != 63 || approximation != 0)
        {
            throw new InvalidDataException("Scan parameters are not valid for a sequential JPEG.");
        }

        // A scan with one component is non-interleaved: its MCU is a single block (T.81 A.2.2).
        int mcusX;
        int mcusY;
        if (count == 1)
        {
            (mcusX, mcusY) = frame.BlocksOf(scan[0].FrameIndex);
        }
        else
        {
            mcusX = frame.McusX;
            mcusY = frame.McusY;
        }

        var reader = new JpegBitReader(data);
        Span<int> predictors = stackalloc int[count];
        predictors.Fill(DcPredictorReset);
        Span<short> block = stackalloc short[64];

        int total = mcusX * mcusY;
        for (int mcu = 0; mcu < total; mcu++)
        {
            if (restartInterval > 0 && mcu > 0 && mcu % restartInterval == 0)
            {
                // A missing or out-of-order RSTn is tolerated: decoding just carries on.
                reader.ConsumeRestartMarker(out _);
                predictors.Fill(DcPredictorReset);
            }

            int mx = mcu % mcusX;
            int my = mcu / mcusX;

            for (int i = 0; i < count; i++)
            {
                ref readonly var sc = ref scan[i];
                var plane = frame.Buffers[sc.FrameIndex];
                int stride = frame.BufferStrides[sc.FrameIndex];

                if (count == 1)
                {
                    DecodeBlock(ref reader, in sc, ref predictors[i], block, plane.AsSpan((my * 8 * stride) + (mx * 8)), stride);
                    continue;
                }

                var component = frame.Header.Components[sc.FrameIndex];
                for (int v = 0; v < component.VerticalSampling; v++)
                {
                    for (int h = 0; h < component.HorizontalSampling; h++)
                    {
                        int x = ((mx * component.HorizontalSampling) + h) * 8;
                        int y = ((my * component.VerticalSampling) + v) * 8;
                        DecodeBlock(ref reader, in sc, ref predictors[i], block, plane.AsSpan((y * stride) + x), stride);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Huffman-decodes one block (T.81 F.2.2), dequantizes it into natural order and writes
    /// its inverse DCT to <paramref name="dest"/>.
    /// </summary>
    private static void DecodeBlock(ref JpegBitReader reader, in ScanComponent sc, ref int predictor, scoped Span<short> block, Span<byte> dest, int stride)
    {
        block.Clear();
        var quant = sc.Quantization;
        var zigZag = JpegTables.ZigZag;

        int dc = (sc.Dc.DecodeDc(ref reader) * quant[0]) + predictor;
        dc = Math.Clamp(dc, short.MinValue, short.MaxValue);
        predictor = dc;
        block[0] = (short)dc;

        bool hasAc = false;
        for (int k = 1; k < 64; k++)
        {
            int ac = sc.Ac.DecodeAc(ref reader);
            int run = ac & 0xFF;
            int value = ac >> 8;

            if (value == 0)
            {
                if (run == 15)
                {
                    k += 15; // ZRL: sixteen zeros
                    continue;
                }

                break; // EOB
            }

            k += run;
            if (k > 63)
            {
                throw new InvalidDataException("AC coefficient index out of range.");
            }

            block[zigZag[k]] = (short)(value * quant[k]);
            hasAc = true;
        }

        // Flat blocks are common and need no transform: every sample has the same value.
        if (hasAc)
        {
            SimpleIdct.Put(block, dest, stride);
        }
        else
        {
            SimpleIdct.PutDcOnly(block[0], dest, stride);
        }
    }

    private readonly record struct ScanComponent(int FrameIndex, HuffmanTable Dc, HuffmanTable Ac, ushort[] Quantization);

    /// <summary>
    /// The largest image decoded, in samples of a plane (16384 x 4096, or 8192 x 8192): JPEG allows 65535 x 65535,
    /// whose buffers (up to 12 GB) a stream can ask for in a few bytes. Larger images are not supported.
    /// </summary>
    private const long MaxSamples = 1 << 26;

    /// <summary>Per-frame decode target: one MCU-aligned sample buffer per component.</summary>
    private sealed class FrameState
    {
        /// <param name="pool">Where to rent the sample buffers when the frame is to be decoded in place, else null.</param>
        public FrameState(JpegFrameHeader header, FrameState? previous, VideoFramePool? pool = null)
        {
            Header = header;
            Format = header.ToPixelFormat();

            foreach (var c in header.Components)
            {
                if (c.HorizontalSampling is < 1 or > 4 || c.VerticalSampling is < 1 or > 4)
                {
                    throw new InvalidDataException("Invalid sampling factor.");
                }

                MaxH = Math.Max(MaxH, c.HorizontalSampling);
                MaxV = Math.Max(MaxV, c.VerticalSampling);
            }

            McusX = DivUp(header.Width, 8 * MaxH);
            McusY = DivUp(header.Height, 8 * MaxV);
            if ((long)McusX * MaxH * 8 * McusY * MaxV * 8 > MaxSamples)
            {
                throw new NotSupportedException($"Images over {MaxSamples} samples ({header.Width} x {header.Height}) are not supported.");
            }

            int count = header.Components.Count;
            BufferStrides = new int[count];
            var sizes = new int[count];
            for (int i = 0; i < count; i++)
            {
                var c = header.Components[i];
                BufferStrides[i] = McusX * c.HorizontalSampling * 8;
                sizes[i] = BufferStrides[i] * McusY * c.VerticalSampling * 8;
            }

            if (pool is not null)
            {
                Buffers = pool.RentPlanes(sizes);
                Rented = true;
                return;
            }

            Buffers = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                Buffers[i] = previous is not null && i < previous.Buffers.Length && previous.Buffers[i].Length == sizes[i] && previous.BufferStrides[i] == BufferStrides[i]
                    ? previous.Buffers[i]
                    : new byte[sizes[i]];
            }
        }

        /// <summary>Whether the buffers are rented for an output frame (decoding in place) rather than the decoder's own.</summary>
        public bool Rented { get; }

        public JpegFrameHeader Header { get; }

        public PixelFormat Format { get; }

        public int MaxH { get; }

        public int MaxV { get; }

        public int McusX { get; }

        public int McusY { get; }

        public byte[][] Buffers { get; }

        public int[] BufferStrides { get; }

        public int IndexOf(byte componentId)
        {
            var components = Header.Components;
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i].Id == componentId)
                {
                    return i;
                }
            }

            throw new InvalidDataException($"Scan refers to unknown component {componentId}.");
        }

        /// <summary>Block grid of one component on its own, used by non-interleaved scans.</summary>
        public (int X, int Y) BlocksOf(int index)
        {
            var c = Header.Components[index];
            int width = DivUp(Header.Width * c.HorizontalSampling, MaxH);
            int height = DivUp(Header.Height * c.VerticalSampling, MaxV);
            return (DivUp(width, 8), DivUp(height, 8));
        }

        public VideoFrame ToVideoFrame(VideoFramePool pool)
        {
            var frame = pool.Rent(Header.Width, Header.Height, Format);
            for (int plane = 0; plane < Buffers.Length; plane++)
            {
                var (width, height) = Format.PlaneSize(plane, Header.Width, Header.Height);
                int stride = BufferStrides[plane];
                for (int y = 0; y < height; y++)
                {
                    Buffers[plane].AsSpan(y * stride, width).CopyTo(frame.GetRow(plane, y));
                }
            }

            return frame;
        }

        /// <summary>Copies the image into every other row of <paramref name="frame"/>, from row <paramref name="parity"/>.</summary>
        public void WriteField(VideoFrame frame, int parity)
        {
            for (int plane = 0; plane < Buffers.Length; plane++)
            {
                var (width, height) = Format.PlaneSize(plane, Header.Width, Header.Height);
                int stride = BufferStrides[plane];
                for (int y = 0; y < height; y++)
                {
                    Buffers[plane].AsSpan(y * stride, width).CopyTo(frame.GetRow(plane, (2 * y) + parity));
                }
            }
        }

        private static int DivUp(int value, int divisor) => (value + divisor - 1) / divisor;
    }
}
