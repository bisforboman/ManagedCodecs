using System.Buffers.Binary;

namespace ManagedCodecs.Containers;

/// <summary>
/// An essence container's index table (SMPTE ST 377-1 11): its segments' entries by edit unit, each with its temporal
/// offset, flags and stream offset, or the constant size of an edit unit where a segment has no entries.
/// <see cref="MxfDemuxer"/> finds frame-wrapped essence elements by walking the file, and splits a clip-wrapped one
/// into edit units by <see cref="EditUnits"/>.
/// </summary>
internal sealed class MxfIndex
{
    /// <summary>An index entry's random access flag (Table 28, bit 7).</summary>
    public const byte RandomAccess = 0x80;

    private readonly List<(long Start, (sbyte TemporalOffset, byte Flags, long StreamOffset)[] Entries)> _segments = [];

    /// <summary>The size of every edit unit (Edit Unit Byte Count, 3F05), where a segment gives one other than 0; else 0.</summary>
    public long EditUnitByteCount { get; private set; }

    /// <summary>Whether the first delta entry (the edit unit's first element) has PosTableIndex -1: it is reordered (11.2.3).</summary>
    public bool FirstElementReordered { get; private set; }

    /// <summary>The temporal offset and flags of edit unit <paramref name="editUnit"/>, or null where no segment indexes it.</summary>
    public (sbyte TemporalOffset, byte Flags)? Entry(long editUnit)
    {
        foreach (var (start, entries) in _segments)
        {
            if (editUnit >= start && editUnit - start < entries.Length)
            {
                var (temporalOffset, flags, _) = entries[editUnit - start];
                return (temporalOffset, flags);
            }
        }

        return null;
    }

    /// <summary>
    /// Where each edit unit starts in a clip-wrapped essence element whose value is <paramref name="size"/> bytes, and
    /// its length: by the entries' stream offsets, which count from the value's first byte (the next entry's offset, or
    /// the value's end, closes each), else by the constant <see cref="EditUnitByteCount"/> (whole edit units only).
    /// Empty where the index gives neither.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream offsets don't start at 0, go back, or run past the element.</exception>
    public List<(long Offset, long Length)> EditUnits(long size)
    {
        var units = new List<(long Offset, long Length)>();
        var offsets = _segments.OrderBy(s => s.Start).SelectMany(s => s.Entries.Select(e => e.StreamOffset)).ToList();
        if (offsets.Count > 0)
        {
            if (offsets[0] != 0)
            {
                throw new InvalidDataException($"A clip-wrapped MXF element's first index entry is at byte {offsets[0]}, not at its start.");
            }

            for (int i = 0; i < offsets.Count; i++)
            {
                long end = i + 1 < offsets.Count ? offsets[i + 1] : size;
                if (end < offsets[i] || end > size)
                {
                    throw new InvalidDataException($"A clip-wrapped MXF element's index entry {i} runs from byte {offsets[i]} to {end}, outside its {size} bytes.");
                }

                units.Add((offsets[i], end - offsets[i]));
            }
        }
        else if (EditUnitByteCount > 0)
        {
            for (long offset = 0; size - offset >= EditUnitByteCount; offset += EditUnitByteCount)
            {
                units.Add((offset, EditUnitByteCount));
            }
        }

        return units;
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
        uint editUnitByteCount = 0;
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
                case 0x3F05 when length == 4: // Edit Unit Byte Count
                    editUnitByteCount = BinaryPrimitives.ReadUInt32BigEndian(property);
                    break;
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

        if (editUnitByteCount > 0)
        {
            index.EditUnitByteCount = editUnitByteCount;
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

        var list = new (sbyte, byte, long)[count];
        for (int i = 0; i < list.Length; i++)
        {
            var entry = entries.Slice(8 + (int)(i * entryLength), (int)entryLength);
            ulong streamOffset = BinaryPrimitives.ReadUInt64BigEndian(entry[3..]);
            list[i] = ((sbyte)entry[0], entry[2], streamOffset > long.MaxValue ? -1 : (long)streamOffset);
        }

        index._segments.Add((start, list));
    }
}
