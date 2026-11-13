namespace ManagedCodecs.Playback;

/// <summary>
/// Turns RTP packets carrying JPEG (RFC 2435) back into whole JPEGs for <c>MjpegDecoder</c>. The packets carry only the
/// scan and an 8-byte header (type, Q, size in 8-pixel units, the fragment's offset in the scan), so the JPEG's headers
/// are rebuilt: DQT (sent in-band for Q 128 to 255, computed from T.81 K.1's tables for Q 1 to 99, RFC 2435 Appendix
/// A), T.81 K.3's Huffman tables, SOF0 for types 0 (4:2:2) and 1 (4:2:0), DRI for types 64 and 65, and SOS. A frame
/// ends at the packet with the marker bit, or, if that was lost, at the first packet with another timestamp. A frame
/// whose fragments don't join up from offset 0 is incomplete and given as <see cref="RtpJpegFrame.Damaged"/>, without
/// a JPEG: it is dropped (docs/decisions/2026-11-12-live-rtsp-jpeg.md, decision 1).
/// </summary>
internal sealed class RtpJpegDepacketizer
{
    // More than any JPEG a camera sends; past it the frame is dropped as damaged rather than grown without bound.
    private const int MaxFrame = 32 * 1024 * 1024;

    private readonly int _payloadType;
    private readonly MemoryStream _scan = new();
    private readonly Dictionary<int, byte[]> _tables = [];
    private bool _inFrame;
    private uint _timestamp;
    private bool _damaged;
    private int _type;
    private int _width;
    private int _height;
    private int _restartInterval;
    private byte[]? _quantization;
    private int _expected = -1;

    /// <param name="payloadType">The SDP's payload type for the track (26, RFC 3551's static one, unless mapped); -1 for any.</param>
    public RtpJpegDepacketizer(int payloadType = 26)
    {
        _payloadType = payloadType;
    }

    /// <summary>Packets the sequence numbers say never arrived.</summary>
    public long LostPackets { get; private set; }

    /// <summary>
    /// Adds an RTP packet (header included), appending to <paramref name="completed"/> the frames it completes: the one
    /// before, if this packet starts another timestamp before that one's marker came, and its own, at the marker bit.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// A frame that can't be rebuilt: a reserved or dynamic type (other than 0, 1, 64 and 65), a reserved Q (0, 100 to
    /// 127), interlaced fields, a size RFC 2435 can't say (0), or quantization tables of 16-bit precision.
    /// </exception>
    public void Add(ReadOnlySpan<byte> packet, List<RtpJpegFrame> completed)
    {
        if (!RtpH264Depacketizer.TryParse(packet, out var header, out var payload))
        {
            _damaged = true; // not RTP version 2, or cut short: as good as lost
            return;
        }

        if (_payloadType >= 0 && header.PayloadType != _payloadType)
        {
            return;
        }

        if (_expected >= 0)
        {
            int gap = (ushort)(header.Sequence - _expected);
            if (gap >= 0x8000)
            {
                return; // an old or repeated packet: over TCP never, and reordering over UDP isn't undone
            }

            // Counted only: the fragment offsets tell which frame lost what.
            LostPackets += gap;
        }

        _expected = (ushort)(header.Sequence + 1);
        if (_inFrame && header.Timestamp != _timestamp)
        {
            // The marker packet of the frame before was lost (or never sent): the new timestamp ends it.
            completed.Add(new RtpJpegFrame(null, _timestamp));
            _inFrame = false;
        }

        if (!_inFrame)
        {
            _inFrame = true;
            _timestamp = header.Timestamp;
            _damaged = false;
            _quantization = null;
            _scan.SetLength(0);
        }

        AddPayload(payload);
        if (header.Marker)
        {
            completed.Add(new RtpJpegFrame(_damaged || _quantization is null || _scan.Length == 0 ? null : Jpeg(), _timestamp));
            _inFrame = false;
        }
    }

