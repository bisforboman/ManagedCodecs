using System.Buffers.Binary;

namespace ManagedCodecs.Containers;

/// <summary>
/// An MXF file's partitions (SMPTE ST 377-1 6.2, 7, 12), in file order: the header partition, its body partitions, and
/// the footer partition where there is one. They are found from the random index pack at the end of the file when it is
/// there and lists them truly; else back from the footer partition by each pack's PreviousPartition; else by walking
/// the file's KLV items from the header partition on (an open file written to a pipe may have neither).
/// </summary>
internal sealed class MxfPartitions
{
    // A random index pack is its key, a BER length, 12 bytes a partition and a final UInt32 (ST 377-1 Table 30).
    private const int RipEntryLength = 12;

    private MxfPartitions(long runIn, IReadOnlyList<MxfPartition> partitions, MxfPartitionSource source)
    {
        RunIn = runIn;
        Partitions = partitions;
        Source = source;
    }

    /// <summary>The run-in's length (ST 377-1 6.6): where the header partition pack is, which the offsets count from.</summary>
    public long RunIn { get; }

    /// <summary>The partitions, by offset; the first is the header partition.</summary>
    public IReadOnlyList<MxfPartition> Partitions { get; }

    /// <summary>How they were found.</summary>
    public MxfPartitionSource Source { get; }

    public MxfPartition Header => Partitions[0];

    /// <summary>The footer partition, or null if the file has none.</summary>
    public MxfPartition? Footer => Partitions[^1].Kind == MxfPartitionKind.Footer ? Partitions[^1] : null;

    /// <summary>Whether the stream starts with an MXF header partition pack, after a run-in of up to 64 KiB.</summary>
    public static bool Probe(Stream stream) => FindHeader(stream) >= 0;

    /// <summary>Finds the partitions of the MXF file in <paramref name="stream"/>.</summary>
    /// <exception cref="InvalidDataException">No header partition pack starts the file, or the file is malformed.</exception>
    /// <exception cref="EndOfStreamException">The file ends inside a KLV item's key or length.</exception>
    public static MxfPartitions Read(Stream stream)
    {
        long runIn = FindHeader(stream);
        if (runIn < 0)
        {
            throw new InvalidDataException("Not an MXF file: no header partition pack.");
        }

        var header = MxfPartition.Read(stream, runIn);
        if (header.Kind != MxfPartitionKind.Header || header.ThisPartition != 0)
        {
            throw new InvalidDataException("The MXF file's first partition is not a header partition at offset 0.");
        }

        if (TryFromRandomIndexPack(stream, runIn, out var partitions))
        {
            return new MxfPartitions(runIn, partitions, MxfPartitionSource.RandomIndexPack);
        }

        if (TryFromFooter(stream, runIn, header, out partitions))
        {
            return new MxfPartitions(runIn, partitions, MxfPartitionSource.PreviousPartitions);
        }

        return new MxfPartitions(runIn, Walk(stream, header), MxfPartitionSource.Walk);
    }

    /// <summary>
    /// Where the header partition pack starts: ST 377-1 6.6 has a decoder skip a run-in of under 64 KiB by searching for
    /// the first 11 bytes of the partition pack label, which the run-in may not hold. -1 if there is none.
    /// </summary>
    private static long FindHeader(Stream stream)
    {
        byte[] head = new byte[Klv.MaxRunIn - 1 + Klv.KeyLength];
        stream.Position = 0;
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        ReadOnlySpan<byte> span = head.AsSpan(0, read);
        for (int i = 0; i + Klv.KeyLength <= span.Length; i++)
        {
            int next = span[i..^(Klv.KeyLength - 1)].IndexOf((byte)0x06);
            if (next < 0)
            {
                break;
            }

            i += next;
            if (Klv.StartsPartitionPack(span[i..]))
            {
                return Klv.IsPartitionPack(span.Slice(i, Klv.KeyLength), out var kind, out _) && kind == MxfPartitionKind.Header ? i : -1;
            }
        }

        return -1;
    }

