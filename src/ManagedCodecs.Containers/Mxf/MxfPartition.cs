using System.Buffers.Binary;

namespace ManagedCodecs.Containers;

/// <summary>A partition's kind: its pack key's byte 14 (SMPTE ST 377-1 7.2 to 7.4).</summary>
internal enum MxfPartitionKind : byte
{
    Header = 2,
    Body = 3,
    Footer = 4,
}

/// <summary>A partition's status: its pack key's byte 15 (SMPTE ST 377-1 6.2.3, Table 4).</summary>
internal enum MxfPartitionStatus : byte
{
    OpenIncomplete = 1,
    ClosedIncomplete = 2,
    OpenComplete = 3,
    ClosedComplete = 4,
}

/// <summary>
/// A partition pack (SMPTE ST 377-1 7.1, Table 5). The offsets in it (<see cref="ThisPartition"/> and the others) count
/// from the header partition pack's key, past any run-in; <see cref="Position"/> is where the pack is in the stream.
/// </summary>
internal sealed class MxfPartition
{
    // Table 5's fixed items before the essence container batch: the versions, KAGSize, the five 8-byte offsets and
    // counts, IndexSID, BodyOffset, BodySID and the operational pattern's UL.
    private const int FixedLength = 2 + 2 + 4 + (8 * 5) + 4 + 8 + 4 + 16;

    // A batch (ST 377-1 4.3): a UInt32 count, a UInt32 item length, the items.
    private const int BatchHeaderLength = 8;

    private MxfPartition(long position, MxfPartitionKind kind, MxfPartitionStatus status, long end)
    {
        Position = position;
        Kind = kind;
        Status = status;
        End = end;
    }

    /// <summary>Where the pack's key is in the stream.</summary>
    public long Position { get; }

    /// <summary>Where the pack's value ends in the stream: the header metadata, an index table or essence follows.</summary>
    public long End { get; }

    public MxfPartitionKind Kind { get; }

    public MxfPartitionStatus Status { get; }

    /// <summary>Whether the partition is closed (ST 377-1 6.2.3): its pack's values, and its header metadata if any, are final.</summary>
    public bool IsClosed => Status is MxfPartitionStatus.ClosedIncomplete or MxfPartitionStatus.ClosedComplete;

    /// <summary>Whether the partition is complete (ST 377-1 6.2.3): no best-effort property in its header metadata is unknown.</summary>
    public bool IsComplete => Status is MxfPartitionStatus.OpenComplete or MxfPartitionStatus.ClosedComplete;

    public ushort MajorVersion { get; private init; }

    public ushort MinorVersion { get; private init; }

    /// <summary>The KLV alignment grid, in bytes.</summary>
    public uint KagSize { get; private init; }

    public ulong ThisPartition { get; private init; }

    public ulong PreviousPartition { get; private init; }

    /// <summary>The footer partition's offset, or 0 where not known (a header or body partition may leave it so).</summary>
    public ulong FooterPartition { get; private init; }

    /// <summary>The bytes of header metadata, the primer pack's key first, after the pack (and any fill item after it).</summary>
    public ulong HeaderByteCount { get; private init; }

    /// <summary>The bytes of index table segments, after the header metadata.</summary>
    public ulong IndexByteCount { get; private init; }

    /// <summary>The index table these segments belong to, or 0 for none.</summary>
    public uint IndexSid { get; private init; }

    /// <summary>Where this partition's essence starts within its essence container.</summary>
    public ulong BodyOffset { get; private init; }

    /// <summary>The essence container this partition holds part of, or 0 for none.</summary>
    public uint BodySid { get; private init; }

    /// <summary>The operational pattern's universal label (ST 377-1 8).</summary>
    public byte[] OperationalPattern { get; private init; } = [];

    /// <summary>The essence containers' universal labels.</summary>
    public IReadOnlyList<byte[]> EssenceContainers { get; private init; } = [];

    /// <summary>Reads the partition pack at <paramref name="position"/>.</summary>
    /// <exception cref="InvalidDataException">No partition pack is there, or it is malformed.</exception>
    /// <exception cref="EndOfStreamException">The stream ends inside the pack.</exception>
    public static MxfPartition Read(Stream stream, long position)
    {
        Span<byte> key = stackalloc byte[Klv.KeyLength];
        long valueStart = Klv.ReadHeader(stream, position, key, out long length);
        if (!Klv.IsPartitionPack(key, out var kind, out var status))
        {
            throw new InvalidDataException($"No MXF partition pack at byte {position}.");
        }

        if (length < FixedLength + BatchHeaderLength)
        {
            throw new InvalidDataException($"The MXF partition pack at byte {position} is {length} bytes, too short.");
        }

        // The batch's items are 16-byte labels: a pack longer than a large batch of them is not one.
        const int MaxLength = FixedLength + BatchHeaderLength + (Klv.KeyLength * 4096);
        if (length > MaxLength)
        {
            throw new InvalidDataException($"The MXF partition pack at byte {position} is {length} bytes, too long.");
        }

        byte[] value = new byte[length];
        stream.ReadExactly(value);
        ReadOnlySpan<byte> v = value;

        uint count = BinaryPrimitives.ReadUInt32BigEndian(v[FixedLength..]);
        uint itemLength = BinaryPrimitives.ReadUInt32BigEndian(v[(FixedLength + 4)..]);
        if (count != 0 && itemLength != Klv.KeyLength)
        {
            throw new InvalidDataException($"The MXF partition pack at byte {position} has essence container labels of {itemLength} bytes, not 16.");
        }

        if (count > (ulong)(length - FixedLength - BatchHeaderLength) / Klv.KeyLength)
        {
            throw new InvalidDataException($"The MXF partition pack at byte {position} lists {count} essence containers, more than it holds.");
        }

        var containers = new byte[count][];
        for (int i = 0; i < containers.Length; i++)
        {
            containers[i] = v.Slice(FixedLength + BatchHeaderLength + (i * Klv.KeyLength), Klv.KeyLength).ToArray();
        }

        return new MxfPartition(position, kind, status, valueStart + length)
        {
            MajorVersion = BinaryPrimitives.ReadUInt16BigEndian(v),
            MinorVersion = BinaryPrimitives.ReadUInt16BigEndian(v[2..]),
            KagSize = BinaryPrimitives.ReadUInt32BigEndian(v[4..]),
            ThisPartition = BinaryPrimitives.ReadUInt64BigEndian(v[8..]),
            PreviousPartition = BinaryPrimitives.ReadUInt64BigEndian(v[16..]),
            FooterPartition = BinaryPrimitives.ReadUInt64BigEndian(v[24..]),
            HeaderByteCount = BinaryPrimitives.ReadUInt64BigEndian(v[32..]),
            IndexByteCount = BinaryPrimitives.ReadUInt64BigEndian(v[40..]),
            IndexSid = BinaryPrimitives.ReadUInt32BigEndian(v[48..]),
            BodyOffset = BinaryPrimitives.ReadUInt64BigEndian(v[52..]),
            BodySid = BinaryPrimitives.ReadUInt32BigEndian(v[60..]),
            OperationalPattern = v.Slice(64, Klv.KeyLength).ToArray(),
            EssenceContainers = containers,
        };
    }
}
