using System.Buffers.Binary;

namespace ManagedCodecs.Containers;

/// <summary>
/// An essence container's index table (SMPTE ST 377-1 11): its segments' entries by edit unit, each with its temporal
/// offset and flags. Byte offsets are not kept: <see cref="MxfDemuxer"/> finds the essence elements by walking the file.
/// </summary>
internal sealed class MxfIndex
{
    /// <summary>An index entry's random access flag (Table 28, bit 7).</summary>
    public const byte RandomAccess = 0x80;

    private readonly List<(long Start, (sbyte TemporalOffset, byte Flags)[] Entries)> _segments = [];

    /// <summary>Whether the first delta entry (the edit unit's first element) has PosTableIndex -1: it is reordered (11.2.3).</summary>
    public bool FirstElementReordered { get; private set; }

    /// <summary>The temporal offset and flags of edit unit <paramref name="editUnit"/>, or null where no segment indexes it.</summary>
    public (sbyte TemporalOffset, byte Flags)? Entry(long editUnit)
    {
        foreach (var (start, entries) in _segments)
        {
            if (editUnit >= start && editUnit - start < entries.Length)
            {
                return entries[editUnit - start];
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="key"/> is an index table segment's (Table 25: byte 6, the length coding, and byte 8 aside).</summary>
    public static bool IsSegmentKey(ReadOnlySpan<byte> key) =>
        key[..5].SequenceEqual((ReadOnlySpan<byte>)[0x06, 0x0E, 0x2B, 0x34, 0x02]) && key[6] == 0x01
        && key[8..].SequenceEqual((ReadOnlySpan<byte>)[0x0D, 0x01, 0x02, 0x01, 0x01, 0x10, 0x01, 0x00]);

    /// <summary>
    /// Adds the segment whose set value is <paramref name="value"/>, to the index of its BodySID
    /// in <paramref name="indexes"/>. Its properties are read by their static local tags: the primer pack does not cover
    /// index tables (ST 377-1 9.2). A segment repeated (the same start) is kept once.
    /// </summary>
    public static void AddSegment(Dictionary<uint, MxfIndex> indexes, ReadOnlySpan<byte> value, bool berLengths)
    {
        long start = 0;
        uint bodySid = 0;
        ReadOnlySpan<byte> entries = default;
        ReadOnlySpan<byte> deltas = default;
        int offset = 0;
        while (offset < value.Length)
        {
            if (value.Length - offset < 3)
            {
                throw new InvalidDataException("An MXF index table segment ends inside a property's tag or length.");
            }

            ushort tag = BinaryPrimitives.ReadUInt16BigEndian(value[offset..]);
            int length;
            if (berLengths)
            {
                using var reader = new MemoryStream(value[(offset + 2)..].ToArray(), writable: false);
                long l = Klv.ReadLength(reader, out int size);
                offset += 2 + size;
                length = l > value.Length - offset ? -1 : (int)l;
            }
            else
            {
                if (value.Length - offset < 4)
                {
                    throw new InvalidDataException("An MXF index table segment ends inside a property's length.");
                }

                length = BinaryPrimitives.ReadUInt16BigEndian(value[(offset + 2)..]);
                offset += 4;
            }

            if (length < 0 || length > value.Length - offset)
            {
                throw new InvalidDataException("A property of an MXF index table segment runs past it.");
            }

            var property = value.Slice(offset, length);
            switch (tag)
            {
                case 0x3F0C when length == 8: // Index Start Position
                    start = BinaryPrimitives.ReadInt64BigEndian(property);
                    break;
                case 0x3F07 when length == 4: // BodySID
                    bodySid = BinaryPrimitives.ReadUInt32BigEndian(property);
                    break;
                case 0x3F09: // Delta Entry Array
                    deltas = property;
                    break;
                case 0x3F0A: // Index Entry Array
                    entries = property;
                    break;
            }

            offset += length;
        }

        if (!indexes.TryGetValue(bodySid, out var index))
        {
            index = new MxfIndex();
            indexes[bodySid] = index;
        }

        // Delta entries (Table 27): PosTableIndex (Int8), Slice, Element Delta.
        if (deltas.Length >= 8 + 6 && BinaryPrimitives.ReadUInt32BigEndian(deltas) > 0 && BinaryPrimitives.ReadUInt32BigEndian(deltas[4..]) >= 6)
        {
            index.FirstElementReordered = (sbyte)deltas[8] == -1;
        }

        // Index entries (Table 28): Temporal Offset (Int8), Key-Frame Offset, Flags, Stream Offset, the slices'...
        if (entries.Length < 8 || index._segments.Exists(s => s.Start == start))
        {
            return;
        }

        uint count = BinaryPrimitives.ReadUInt32BigEndian(entries);
        uint entryLength = BinaryPrimitives.ReadUInt32BigEndian(entries[4..]);
        if (entryLength < 11 || count > (uint)(entries.Length - 8) / entryLength)
        {
            throw new InvalidDataException("An MXF index table segment's entries do not hold together.");
        }

        var list = new (sbyte, byte)[count];
        for (int i = 0; i < list.Length; i++)
        {
            var entry = entries.Slice(8 + (int)(i * entryLength), (int)entryLength);
            list[i] = ((sbyte)entry[0], entry[2]);
        }

        index._segments.Add((start, list));
    }
}