    /// <summary>
    /// The partitions the random index pack lists, if the file ends with one (its last four bytes give its length) and
    /// every offset in it is a partition pack that says it is at that offset.
    /// </summary>
    public static bool TryFromRandomIndexPack(Stream stream, long runIn, out List<MxfPartition> partitions)
    {
        partitions = [];
        long fileLength = stream.Length;
        if (fileLength - runIn < Klv.KeyLength + 1 + 4)
        {
            return false;
        }

        Span<byte> tail = stackalloc byte[4];
        stream.Position = fileLength - 4;
        stream.ReadExactly(tail);
        uint packLength = BinaryPrimitives.ReadUInt32BigEndian(tail);
        if (packLength < Klv.KeyLength + 1 + 4 || packLength > fileLength - runIn)
        {
            return false;
        }

        long start = fileLength - packLength;
        Span<byte> key = stackalloc byte[Klv.KeyLength];
        long valueStart;
        long length;
        try
        {
            // The last four bytes of a file without one are anything: so is what they point at.
            valueStart = Klv.ReadHeader(stream, start, key, out length);
        }
        catch (Exception e)
        {
            // No exception filter: compiled to WebAssembly, one broke the method's arguments (ExceptionFilterTests).
            if (!IsNotThere(e))
            {
                throw;
            }

            return false;
        }

        if (!Klv.IsRandomIndexPack(key) || valueStart + length != fileLength || (length - 4) % RipEntryLength != 0)
        {
            return false;
        }

        byte[] entries = new byte[length - 4];
        stream.ReadExactly(entries);
        ulong previousOffset = 0;
        for (int i = 0; i < entries.Length; i += RipEntryLength)
        {
            ulong offset = BinaryPrimitives.ReadUInt64BigEndian(entries.AsSpan(i + 4));
            if ((i > 0 && offset <= previousOffset) || offset >= (ulong)(start - runIn) || !TryReadPartition(stream, runIn, offset, out var partition))
            {
                return false;
            }

            uint bodySid = BinaryPrimitives.ReadUInt32BigEndian(entries.AsSpan(i));
            if (partition.BodySid != bodySid)
            {
                return false;
            }

            partitions.Add(partition);
            previousOffset = offset;
        }

        return partitions.Count > 0 && partitions[0].ThisPartition == 0;
    }

    /// <summary>
    /// The partitions back from the footer partition by each pack's PreviousPartition, if the header partition pack
    /// gives the footer's offset (ST 377-1 7.2.2 lets it be 0) and the chain reaches the header partition with offsets
    /// that fall each time.
    /// </summary>
    public static bool TryFromFooter(Stream stream, long runIn, MxfPartition header, out List<MxfPartition> partitions)
    {
        partitions = [];
        ulong footerOffset = header.FooterPartition;
        if (footerOffset == 0 || !TryReadPartition(stream, runIn, footerOffset, out var footer) || footer.Kind != MxfPartitionKind.Footer)
        {
            return false;
        }

        var chain = new List<MxfPartition> { footer };
        var current = footer;
        while (current.ThisPartition != 0)
        {
            ulong previous = current.PreviousPartition;
            if (previous >= current.ThisPartition || !TryReadPartition(stream, runIn, previous, out current))
            {
                return false;
            }

            chain.Add(current);
        }

        chain.Reverse();
        partitions = chain;
        return true;
    }

    /// <summary>
    /// The partitions found by walking every KLV item from the header partition pack to the end of the file, or to the
    /// item it breaks off in (a file cut short, or still being written).
    /// </summary>
    public static List<MxfPartition> Walk(Stream stream, MxfPartition header)
    {
        var partitions = new List<MxfPartition> { header };
        long fileLength = stream.Length;
        long position = header.End;
        Span<byte> key = stackalloc byte[Klv.KeyLength];
        while (position < fileLength && Klv.HeaderFits(stream, position, fileLength))
        {
            long valueStart = Klv.ReadHeader(stream, position, key, out long length);
            long next = checked(valueStart + length);
            if (next > fileLength)
            {
                break;
            }

            if (Klv.IsPartitionPack(key, out _, out _))
            {
                partitions.Add(MxfPartition.Read(stream, position));
            }
            else if (Klv.IsRandomIndexPack(key))
            {
                // The last item (ST 377-1 12): one that did not hold up (TryFromRandomIndexPack) ends the walk too.
                break;
            }

            position = next;
        }

        return partitions;
    }

    /// <summary>Whether <paramref name="e"/> says that what an offset points at is not what it should be.</summary>
    private static bool IsNotThere(Exception e) => e is InvalidDataException || Malformed.IsMalformedInput(e);

    private static bool TryReadPartition(Stream stream, long runIn, ulong offset, out MxfPartition partition)
    {
        partition = null!;
        if (offset > (ulong)(stream.Length - runIn))
        {
            return false;
        }

        try
        {
            partition = MxfPartition.Read(stream, runIn + (long)offset);
        }
        catch (Exception e)
        {
            // No exception filter: compiled to WebAssembly, one broke the method's arguments (ExceptionFilterTests).
            if (!IsNotThere(e))
            {
                throw;
            }

            return false;
        }

        return partition.ThisPartition == offset;
    }
}

/// <summary>How <see cref="MxfPartitions"/> found a file's partitions.</summary>
internal enum MxfPartitionSource
{
    /// <summary>From the random index pack at the end of the file.</summary>
    RandomIndexPack,

    /// <summary>Back from the footer partition by each pack's PreviousPartition.</summary>
    PreviousPartitions,

    /// <summary>By walking every KLV item of the file.</summary>
    Walk,
}
