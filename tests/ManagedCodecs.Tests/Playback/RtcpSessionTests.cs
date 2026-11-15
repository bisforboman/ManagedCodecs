using System.Buffers.Binary;
using System.Text;
using ManagedCodecs.Playback.Rtsp;

namespace ManagedCodecs.Tests.Playback;

/// <summary>A receiver report as a test reads it, by a parser of its own (RFC 3550, 6.4.2 and 6.5).</summary>
internal sealed record RtcpReport(uint Ssrc, List<RtcpReportBlock> Blocks, string? Name, uint DescribedSsrc)
{
    /// <summary>Reads a compound packet: a receiver report first, then a source description with a CNAME.</summary>
    public static RtcpReport Parse(byte[] packet)
    {
        Assert.Equal(0, packet.Length % 4);
        var blocks = new List<RtcpReportBlock>();
        Assert.Equal((2, 201), (packet[0] >> 6, packet[1]));
        int count = packet[0] & 0x1F;
        int length = (BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)) + 1) * 4;
        Assert.Equal(8 + (24 * count), length);
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            var block = packet.AsSpan(8 + (24 * i), 24);
            uint lost = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(4));
            blocks.Add(new RtcpReportBlock(
                BinaryPrimitives.ReadUInt32BigEndian(block),
                (int)(lost >> 24),
                ((int)(lost << 8)) >> 8,
                BinaryPrimitives.ReadUInt32BigEndian(block.Slice(8)),
                BinaryPrimitives.ReadUInt32BigEndian(block.Slice(12)),
                BinaryPrimitives.ReadUInt32BigEndian(block.Slice(16)),
                BinaryPrimitives.ReadUInt32BigEndian(block.Slice(20))));
        }

        // The source description: one chunk, whose first item is the CNAME, ended by a zero and padded to 32 bits.
        var description = packet.AsSpan(length);
        Assert.Equal((0x81, 202), (description[0], description[1]));
        Assert.Equal(description.Length, (BinaryPrimitives.ReadUInt16BigEndian(description.Slice(2)) + 1) * 4);
        Assert.Equal(1, description[8]);
        int nameLength = description[9];
        Assert.True(10 + nameLength < description.Length);
        Assert.All(description.Slice(10 + nameLength).ToArray(), b => Assert.Equal(0, b));
        return new RtcpReport(ssrc, blocks, Encoding.ASCII.GetString(description.Slice(10, nameLength)), BinaryPrimitives.ReadUInt32BigEndian(description.Slice(4)));
    }

    /// <summary>A sender report (6.4.1) with no report blocks.</summary>
    public static byte[] SenderReport(uint ssrc, uint ntpSeconds, uint ntpFraction, uint rtpTime)
    {
        var packet = new byte[28];
        packet[0] = 0x80;
        packet[1] = 200;
        packet[3] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ntpSeconds);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), ntpFraction);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), rtpTime);
        return packet;
    }
}

internal sealed record RtcpReportBlock(uint Source, int Fraction, int Lost, uint Highest, uint Jitter, uint LastSenderReport, uint Delay);

/// <summary>
/// RtcpSession against RFC 3550's definitions: the loss counts of A.3 (expected from the sequence numbers, across their
/// wrap; a fraction of each interval's), the interarrival jitter of 6.4.1 (against its formula in floating point), and
/// the echo of the last sender report (LSR and DLSR).
/// </summary>
public class RtcpSessionTests
{
    private const uint Source = 0xCAFE1234;

    [Fact]
    public void ReportsNoStreamBeforeItsFirstPacket()
    {
        var session = new RtcpSession(90000);
        var report = RtcpReport.Parse(session.Build(TimeSpan.FromSeconds(5)));
        Assert.Empty(report.Blocks);
        Assert.Equal((session.Ssrc, session.Ssrc), (report.Ssrc, report.DescribedSsrc));
        Assert.False(string.IsNullOrEmpty(report.Name));

        // Another session is another receiver: its own SSRC and CNAME, both random.
        var other = RtcpReport.Parse(new RtcpSession(90000).Build(TimeSpan.Zero));
        Assert.NotEqual((report.Ssrc, report.Name), (other.Ssrc, other.Name));
    }

