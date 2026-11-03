using System.Buffers.Binary;
using System.Text;

namespace ManagedCodecs.Containers;

/// <summary>EBML (RFC 8794): its variable-size integers and element headers, and the Matroska element IDs read here (RFC 9559).</summary>
internal static class Ebml
{
    public const uint Header = 0x1A45DFA3;
    public const uint DocType = 0x4282;
    public const uint Segment = 0x18538067;
    public const uint SeekHead = 0x114D9B74;
    public const uint Info = 0x1549A966;
    public const uint TimestampScale = 0x2AD7B1;
    public const uint Duration = 0x4489;
    public const uint Tracks = 0x1654AE6B;
    public const uint TrackEntry = 0xAE;
    public const uint TrackNumber = 0xD7;
    public const uint TrackType = 0x83;
    public const uint CodecId = 0x86;
    public const uint CodecPrivate = 0x63A2;
    public const uint DefaultDuration = 0x23E383;
    public const uint CodecDelay = 0x56AA;
    public const uint TrackTimestampScale = 0x23314F;
    public const uint Video = 0xE0;
    public const uint PixelWidth = 0xB0;
    public const uint PixelHeight = 0xBA;
    public const uint DisplayWidth = 0x54B0;
    public const uint DisplayHeight = 0x54BA;
    public const uint DisplayUnit = 0x54B2;
    public const uint Colour = 0x55B0;
    public const uint MatrixCoefficients = 0x55B1;
    public const uint Range = 0x55B9;
    public const uint TransferCharacteristics = 0x55BA;
    public const uint Primaries = 0x55BB;
    public const uint Audio = 0xE1;
    public const uint SamplingFrequency = 0xB5;
    public const uint OutputSamplingFrequency = 0x78B5;
    public const uint Channels = 0x9F;
    public const uint ContentEncodings = 0x6D80;
    public const uint ContentEncoding = 0x6240;
    public const uint ContentEncodingScope = 0x5032;
    public const uint ContentEncodingType = 0x5033;
    public const uint ContentCompression = 0x5034;
    public const uint ContentCompAlgo = 0x4254;
    public const uint ContentCompSettings = 0x4255;
    public const uint Cluster = 0x1F43B675;
    public const uint Timestamp = 0xE7;
    public const uint SimpleBlock = 0xA3;
    public const uint BlockGroup = 0xA0;
    public const uint Block = 0xA1;
    public const uint BlockDuration = 0x9B;
    public const uint ReferenceBlock = 0xFB;
    public const uint Cues = 0x1C53BB6B;
    public const uint Chapters = 0x1043A770;
    public const uint Tags = 0x1254C367;
    public const uint Attachments = 0x1941A469;

    /// <summary>
    /// Whether an element of this ID ends an unknown-size Cluster: a Segment's own children (and the EBML header and a
    /// Segment, of a chained file), none of which can be inside a Cluster.
    /// </summary>
    public static bool IsTopLevel(uint id) => id is Cluster or Cues or Tags or Chapters or Attachments or SeekHead or Info or Tracks or Header or Segment;

    /// <summary>An element ID (RFC 8794 5): 1 to 4 bytes, its length marker kept as part of it.</summary>
    public static bool TryReadId(ReadOnlySpan<byte> data, out uint id, out int length)
    {
        id = 0;
        length = data.Length > 0 ? Length(data[0]) : 0;
        if (length is 0 or > 4 || data.Length < length)
        {
            return false;
        }

        for (int i = 0; i < length; i++)
        {
            id = (id << 8) | data[i];
        }

        return true;
    }

    /// <summary>
    /// A variable-size integer (RFC 8794 4), as element data sizes and Matroska's track numbers and lace sizes are written:
    /// 1 to 8 bytes, its length marker removed; -1 for all value bits set (an unknown size).
    /// </summary>
    public static bool TryReadSize(ReadOnlySpan<byte> data, out long value, out int length)
    {
        value = 0;
        length = data.Length > 0 ? Length(data[0]) : 0;
        if (length == 0 || data.Length < length)
        {
            return false;
        }

        ulong bits = data[0] & (0xFFu >> length);
        for (int i = 1; i < length; i++)
        {
            bits = (bits << 8) | data[i];
        }

        value = bits == (1UL << (7 * length)) - 1 ? -1 : (long)bits;
        return true;
    }

    /// <summary>
    /// The next child element of <paramref name="data"/> (an element read whole), moving past it; false at its end. A
    /// child of unknown size runs to the end.
    /// </summary>
    public static bool NextChild(ref ReadOnlySpan<byte> data, out uint id, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (data.IsEmpty)
        {
            id = 0;
            return false;
        }

        if (!TryReadId(data, out id, out int idLength) || !TryReadSize(data[idLength..], out long size, out int sizeLength))
        {
            throw new InvalidDataException("Invalid EBML element header.");
        }

        int start = idLength + sizeLength;
        if (size < 0)
        {
            size = data.Length - start;
        }

        if (size > data.Length - start)
        {
            throw new InvalidDataException($"The EBML element {id:X} runs past the end of its parent.");
        }

        payload = data.Slice(start, (int)size);
        data = data[(start + (int)size)..];
        return true;
    }

    /// <summary>An unsigned integer element's value: 0 to 8 bytes, big-endian.</summary>
    public static ulong UInt(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 8)
        {
            throw new InvalidDataException($"An EBML unsigned integer of {payload.Length} bytes.");
        }

        ulong value = 0;
        foreach (byte b in payload)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    /// <summary>An unsigned integer element's value that a long holds (a duration, a delay).</summary>
    public static long Bounded(ReadOnlySpan<byte> payload) => checked((long)UInt(payload));

    /// <summary>A float element's value: 0, 4 or 8 bytes.</summary>
    public static double Float(ReadOnlySpan<byte> payload) => payload.Length switch
    {
        0 => 0,
        4 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(payload)),
        8 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(payload)),
        _ => throw new InvalidDataException($"An EBML float of {payload.Length} bytes."),
    };

    /// <summary>A string element's value: ASCII or UTF-8, up to its first zero byte.</summary>
    public static string String(ReadOnlySpan<byte> payload)
    {
        int end = payload.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? payload : payload[..end]);
    }

    /// <summary>How many bytes of a small element to look at: its size, or 9 for one too large to be a number (refused then).</summary>
    public static int Bytes(long size) => (int)Math.Min(size, 9);

    // The length of a variable-size integer from its first byte's leading zeros; 0 for a zero byte.
    private static int Length(byte first)
    {
        for (int length = 1; length <= 8; length++)
        {
            if ((first & (0x80 >> (length - 1))) != 0)
            {
                return length;
            }
        }

        return 0;
    }
}
