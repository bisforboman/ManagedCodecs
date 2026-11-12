using System.Buffers.Binary;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// An index table's edit units in a clip-wrapped essence element (SMPTE ST 377-1 11), on hand-made segments: by the
/// entries' stream offsets, by the constant edit unit size, and the offsets a file can't hold. The OP-Atom samples
/// (MxfConformanceTests) cover the same against FFmpeg's packets.
/// </summary>
public class MxfIndexTests
{
    [Fact]
    public void CutsByTheEntriesStreamOffsets()
    {
        var index = Index(Segment(start: 0, offsets: [0, 100, 150]));

        Assert.Equal([(0L, 100L), (100L, 50L), (150L, 70L)], index.EditUnits(220));
    }

    [Fact]
    public void JoinsSegmentsInTheirEditUnitOrder()
    {
        var index = Index(Segment(start: 2, offsets: [150, 160]), Segment(start: 0, offsets: [0, 100]));

        Assert.Equal([(0L, 100L), (100L, 50L), (150L, 10L), (160L, 40L)], index.EditUnits(200));
    }

    [Fact]
    public void CutsByTheConstantSizeWithoutEntries()
    {
        var index = Index(Segment(start: 0, offsets: null, editUnitByteCount: 30));

        // Whole edit units only: the last 10 bytes are none.
        Assert.Equal([(0L, 30L), (30L, 30L), (60L, 30L)], index.EditUnits(100));
        Assert.Equal(30, index.EditUnitByteCount);
    }

    [Fact]
    public void GivesNoEditUnitsWithoutOffsetsOrSize()
    {
        Assert.Empty(Index(Segment(start: 0, offsets: null)).EditUnits(100));
    }

    [Theory]
    [InlineData(new long[] { 10, 100 }, 200)] // not from the element's start
    [InlineData(new long[] { 0, 100, 50 }, 200)] // going back
    [InlineData(new long[] { 0, 300 }, 200)] // past the element
    [InlineData(new long[] { 0, -1 }, 200)] // an offset over Int64
    public void RefusesOffsetsOutsideTheElement(long[] offsets, long size)
    {
        var index = Index(Segment(start: 0, offsets: offsets));

        Assert.Throws<InvalidDataException>(() => index.EditUnits(size));
    }

    private static MxfIndex Index(params byte[][] segments)
    {
        var indexes = new Dictionary<uint, MxfIndex>();
        foreach (var segment in segments)
        {
            MxfIndex.AddSegment(indexes, segment, berLengths: false);
        }

        return Assert.Single(indexes).Value;
    }

    // An index table segment's value: Index Start Position, BodySID 1, the Edit Unit Byte Count where given, and an
    // entry (temporal offset 0, key-frame offset 0, flags 0x80, the stream offset) for each offset.
    private static byte[] Segment(long start, long[]? offsets, uint editUnitByteCount = 0)
    {
        var bytes = new List<byte>();
        void Property(ushort tag, byte[] value)
        {
            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(header, tag);
            BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)value.Length);
            bytes.AddRange(header.ToArray());
            bytes.AddRange(value);
        }

        var startValue = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(startValue, start);
        Property(0x3F0C, startValue);
        Property(0x3F07, [0, 0, 0, 1]);
        if (editUnitByteCount > 0)
        {
            var count = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(count, editUnitByteCount);
            Property(0x3F05, count);
        }

        if (offsets is not null)
        {
            var entries = new byte[8 + (offsets.Length * 11)];
            BinaryPrimitives.WriteUInt32BigEndian(entries, (uint)offsets.Length);
            BinaryPrimitives.WriteUInt32BigEndian(entries.AsSpan(4), 11);
            for (int i = 0; i < offsets.Length; i++)
            {
                var entry = entries.AsSpan(8 + (i * 11), 11);
                entry[2] = 0x80;
                BinaryPrimitives.WriteInt64BigEndian(entry[3..], offsets[i]);
            }

            Property(0x3F0A, entries);
        }

        return [.. bytes];
    }
}