    /// <summary>
    /// The quantization tables for Q 1 to 99, luma then chroma, in zig-zag order: T.81 K.1's scaled as RFC 2435's
    /// Appendix A scales them (the IJG library's quality).
    /// </summary>
    internal static byte[] ComputedTables(int q)
    {
        int factor = Math.Clamp(q, 1, 99);
        int scale = factor < 50 ? 5000 / factor : 200 - (factor * 2);
        var tables = new byte[128];
        for (int i = 0; i < 64; i++)
        {
            tables[i] = (byte)Math.Clamp(((LumaQuantization[ZigZag[i]] * scale) + 50) / 100, 1, 255);
            tables[64 + i] = (byte)Math.Clamp(((ChromaQuantization[ZigZag[i]] * scale) + 50) / 100, 1, 255);
        }

        return tables;
    }

    private void AddPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
        {
            _damaged = true;
            return;
        }

        int typeSpecific = payload[0];
        int offset = (payload[1] << 16) | (payload[2] << 8) | payload[3];
        int type = payload[4];
        int q = payload[5];
        int width = payload[6] * 8;
        int height = payload[7] * 8;
        if ((type & 0x3F) > 1 || type >= 128)
        {
            throw new NotSupportedException($"The camera sends RTP/JPEG type {type}; only 0, 1, 64 and 65 (RFC 2435's 4:2:2 and 4:2:0) are supported.");
        }

        if (q == 0 || q is >= 100 and < 128)
        {
            throw new NotSupportedException($"The camera sends RTP/JPEG with Q {q}, which RFC 2435 reserves.");
        }

        if (typeSpecific != 0)
        {
            throw new NotSupportedException("The camera sends interlaced JPEG over RTP (fields), which isn't supported.");
        }

        if (width == 0 || height == 0)
        {
            throw new NotSupportedException("The camera sends JPEG over RTP of a size RFC 2435 can't say (over 2040 pixels).");
        }

        int position = 8;
        int restartInterval = 0;
        if (type >= 64)
        {
            if (payload.Length < position + 4)
            {
                _damaged = true;
                return;
            }

            // The restart marker header: the interval, then where the packet's intervals fall (the offsets say it too).
            restartInterval = (payload[position] << 8) | payload[position + 1];
            position += 4;
        }

        if (offset == 0)
        {
            if (_scan.Length != 0 || _quantization is not null)
            {
                _damaged = true; // a second first fragment
                return;
            }

            _type = type;
            _width = width;
            _height = height;
            _restartInterval = restartInterval;
            if (q >= 128)
            {
                if (payload.Length < position + 4)
                {
                    _damaged = true;
                    return;
                }

                int precision = payload[position + 1];
                int length = (payload[position + 2] << 8) | payload[position + 3];
                position += 4;
                if (precision != 0)
                {
                    throw new NotSupportedException("The camera sends 16-bit quantization tables, which baseline JPEG doesn't have.");
                }

                if (length == 0)
                {
                    // The tables of an earlier frame with this Q; 255 means they change every frame, so none is kept.
                    if (q == 255 || !_tables.TryGetValue(q, out _quantization))
                    {
                        _damaged = true;
                        return;
                    }
                }
                else
                {
                    // Two tables, luma's then chroma's; or one for both, as FFmpeg's packetizer sends when its encoder
                    // wrote one DQT table for all three components.
                    if (length is not (64 or 128) || payload.Length < position + length)
                    {
                        _damaged = true;
                        return;
                    }

                    _quantization = new byte[128];
                    payload.Slice(position, 64).CopyTo(_quantization);
                    payload.Slice(position + length - 64, 64).CopyTo(_quantization.AsSpan(64));
                    position += length;
                    if (q < 255)
                    {
                        _tables[q] = _quantization;
                    }
                }
            }
            else if (!_tables.TryGetValue(q, out _quantization))
            {
                _quantization = ComputedTables(q);
                _tables[q] = _quantization;
            }
        }

        // A fragment that doesn't continue the scan where it stands: one before it was lost.
        if (_damaged || offset != _scan.Length || _scan.Length + payload.Length - position > MaxFrame)
        {
            _damaged = true;
            return;
        }

