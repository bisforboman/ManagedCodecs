namespace ManagedCodecs.Mjpeg;

/// <summary>JPEG marker codes (ITU-T T.81, Table B.1), without the leading 0xFF.</summary>
internal static class JpegMarker
{
    public const byte Sof0 = 0xC0; // Baseline DCT
    public const byte Sof1 = 0xC1; // Extended sequential DCT, Huffman
    public const byte Sof2 = 0xC2; // Progressive DCT
    public const byte Dht = 0xC4;
    public const byte Jpg = 0xC8;
    public const byte Dac = 0xCC;
    public const byte Rst0 = 0xD0;
    public const byte Rst7 = 0xD7;
    public const byte Soi = 0xD8;
    public const byte Eoi = 0xD9;
    public const byte Sos = 0xDA;
    public const byte Dqt = 0xDB;
    public const byte Dri = 0xDD;
    public const byte Tem = 0x01;

    public static bool IsStandalone(byte marker) => marker is Tem or (>= Rst0 and <= Eoi);

    /// <summary>Any SOFn marker other than baseline/extended sequential Huffman.</summary>
    public static bool IsUnsupportedFrame(byte marker) =>
        marker is >= Sof2 and <= 0xCF and not Dht and not Jpg and not Dac;
}

/// <summary>
/// One marker segment. Offsets index into the buffer passed to
/// <see cref="JpegMarkerReader.ReadSegments"/>. Only SOS segments have entropy-coded data.
/// </summary>
internal readonly record struct JpegSegment(
    byte Marker,
    int PayloadOffset,
    int PayloadLength,
    int EntropyDataOffset = 0,
    int EntropyDataLength = 0);

/// <summary>Splits a JPEG (one MJPEG frame) into its marker segments.</summary>
internal static class JpegMarkerReader
{
    public static List<JpegSegment> ReadSegments(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2 || data[0] != 0xFF || data[1] != JpegMarker.Soi)
        {
            throw new InvalidDataException("Missing SOI marker.");
        }

        var segments = new List<JpegSegment> { new(JpegMarker.Soi, 2, 0) };
        int pos = 2;

        while (pos < data.Length)
        {
            if (data[pos] != 0xFF)
            {
                throw new InvalidDataException($"Expected a marker at offset {pos}.");
            }

            // Any number of 0xFF fill bytes may precede a marker.
            while (pos < data.Length && data[pos] == 0xFF)
            {
                pos++;
            }

            if (pos >= data.Length)
            {
                break;
            }

            byte marker = data[pos++];

            if (marker == JpegMarker.Eoi)
            {
                segments.Add(new(marker, pos, 0));
                return segments;
            }

            if (JpegMarker.IsStandalone(marker))
            {
                segments.Add(new(marker, pos, 0));
                continue;
            }

            if (pos + 2 > data.Length)
            {
                throw new InvalidDataException($"Truncated segment 0xFF{marker:X2}.");
            }

            int length = (data[pos] << 8) | data[pos + 1];
            if (length < 2 || pos + length > data.Length)
            {
                throw new InvalidDataException($"Invalid length {length} for segment 0xFF{marker:X2}.");
            }

            int payloadOffset = pos + 2;
            int payloadLength = length - 2;
            pos += length;

            if (marker == JpegMarker.Sos)
            {
                int scanStart = pos;
                pos = FindEndOfEntropyData(data, pos);
                segments.Add(new(marker, payloadOffset, payloadLength, scanStart, pos - scanStart));
            }
            else
            {
                segments.Add(new(marker, payloadOffset, payloadLength));
            }
        }

        throw new InvalidDataException("Missing EOI marker.");
    }

    /// <summary>
    /// Entropy-coded data runs until a 0xFF that is not followed by 0x00 (byte stuffing)
    /// or an RSTn marker. Restart markers stay inside the scan data.
    /// </summary>
    private static int FindEndOfEntropyData(ReadOnlySpan<byte> data, int pos)
    {
        while (pos < data.Length)
        {
            int index = data[pos..].IndexOf((byte)0xFF);
            if (index < 0 || pos + index + 1 >= data.Length)
            {
                return data.Length;
            }

            pos += index;
            byte next = data[pos + 1];
            if (next == 0x00 || next is >= JpegMarker.Rst0 and <= JpegMarker.Rst7)
            {
                pos += 2;
                continue;
            }

            return pos;
        }

        return data.Length;
    }
}
