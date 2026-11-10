using System.Buffers.Binary;

namespace ManagedCodecs.Containers;

/// <summary>
/// KLV coding (SMPTE ST 336) as MXF uses it (SMPTE ST 377-1 6.3): a 16-byte key, a BER length, the value. The keys
/// here are those ST 377-1 defines itself: the partition packs' (its Table 4), the random index pack's (Table 29) and
/// the fill item's (6.3.3).
/// </summary>
internal static class Klv
{
    public const int KeyLength = 16;

    // The longest BER length MXF allows (ST 377-1 6.3.4): 0x88 and eight bytes.
    private const int MaxLengthBytes = 8;

    /// <summary>The partition pack key's bytes 1 to 13 (ST 377-1 Table 4); byte 14 is the kind, 15 the status.</summary>
    private static ReadOnlySpan<byte> PartitionPackPrefix => [0x06, 0x0E, 0x2B, 0x34, 0x02, 0x05, 0x01, 0x01, 0x0D, 0x01, 0x02, 0x01, 0x01];

    /// <summary>The random index pack's key (ST 377-1 Table 29).</summary>
    private static ReadOnlySpan<byte> RandomIndexPackKey => [0x06, 0x0E, 0x2B, 0x34, 0x02, 0x05, 0x01, 0x01, 0x0D, 0x01, 0x02, 0x01, 0x01, 0x11, 0x01, 0x00];

    /// <summary>The fill item's key (ST 377-1 6.3.3, from SMPTE RP 210).</summary>
    private static ReadOnlySpan<byte> FillKey => [0x06, 0x0E, 0x2B, 0x34, 0x01, 0x01, 0x01, 0x02, 0x03, 0x01, 0x02, 0x10, 0x01, 0x00, 0x00, 0x00];

    /// <summary>
    /// How many of a partition pack key's first bytes a decoder searches for to skip a run-in (ST 377-1 6.6: "the first
    /// 11 bytes of the Partition Pack label").
    /// </summary>
    public const int RunInSearchLength = 11;

    /// <summary>The longest run-in ST 377-1 6.6 allows, exclusive.</summary>
    public const int MaxRunIn = 65536;

    /// <summary>
    /// Whether <paramref name="key"/> is <paramref name="label"/>, its byte 8 (the registry version) aside: ST 377-1 6.3.3
    /// has decoders ignore it for the fill item, as earlier encoders wrote another, and ST 336 lets parsers ignore it
    /// for any key.
    /// </summary>
    public static bool Matches(ReadOnlySpan<byte> key, ReadOnlySpan<byte> label) =>
        key.Length >= label.Length && key[..7].SequenceEqual(label[..7]) && key[8..label.Length].SequenceEqual(label[8..]);

    /// <summary>Whether <paramref name="key"/> starts like a partition pack key, its first 11 bytes (the run-in search's).</summary>
    public static bool StartsPartitionPack(ReadOnlySpan<byte> key) => Matches(key, PartitionPackPrefix[..RunInSearchLength]);

    /// <summary>
    /// The kind (2 header, 3 body, 4 footer) and status (1 to 4) of the partition pack <paramref name="key"/> names, or
    /// false for another key (another standard's partitions, such as SMPTE ST 410's generic stream partition, among them).
    /// </summary>
    public static bool IsPartitionPack(ReadOnlySpan<byte> key, out MxfPartitionKind kind, out MxfPartitionStatus status)
    {
        kind = default;
        status = default;
        if (!Matches(key, PartitionPackPrefix) || key[13] is < 2 or > 4 || key[14] is < 1 or > 4)
        {
            return false;
        }

        kind = (MxfPartitionKind)key[13];
        status = (MxfPartitionStatus)key[14];
        return true;
    }

    public static bool IsRandomIndexPack(ReadOnlySpan<byte> key) => Matches(key, RandomIndexPackKey);

    public static bool IsFill(ReadOnlySpan<byte> key) => Matches(key, FillKey);

    /// <summary>
    /// Reads a BER length (ST 336; ST 377-1 6.3.4) at the stream's position: the short form (below 0x80) or the long one
    /// (0x81 to 0x88, then that many bytes, big-endian).
    /// </summary>
    /// <param name="stream">The stream, positioned on the length.</param>
    /// <param name="size">The length's own size in bytes.</param>
    /// <exception cref="InvalidDataException">An unspecified length (0x80), more than eight bytes, or a length past <see cref="long.MaxValue"/>.</exception>
    /// <exception cref="EndOfStreamException">The stream ends inside the length.</exception>
    public static long ReadLength(Stream stream, out int size)
    {
        int first = stream.ReadByte();
        if (first < 0)
        {
            throw new EndOfStreamException("The file ends inside a KLV length.");
        }

        if (first < 0x80)
        {
            size = 1;
            return first;
        }

        int count = first & 0x7F;
        if (count == 0)
        {
            throw new InvalidDataException("A KLV length is unspecified (0x80), which MXF does not allow.");
        }

        if (count > MaxLengthBytes)
        {
            throw new InvalidDataException($"A KLV length takes {count} bytes, more than MXF's {MaxLengthBytes}.");
        }

        Span<byte> bytes = stackalloc byte[MaxLengthBytes];
        bytes.Clear();
        stream.ReadExactly(bytes[(MaxLengthBytes - count)..]);
        ulong value = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        if (value > long.MaxValue)
        {
            throw new InvalidDataException("A KLV length is larger than a file can be.");
        }

        size = 1 + count;
        return (long)value;
    }

    /// <summary>Reads a KLV item's key and length at <paramref name="position"/>, and returns where its value starts.</summary>
    /// <exception cref="EndOfStreamException">The stream ends inside the key or the length.</exception>
    public static long ReadHeader(Stream stream, long position, Span<byte> key, out long length)
    {
        stream.Position = position;
        stream.ReadExactly(key[..KeyLength]);
        length = ReadLength(stream, out int size);
        return checked(position + KeyLength + size);
    }
}