    [Fact]
    public void CountsTheLostPacketsAndTheFractionOfEachInterval()
    {
        var session = new RtcpSession(90000);
        foreach (int sequence in Enumerable.Range(10, 10).Where(s => s is not (13 or 14)))
        {
            session.Received(Packet(sequence, 0), TimeSpan.Zero);
        }

        var block = Assert.Single(RtcpReport.Parse(session.Build(TimeSpan.Zero)).Blocks);
        Assert.Equal((Source, 2, 2 * 256 / 10, 19u), (block.Source, block.Lost, block.Fraction, block.Highest));

        // The next interval loses nothing: its fraction is 0 and the count stays.
        foreach (int sequence in Enumerable.Range(20, 10))
        {
            session.Received(Packet(sequence, 0), TimeSpan.Zero);
        }

        block = Assert.Single(RtcpReport.Parse(session.Build(TimeSpan.Zero)).Blocks);
        Assert.Equal((2, 0, 29u), (block.Lost, block.Fraction, block.Highest));

        // An interval with nothing received at all reports no loss (nothing was expected either).
        block = Assert.Single(RtcpReport.Parse(session.Build(TimeSpan.Zero)).Blocks);
        Assert.Equal((2, 0, 29u), (block.Lost, block.Fraction, block.Highest));
    }

    [Fact]
    public void ExtendsTheSequenceNumberPastItsWrapAndTakesReorderingForNoLoss()
    {
        var session = new RtcpSession(90000);
        foreach (int sequence in (int[])[65533, 65535, 65534, 1, 0, 2])
        {
            session.Received(Packet(sequence, 0), TimeSpan.Zero);
        }

        var block = Assert.Single(RtcpReport.Parse(session.Build(TimeSpan.Zero)).Blocks);
        Assert.Equal((0, 0, 0x10002u), (block.Lost, block.Fraction, block.Highest));
    }

    [Fact]
    public void CountsDuplicatesAsReceivedSoThatLossCanBeNegative()
    {
        var session = new RtcpSession(90000);
        foreach (int sequence in (int[])[7, 8, 8, 8, 9])
        {
            session.Received(Packet(sequence, 0), TimeSpan.Zero);
        }

        var block = Assert.Single(RtcpReport.Parse(session.Build(TimeSpan.Zero)).Blocks);
        Assert.Equal((-2, 0, 9u), (block.Lost, block.Fraction, block.Highest));
    }

    [Fact]
    public void MeasuresTheJitterAsRfc3550DefinesIt()
    {
        // Packets a 25th of a second apart by their timestamps, arriving up to 15 ms off that: J += (|D| - J) / 16,
        // D the change in (arrival - timestamp), in timestamp units (6.4.1).
        var random = new Random(3550);
        var session = new RtcpSession(90000);
        double jitter = 0;
        double? last = null;
        for (int i = 0; i < 500; i++)
        {
            double arrival = (i * 0.04) + (random.NextDouble() * 0.015) + 1000;
            uint timestamp = (uint)(0xFFFF0000 + (i * 3600)); // wraps on the way
            session.Received(Packet(i, timestamp), TimeSpan.FromSeconds(arrival));
            double transit = (arrival * 90000) - (i * 3600);
            if (last is { } previous)
            {
                jitter += (Math.Abs(transit - previous) - jitter) / 16;
            }

            last = transit;
        }

        var block = Assert.Single(RtcpReport.Parse(session.Build(TimeSpan.Zero)).Blocks);
        Assert.True(jitter > 100, $"The test's own jitter is {jitter}.");
        Assert.InRange(block.Jitter, jitter - 3, jitter + 3);

        // Evenly spaced arrivals have none, whatever the offset between the two clocks.
        var even = new RtcpSession(8000);
        for (int i = 0; i < 50; i++)
        {
            even.Received(Packet(i, (uint)(i * 160)), TimeSpan.FromSeconds(123456789 + (i * 0.02)));
        }

        Assert.Equal(0u, Assert.Single(RtcpReport.Parse(even.Build(TimeSpan.Zero)).Blocks).Jitter);
    }