        _scan.Write(payload.Slice(position));
    }

    // The frame's JPEG: SOI, DQT, DRI, SOF0, DHT, SOS, the scan and EOI (unless the scan has it).
    private byte[] Jpeg()
    {
        var jpeg = new MemoryStream((int)_scan.Length + 700);
        jpeg.Write([0xFF, 0xD8]);

        jpeg.Write([0xFF, 0xDB, 0, 132, 0]);
        jpeg.Write(_quantization!, 0, 64);
        jpeg.WriteByte(1);
        jpeg.Write(_quantization!, 64, 64);

        if (_restartInterval > 0)
        {
            jpeg.Write([0xFF, 0xDD, 0, 4, (byte)(_restartInterval >> 8), (byte)_restartInterval]);
        }

        byte luma = (_type & 0x3F) == 0 ? (byte)0x21 : (byte)0x22; // type 0: Y 2x1 (4:2:2); type 1: 2x2 (4:2:0)
        jpeg.Write([0xFF, 0xC0, 0, 17, 8, (byte)(_height >> 8), (byte)_height, (byte)(_width >> 8), (byte)_width, 3, 1, luma, 0, 2, 0x11, 1, 3, 0x11, 1]);

        int dht = 2 + (4 * 17) + DcLuminanceValues.Length + AcLuminanceValues.Length + DcChrominanceValues.Length + AcChrominanceValues.Length;
        jpeg.Write([0xFF, 0xC4, (byte)(dht >> 8), (byte)dht]);
        HuffmanTable(jpeg, 0x00, DcLuminanceBits, DcLuminanceValues);
        HuffmanTable(jpeg, 0x10, AcLuminanceBits, AcLuminanceValues);
        HuffmanTable(jpeg, 0x01, DcChrominanceBits, DcChrominanceValues);
        HuffmanTable(jpeg, 0x11, AcChrominanceBits, AcChrominanceValues);

        jpeg.Write([0xFF, 0xDA, 0, 12, 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);
        jpeg.Write(_scan.GetBuffer(), 0, (int)_scan.Length);
        var buffer = _scan.GetBuffer();
        if (_scan.Length < 2 || buffer[_scan.Length - 2] != 0xFF || buffer[_scan.Length - 1] != 0xD9)
        {
            jpeg.Write([0xFF, 0xD9]);
        }

        return jpeg.ToArray();
    }

    private static void HuffmanTable(MemoryStream jpeg, byte classAndId, ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
    {
        jpeg.WriteByte(classAndId);
        jpeg.Write(bits);
        jpeg.Write(values);
    }

    // T.81's tables, copied from ManagedCodecs.Mjpeg's JpegTables (the Unity build compiles both into one assembly, so
    // the file can't be shared; RtpJpegDepacketizerTests checks the copies are the same).
    internal static ReadOnlySpan<byte> ZigZag =>
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    // Annex K.1, Tables K.1 and K.2, in natural (row-major) order.
    internal static ReadOnlySpan<byte> LumaQuantization =>
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    internal static ReadOnlySpan<byte> ChromaQuantization =>
    [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    ];

    // Annex K.3: BITS (codes of each length, 1 to 16) and HUFFVAL.
    internal static ReadOnlySpan<byte> DcLuminanceBits => [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];

    internal static ReadOnlySpan<byte> DcLuminanceValues => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    internal static ReadOnlySpan<byte> DcChrominanceBits => [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];

    internal static ReadOnlySpan<byte> DcChrominanceValues => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    internal static ReadOnlySpan<byte> AcLuminanceBits => [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D];

    internal static ReadOnlySpan<byte> AcLuminanceValues =>
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
        0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];

    internal static ReadOnlySpan<byte> AcChrominanceBits => [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];

    internal static ReadOnlySpan<byte> AcChrominanceValues =>
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
        0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
        0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
        0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
        0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];
}

/// <summary>A frame rebuilt from RTP: its JPEG, or null when it arrived incomplete (<see cref="Damaged"/>).</summary>
/// <param name="Jpeg">The whole JPEG, or null.</param>
/// <param name="Timestamp">Its RTP timestamp (90 kHz).</param>
internal sealed record RtpJpegFrame(byte[]? Jpeg, uint Timestamp)
{
    public bool Damaged => Jpeg is null;
}
