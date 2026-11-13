using System.Buffers.Binary;
using System.Globalization;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// MXF's KLV coding and partitions (SMPTE ST 377-1 6.3, 7, 12): every samples/*.mxf against the partition packs FFmpeg's
/// demuxer reads (samples/*.partitions.csv), each of the three ways of finding them against the others, and the KLV
/// rules on hand-made bytes.
/// </summary>
public class MxfPartitionTests
{
    public static TheoryData<string> Samples() =>
        new(TestSamples.Directory is null
            ? Enumerable.Empty<string>()
            : Directory.EnumerateFiles(TestSamples.Directory, "*.mxf").Order()
                .Where(f => File.Exists(Path.ChangeExtension(f, ".partitions.csv")))
                .Select(f => Path.GetFileName(f)));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegsPartitions(string sampleName)
    {
        var expected = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".partitions.csv"))
            .Select(line => line.Split(',').Select(f => ulong.Parse(f, CultureInfo.InvariantCulture)).ToArray())
            .ToList();

        using var stream = File.OpenRead(TestSamples.PathOf(sampleName));
        var file = MxfPartitions.Read(stream);

        Assert.Equal(expected.Count, file.Partitions.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            var p = file.Partitions[i];
            Assert.Equal(expected[i], new ulong[] { p.ThisPartition, p.PreviousPartition, p.FooterPartition, p.IndexSid, p.BodySid });
            Assert.Equal(file.RunIn + (long)p.ThisPartition, p.Position);
            Assert.Equal(1, p.MajorVersion);
            Assert.NotEmpty(p.EssenceContainers);
        }

        Assert.Equal(MxfPartitionKind.Header, file.Header.Kind);
        Assert.All(file.Partitions.Skip(1).SkipLast(1), p => Assert.Equal(MxfPartitionKind.Body, p.Kind));
        Assert.NotNull(file.Footer);
        Assert.True(file.Footer.IsClosed);
        Assert.Equal(0u, file.Footer.BodySid);

        // How each sample's partitions must be found (make-samples.sh says what each one isolates).
        var source = sampleName switch
        {
            _ when sampleName.Contains("pipe-norip", StringComparison.Ordinal) => MxfPartitionSource.Walk,
            _ when sampleName.Contains("norip", StringComparison.Ordinal) => MxfPartitionSource.PreviousPartitions,
            _ => MxfPartitionSource.RandomIndexPack,
        };
        Assert.Equal(source, file.Source);
        Assert.Equal(sampleName.Contains("runin", StringComparison.Ordinal) ? 1024L : 0L, file.RunIn);

        // Written to a pipe, the header partition is left open, its FooterPartition 0.
        if (sampleName.Contains("pipe", StringComparison.Ordinal))
        {
            Assert.False(file.Header.IsClosed);
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void FindsTheSamePartitionsEveryWay(string sampleName)
    {
        using var stream = File.OpenRead(TestSamples.PathOf(sampleName));
        var file = MxfPartitions.Read(stream);
        ulong[] offsets = [.. file.Partitions.Select(p => p.ThisPartition)];

        Assert.Equal(offsets, MxfPartitions.Walk(stream, file.Header).Select(p => p.ThisPartition));
        if (MxfPartitions.TryFromFooter(stream, file.RunIn, file.Header, out var chain))
        {
            Assert.Equal(offsets, chain.Select(p => p.ThisPartition));
        }

        if (MxfPartitions.TryFromRandomIndexPack(stream, file.RunIn, out var listed))
        {
            Assert.Equal(offsets, listed.Select(p => p.ThisPartition));
        }
    }

    [Theory]
    [InlineData(new byte[] { 0x00 }, 0L, 1)]
    [InlineData(new byte[] { 0x7F }, 127L, 1)]
    [InlineData(new byte[] { 0x81, 0x05 }, 5L, 2)] // the long form for a short length, which ST 377-1 6.3.4 allows
    [InlineData(new byte[] { 0x83, 0x01, 0x02, 0x03 }, 0x010203L, 4)]
    [InlineData(new byte[] { 0x88, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, long.MaxValue, 9)]
    public void ReadsBerLengths(byte[] bytes, long expected, int expectedSize)
    {
        Assert.Equal(expected, Klv.ReadLength(new MemoryStream(bytes), out int size));
        Assert.Equal(expectedSize, size);
    }

    [Theory]
    [InlineData(new byte[] { 0x80 })] // unspecified, which MXF does not allow
    [InlineData(new byte[] { 0x89, 0, 0, 0, 0, 0, 0, 0, 0, 1 })] // longer than MXF's nine bytes
    [InlineData(new byte[] { 0x88, 0x80, 0, 0, 0, 0, 0, 0, 0 })] // past long.MaxValue
    public void RejectsBerLengthsMxfDoesNotAllow(byte[] bytes) =>
        Assert.Throws<InvalidDataException>(() => Klv.ReadLength(new MemoryStream(bytes), out _));

    [Fact]
    public void ReportsALengthCutShort() =>
        Assert.Throws<EndOfStreamException>(() => Klv.ReadLength(new MemoryStream([0x84, 0x01, 0x02]), out _));

    [Fact]
    public void IgnoresTheVersionByteOfKeys()
    {
        // The fill item's key as RP 210 has it (version 2) and as earlier encoders wrote it (version 1; ST 377-1 6.3.3).
        Assert.True(Klv.IsFill(Convert.FromHexString("060E2B34010101020301021001000000")));
        Assert.True(Klv.IsFill(Convert.FromHexString("060E2B34010101010301021001000000")));
        Assert.False(Klv.IsFill(Convert.FromHexString("060E2B34010101020301021002000000")));

        Assert.True(Klv.IsPartitionPack(Convert.FromHexString("060E2B34020501FF0D01020101030400"), out var kind, out var status));
        Assert.Equal(MxfPartitionKind.Body, kind);
        Assert.Equal(MxfPartitionStatus.ClosedComplete, status);

        // Another standard's partition (ST 410's generic stream partition has kind 0x03 and status 0x11) is not one of these.
        Assert.False(Klv.IsPartitionPack(Convert.FromHexString("060E2B34020501010D01020101031100"), out _, out _));
    }

    [Fact]
    public void ParsesAPartitionPack()
    {
        byte[] pack = PartitionPack(kind: 3, status: 2, thisPartition: 0x1400, previous: 0, footer: 0x8000, indexSid: 2, bodySid: 1, containers: 2);
        var partition = MxfPartition.Read(new MemoryStream(pack), 0);

        Assert.Equal(MxfPartitionKind.Body, partition.Kind);
        Assert.True(partition.IsClosed);
        Assert.False(partition.IsComplete);
        Assert.Equal((ushort)1, partition.MajorVersion);
        Assert.Equal((ushort)3, partition.MinorVersion);
        Assert.Equal(512u, partition.KagSize);
        Assert.Equal(0x1400ul, partition.ThisPartition);
        Assert.Equal(0x8000ul, partition.FooterPartition);
        Assert.Equal(2u, partition.IndexSid);
        Assert.Equal(1u, partition.BodySid);
        Assert.Equal(2, partition.EssenceContainers.Count);
        Assert.Equal(0xC1, partition.EssenceContainers[1][15]);
        Assert.Equal(pack.Length, partition.End);
    }

    [Fact]
    public void RejectsPartitionPacksThatDoNotHoldTogether()
    {
        // A batch whose items are not 16-byte labels, one that lists more than the pack holds, and a pack too short.
        byte[] wrongItems = PartitionPack(containers: 1);
        BinaryPrimitives.WriteUInt32BigEndian(wrongItems.AsSpan(PackValueStart + 84), 15);
        byte[] tooMany = PartitionPack(containers: 1);
        BinaryPrimitives.WriteUInt32BigEndian(tooMany.AsSpan(PackValueStart + 80), 2);
        byte[] tooShort = PartitionPack(containers: 0)[..^1];
        tooShort[PackValueStart - 1] = (byte)(tooShort.Length - PackValueStart);

        Assert.Throws<InvalidDataException>(() => MxfPartition.Read(new MemoryStream(wrongItems), 0));
        Assert.Throws<InvalidDataException>(() => MxfPartition.Read(new MemoryStream(tooMany), 0));
        Assert.Throws<InvalidDataException>(() => MxfPartition.Read(new MemoryStream(tooShort), 0));
        Assert.Throws<EndOfStreamException>(() => MxfPartition.Read(new MemoryStream(PartitionPack()[..60]), 0));
    }

    [Fact]
    public void FallsBackWhenTheRandomIndexPackDoesNotHoldUp()
    {
        // A header and a footer partition, then a random index pack that lists a body partition at an offset where none is.
        byte[] header = PartitionPack(kind: 2, status: 4, thisPartition: 0, footer: 0);
        byte[] footer = PartitionPack(kind: 4, status: 4, thisPartition: (ulong)header.Length, footer: (ulong)header.Length);
        byte[] rip = RandomIndexPack((0, 0), (8, 1), ((ulong)header.Length, 0));
        var file = MxfPartitions.Read(new MemoryStream([.. header, .. footer, .. rip]));

        Assert.Equal(MxfPartitionSource.Walk, file.Source);
        Assert.Equal(new[] { 0ul, (ulong)header.Length }, file.Partitions.Select(p => p.ThisPartition));

        // One with the right offsets but a BodySID that is not the partition's.
        var wrongSid = MxfPartitions.Read(new MemoryStream([.. header, .. footer, .. RandomIndexPack((0, 1), ((ulong)header.Length, 0))]));
        Assert.Equal(MxfPartitionSource.Walk, wrongSid.Source);

        // The same random index pack, true this time.
        var listed = MxfPartitions.Read(new MemoryStream([.. header, .. footer, .. RandomIndexPack((0, 0), ((ulong)header.Length, 0))]));
        Assert.Equal(MxfPartitionSource.RandomIndexPack, listed.Source);
        Assert.Equal(2, listed.Partitions.Count);
    }

    [Fact]
    public void FollowsPreviousPartitionsOnlyBackwards()
    {
        // The header gives the footer's offset and there is no random index pack: the chain back from the footer.
        byte[] header = PartitionPack(kind: 2, status: 4, thisPartition: 0, footer: 0);
        ulong footerAt = (ulong)(2 * header.Length);
        header = PartitionPack(kind: 2, status: 4, thisPartition: 0, footer: footerAt);
        byte[] body = PartitionPack(kind: 3, status: 4, thisPartition: (ulong)header.Length, previous: 0, footer: footerAt, bodySid: 1);
        byte[] footer = PartitionPack(kind: 4, status: 4, thisPartition: footerAt, previous: (ulong)header.Length, footer: footerAt);
        var file = MxfPartitions.Read(new MemoryStream([.. header, .. body, .. footer]));
        Assert.Equal(MxfPartitionSource.PreviousPartitions, file.Source);
        Assert.Equal(new[] { MxfPartitionKind.Header, MxfPartitionKind.Body, MxfPartitionKind.Footer }, file.Partitions.Select(p => p.Kind));

        // A footer that names itself as the previous partition would loop: the walk finds the partitions instead.
        byte[] looping = PartitionPack(kind: 4, status: 4, thisPartition: footerAt, previous: footerAt, footer: footerAt);
        var walked = MxfPartitions.Read(new MemoryStream([.. header, .. body, .. looping]));
        Assert.Equal(MxfPartitionSource.Walk, walked.Source);
        Assert.Equal(3, walked.Partitions.Count);
    }

    [Fact]
    public void FindsTheHeaderPastARunInOnly()
    {
        byte[] header = PartitionPack(kind: 2, status: 4, thisPartition: 0);
        Assert.True(MxfPartitions.Probe(new MemoryStream(header)));
        Assert.True(MxfPartitions.Probe(new MemoryStream([.. new byte[65535], .. header])));

        // A run-in of 64 KiB or more is not one (ST 377-1 6.6), and the first partition must be the header's.
        Assert.False(MxfPartitions.Probe(new MemoryStream([.. new byte[65536], .. header])));
        Assert.False(MxfPartitions.Probe(new MemoryStream(PartitionPack(kind: 3, status: 4))));
        Assert.False(MxfPartitions.Probe(new MemoryStream("RIFF\0\0\0\0AVI "u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => MxfPartitions.Read(new MemoryStream(new byte[100])));
    }

    [Fact]
    public void EndsAWalkAtAnItemCutShort()
    {
        // No random index pack and no footer offset: the walk meets a KLV item longer than what is left of the file
        // (a file cut short, or still being written), or a key or length the file breaks off in, and ends there.
        byte[] header = PartitionPack(kind: 2, status: 1, thisPartition: 0);
        byte[] key = Convert.FromHexString("060E2B34010201010D01030115010501");
        foreach (byte[] item in new byte[][] { [.. key, 0x83, 0x01, 0x00, 0x00, 1, 2, 3], [.. key, 0x83, 0x01], key[..8] })
        {
            var file = MxfPartitions.Read(new MemoryStream([.. header, .. item]));
            Assert.Equal(MxfPartitionSource.Walk, file.Source);
            Assert.Single(file.Partitions);
        }
    }

    // The key (16) and a one-byte BER length come before a pack's value in PartitionPack's packs.
    private const int PackValueStart = 17;

    private static byte[] PartitionPack(byte kind = 2, byte status = 4, ulong thisPartition = 0, ulong previous = 0, ulong footer = 0, uint indexSid = 0, uint bodySid = 0, int containers = 1)
    {
        var value = new byte[88 + (16 * containers)];
        BinaryPrimitives.WriteUInt16BigEndian(value, 1);
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(2), 3);
        BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(4), 512);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(8), thisPartition);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(16), previous);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(24), footer);
        BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(48), indexSid);
        BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(60), bodySid);
        BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(80), (uint)containers);
        BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(84), 16);
        for (int i = 0; i < containers; i++)
        {
            value[88 + (16 * i) + 15] = (byte)(0xC0 + i);
        }

        byte[] key = Convert.FromHexString("060E2B34020501010D01020101000000");
        key[13] = kind;
        key[14] = status;
        return [.. key, (byte)value.Length, .. value];
    }

    private static byte[] RandomIndexPack(params (ulong Offset, uint BodySid)[] entries)
    {
        int length = (12 * entries.Length) + 4;
        var value = new byte[length];
        for (int i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(12 * i), entries[i].BodySid);
            BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan((12 * i) + 4), entries[i].Offset);
        }

        BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(length - 4), (uint)(16 + 1 + length));
        return [.. Convert.FromHexString("060E2B34020501010D01020101110100"), (byte)length, .. value];
    }
}