    [Fact]
    public void EchoesTheLastSenderReportAndTheTimeSinceIt()
    {
        var session = new RtcpSession(90000);
        session.Received(Packet(1, 0), TimeSpan.Zero);
        var block = Assert.Single(RtcpReport.Parse(session.Build(TimeSpan.FromSeconds(1))).Blocks);
        Assert.Equal((0u, 0u), (block.LastSenderReport, block.Delay));

        // The sender report second in a compound packet, after a packet of another type; the echo is the middle 32
        // bits of its NTP time, and the delay counts in 1/65536 s from its arrival.
        byte[] other = [0x80, 204, 0, 2, 0, 0, 0, 1, (byte)'t', (byte)'e', (byte)'s', (byte)'t'];
        session.Report([.. other, .. RtcpReport.SenderReport(Source, 0x11223344, 0x55667788, 0)], TimeSpan.FromSeconds(1));
        block = Assert.Single(RtcpReport.Parse(session.Build(TimeSpan.FromSeconds(3.5))).Blocks);
        Assert.Equal((0x33445566u, (uint)(2.5 * 65536)), (block.LastSenderReport, block.Delay));

        // A packet cut short, one of another version and a receiver report change nothing.
        session.Report(RtcpReport.SenderReport(Source, 1, 1, 0).AsSpan(0, 20), TimeSpan.FromSeconds(4));
        var version = RtcpReport.SenderReport(Source, 2, 2, 0);
        version[0] = 0x40;
        session.Report(version, TimeSpan.FromSeconds(4));
        var receiver = RtcpReport.SenderReport(Source, 3, 3, 0);
        receiver[1] = 201;
        session.Report(receiver, TimeSpan.FromSeconds(4));
        block = Assert.Single(RtcpReport.Parse(session.Build(TimeSpan.FromSeconds(4.5))).Blocks);
        Assert.Equal((0x33445566u, (uint)(3.5 * 65536)), (block.LastSenderReport, block.Delay));
    }

    [Fact]
    public void KeepsASenderReportThatComesBeforeTheFirstPacket()
    {
        var session = new RtcpSession(90000);
        session.Report(RtcpReport.SenderReport(Source, 0x11223344, 0x55667788, 0), TimeSpan.FromSeconds(1));
        Assert.Empty(RtcpReport.Parse(session.Build(TimeSpan.FromSeconds(1))).Blocks);
        session.Received(Packet(1, 0), TimeSpan.FromSeconds(2));
        var block = Assert.Single(RtcpReport.Parse(session.Build(TimeSpan.FromSeconds(3))).Blocks);
        Assert.Equal((0x33445566u, 2u * 65536), (block.LastSenderReport, block.Delay));
    }

    [Fact]
    public void StartsAgainWhenTheStreamsSsrcChanges()
    {
        var session = new RtcpSession(90000);
        foreach (int sequence in (int[])[100, 105])
        {
            session.Received(Packet(sequence, 0), TimeSpan.Zero);
        }

        session.Report(RtcpReport.SenderReport(Source, 1, 0, 0), TimeSpan.Zero);
        session.Received(Packet(40000, 0, Source + 1), TimeSpan.Zero);
        session.Received(Packet(40001, 0, Source + 1), TimeSpan.Zero);
        var block = Assert.Single(RtcpReport.Parse(session.Build(TimeSpan.FromSeconds(1))).Blocks);
        Assert.Equal((Source + 1, 0, 0, 40001u, 0u, 0u), (block.Source, block.Lost, block.Fraction, block.Highest, block.LastSenderReport, block.Delay));
    }

    [Fact]
    public void IgnoresAPacketTooShortToBeRtp()
    {
        var session = new RtcpSession(90000);
        session.Received(new byte[11], TimeSpan.Zero);
        Assert.Empty(RtcpReport.Parse(session.Build(TimeSpan.Zero)).Blocks);
    }

    private static byte[] Packet(int sequence, uint timestamp, uint ssrc = Source)
    {
        var packet = new byte[16];
        packet[0] = 0x80;
        packet[1] = 96;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);
        return packet;
    }
}
