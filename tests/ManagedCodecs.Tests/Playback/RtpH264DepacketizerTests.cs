using System.Buffers.Binary;
using System.Text.RegularExpressions;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// RtpH264Depacketizer against FFmpeg's packetizer: the RTP that FFmpeg sends for an MP4's H.264 (make-samples.sh,
/// tools/rtp-record.py) must give back the MP4's samples, NAL unit for NAL unit, and decode to the same frames. Then
/// hand-built packets for what FFmpeg never sends: lost packets, cut fragments, a sequence number's wrap, padding and
/// header extensions, the interleaved mode's packet types (docs/decisions/2026-11-10-live-rtsp-h264.md).
/// </summary>
public class RtpH264DepacketizerTests
{
    // h264-main-slices: STAP-A (parameter sets and small slices), single NAL units and FU-A; h264-bench-720p: FU-A.
    public static TheoryData<string> Recordings() => ["h264-main-slices", "h264-bench-720p"];

    [Theory]
    [MemberData(nameof(Recordings))]
    public void GivesBackTheMp4sAccessUnits(string name)
    {
        var (expected, keyframes, parameterSets) = Mp4AccessUnits(name);
        var units = Depacketize(name, out long lost);

        Assert.Equal(0, lost);
        Assert.Equal(expected.Count, units.Count);
        for (int i = 0; i < units.Count; i++)
        {
            var nals = i == 0 ? [.. parameterSets, .. expected[i]] : expected[i];
            Assert.Equal(nals, SplitAnnexB(units[i].Data));
            Assert.False(units[i].Damaged, $"Access unit {i} is marked damaged.");
            Assert.Equal(keyframes[i], units[i].HasIdr);
        }

        Assert.Equal(units.Count, units.Select(u => u.Timestamp).Distinct().Count());
    }

    [Fact]
    public void DecodesToTheMp4sFrames()
    {
        var expected = new List<string>();
        using (var demuxer = Demuxer.Open(TestSamples.PathOf("h264-main-slices.mp4")))
        {
            var decoder = new H264Decoder(demuxer.Video!);
            foreach (var packet in demuxer.ReadPackets())
            {
                expected.AddRange(decoder.Decode(packet).Select(Hash));
            }

            expected.AddRange(decoder.Flush().Select(Hash));
        }

        var annexB = new H264Decoder(ReadOnlySpan<byte>.Empty);
        var frames = new List<string>();
        long pts = 0;
        foreach (var unit in Depacketize("h264-main-slices", out _))
        {
            frames.AddRange(annexB.Decode(unit.Data, pts++).Select(Hash));
        }

        frames.AddRange(annexB.Flush().Select(Hash));
        Assert.Equal(expected.Count, frames.Count);
        Assert.Equal(expected, frames);
    }

    [Fact]
    public void LostPacketsGiveDamagedFramesTheDecoderConceals()
    {
        // Every 37th packet of the 720p recording lost: FU-A fragments, so NAL units go missing whole.
        var packets = Recording("h264-bench-720p");
        var depacketizer = new RtpH264Depacketizer(96, SdpParameterSets("h264-bench-720p"));
        var units = new List<RtpAccessUnit>();
        for (int i = 0; i < packets.Count; i++)
        {
            if (i % 37 != 36)
            {
                depacketizer.Add(packets[i], units);
            }
        }

        if (depacketizer.Flush() is { } last)
        {
            units.Add(last);
        }

        Assert.Equal(packets.Count / 37, depacketizer.LostPackets);
        Assert.Contains(units, u => u.Damaged);
        Assert.Contains(units, u => !u.Damaged);

        var decoder = new H264Decoder(ReadOnlySpan<byte>.Empty) { ConcealErrors = true };
        var frames = new List<VideoFrame>();
        long pts = 0;
        foreach (var unit in units)
        {
            // A unit whose every slice lost a fragment has no picture left: skipped, the next picture's frame_num gap
            // tells the decoder one is missing.
            if (unit.HasPicture)
            {
                frames.AddRange(decoder.Decode(unit.Data, pts++));
            }
        }

        frames.AddRange(decoder.Flush());
        Assert.Contains(units, u => !u.HasPicture);
        Assert.InRange(frames.Count, units.Count(u => u.HasPicture), 50);
        Assert.Contains(frames, f => f.IsDamaged);
    }

    [Fact]
    public void ReadsSingleNalUnitsStapAAndFuA()
    {
        byte[] sps = [0x67, 1, 2, 3];
        byte[] pps = [0x68, 4];
        byte[] idr = [0x65, .. Enumerable.Range(0, 300).Select(i => (byte)i)];
        byte[] sei = [0x06, 9, 9];
        var units = Run(
            Packet(1, 1000, false, [0x78, 0, (byte)sps.Length, .. sps, 0, (byte)pps.Length, .. pps]), // STAP-A
            Packet(2, 1000, false, sei),
            Packet(3, 1000, false, [0x7C, 0x85, .. idr[1..100]]), // FU-A start: NRI 3, type 5
            Packet(4, 1000, false, [0x7C, 0x05, .. idr[100..200]]),
            Packet(5, 1000, true, [0x7C, 0x45, .. idr[200..]])); // FU-A end
        var unit = Assert.Single(units);
        Assert.Equal([sps, pps, sei, idr], SplitAnnexB(unit.Data));
        Assert.False(unit.Damaged);
        Assert.True(unit.HasIdr);
        Assert.True(unit.HasPicture);
        Assert.Equal(1000u, unit.Timestamp);
    }

    [Fact]
    public void PutsTheSdpsParameterSetsBeforeTheFirstUnitOnly()
    {
        var parameterSets = RtpH264Depacketizer.ParseParameterSets("Z0LAH9kAUAW7ARAAAAMAEAAAAwMg8YMkgA==,aMuMsg==");
        Assert.Equal(2, parameterSets.Length);
        Assert.Equal(0x67, parameterSets[0][0]);
        Assert.Equal(0x68, parameterSets[1][0]);

        var depacketizer = new RtpH264Depacketizer(96, parameterSets);
        var units = new List<RtpAccessUnit>();
        depacketizer.Add(Packet(1, 0, true, [0x65, 1]), units);
        depacketizer.Add(Packet(2, 3000, true, [0x41, 2]), units);
        Assert.Equal([.. parameterSets, [0x65, 1]], SplitAnnexB(units[0].Data));
        Assert.Equal([[0x41, 2]], SplitAnnexB(units[1].Data));
    }

    [Fact]
    public void DropsAFragmentedNalUnitMissingAPieceAndKeepsTheRest()
    {
        var depacketizer = new RtpH264Depacketizer();
        var units = new List<RtpAccessUnit>();
        depacketizer.Add(Packet(10, 0, false, [0x61, 7]), units); // a whole slice
        depacketizer.Add(Packet(11, 0, false, [0x7C, 0x81, 1, 2]), units); // FU-A start ...
        depacketizer.Add(Packet(13, 0, false, [0x7C, 0x41, 5, 6]), units); // ... and end: 12 was lost
        depacketizer.Add(Packet(14, 0, true, [0x61, 8]), units);

        var unit = Assert.Single(units);
        Assert.Equal([[0x61, 7], [0x61, 8]], SplitAnnexB(unit.Data));
        Assert.True(unit.Damaged);
        Assert.Equal(1, depacketizer.LostPackets);
    }

    [Fact]
    public void IgnoresFragmentsWhoseStartWasLost()
    {
        var units = Run(
            Packet(1, 0, false, [0x7C, 0x01, 1, 2]), // a middle fragment, no start before it
            Packet(2, 0, true, [0x7C, 0x41, 3, 4]));
        var unit = Assert.Single(units);
        Assert.Empty(unit.Data);
        Assert.True(unit.Damaged);
        Assert.False(unit.HasPicture);
    }

    [Fact]
    public void EndsAUnitAtANewTimestampWhenItsMarkerWasLost()
    {
        var depacketizer = new RtpH264Depacketizer();
        var units = new List<RtpAccessUnit>();
        depacketizer.Add(Packet(1, 0, false, [0x65, 1]), units);
        depacketizer.Add(Packet(3, 3000, true, [0x41, 2]), units); // 2, the first unit's marker packet, was lost

        Assert.Equal(2, units.Count);
        Assert.Equal((0u, true), (units[0].Timestamp, units[0].Damaged));

        // The gap may have held the second unit's start, too.
        Assert.Equal((3000u, true), (units[1].Timestamp, units[1].Damaged));
    }

    [Fact]
    public void AGapBetweenUnitsDamagesOnlyTheNextOne()
    {
        var units = Run(
            Packet(1, 0, true, [0x65, 1]),
            Packet(3, 3000, true, [0x41, 2]),
            Packet(4, 6000, true, [0x41, 3]));
        Assert.Equal([false, true, false], units.Select(u => u.Damaged));
    }

    [Fact]
    public void FollowsTheSequenceNumberAcrossItsWrap()
    {
        var depacketizer = new RtpH264Depacketizer();
        var units = new List<RtpAccessUnit>();
        depacketizer.Add(Packet(65534, 0, true, [0x65, 1]), units);
        depacketizer.Add(Packet(65535, 3000, true, [0x41, 2]), units);
        depacketizer.Add(Packet(0, 6000, true, [0x41, 3]), units);
        depacketizer.Add(Packet(2, 9000, true, [0x41, 4]), units);

        Assert.Equal(4, units.Count);
        Assert.Equal([false, false, false, true], units.Select(u => u.Damaged));
        Assert.Equal(1, depacketizer.LostPackets);
    }

    [Fact]
    public void IgnoresARepeatedPacket()
    {
        var units = Run(
            Packet(5, 0, false, [0x61, 1]),
            Packet(5, 0, false, [0x61, 1]),
            Packet(6, 0, true, [0x61, 2]));
        Assert.Equal([[0x61, 1], [0x61, 2]], SplitAnnexB(Assert.Single(units).Data));
        Assert.False(units[0].Damaged);
    }

    [Fact]
    public void ReadsPastCsrcsAHeaderExtensionAndPadding()
    {
        // Two CSRCs, a one-word extension, and three bytes of padding.
        byte[] packet =
        [
            0xB2, 0x80 | 96, 0, 1, 0, 0, 0, 7, 1, 2, 3, 4, // V=2, P, X, CC=2; M, PT 96; seq 1; ts 7; SSRC
            0, 0, 0, 1, 0, 0, 0, 2, // the CSRCs
            0xBE, 0xDE, 0, 1, 9, 9, 9, 9, // the extension: profile, one word, the word
            0x65, 0xAA, 0xBB, // the payload
            0, 0, 3, // padding, its last byte counting it
        ];
        var unit = Assert.Single(Run(packet));
        Assert.Equal([[0x65, 0xAA, 0xBB]], SplitAnnexB(unit.Data));
        Assert.Equal(7u, unit.Timestamp);
    }

    [Fact]
    public void SkipsOtherPayloadTypes()
    {
        var depacketizer = new RtpH264Depacketizer(96);
        var units = new List<RtpAccessUnit>();
        depacketizer.Add(Packet(1, 0, true, [0x65, 1], payloadType: 97), units);
        depacketizer.Add(Packet(2, 0, true, [0x65, 2]), units);
        Assert.Equal([[0x65, 2]], SplitAnnexB(Assert.Single(units).Data));
    }

    [Theory]
    [InlineData(new byte[] { 0x78, 0, 0, 0, 2, 0x61, 1 })] // STAP-A with a unit of size 0
    [InlineData(new byte[] { 0x78, 0, 9, 0x61, 1 })] // STAP-A with a unit past its end
    [InlineData(new byte[] { 0x79, 0, 0, 0, 2, 0x61, 1 })] // STAP-B: the interleaved mode's
    [InlineData(new byte[] { 0x7D, 0x81, 0, 0, 1 })] // FU-B: the interleaved mode's
    [InlineData(new byte[] { 0x7C, 0x81 })] // FU-A without a piece
    [InlineData(new byte[0])] // an empty payload
    public void MarksWhatItCantReadAsDamaged(byte[] payload)
    {
        Assert.True(Assert.Single(Run(Packet(1, 0, true, payload))).Damaged);
    }

    [Fact]
    public void TreatsWhatIsntRtpAsLost()
    {
        var units = Run(
            Packet(1, 0, false, [0x61, 1]),
            [0x40, 0, 0], // cut short, and RTP version 1
            Packet(2, 0, true, [0x61, 2]));
        Assert.True(Assert.Single(units).Damaged);
    }

    [Fact]
    public void FlushGivesTheUnitInProgressAsDamaged()
    {
        var depacketizer = new RtpH264Depacketizer();
        var units = new List<RtpAccessUnit>();
        depacketizer.Add(Packet(1, 0, false, [0x65, 1]), units);
        Assert.Empty(units);
        var unit = depacketizer.Flush();
        Assert.NotNull(unit);
        Assert.True(unit.Damaged);
        Assert.Null(depacketizer.Flush());
    }

    internal static List<byte[]> Recording(string name)
    {
        var data = File.ReadAllBytes(TestSamples.PathOf(name + ".rtp"));
        var packets = new List<byte[]>();
        for (int at = 0; at < data.Length;)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
            packets.Add(data.AsSpan(at + 2, length).ToArray());
            at += 2 + length;
        }

        return packets;
    }

    internal static byte[][] SdpParameterSets(string name)
    {
        var sdp = File.ReadAllText(TestSamples.PathOf(name + ".sdp"));
        return RtpH264Depacketizer.ParseParameterSets(Regex.Match(sdp, "sprop-parameter-sets=([^;\\s]+)").Groups[1].Value);
    }

    private static List<RtpAccessUnit> Depacketize(string name, out long lost)
    {
        var depacketizer = new RtpH264Depacketizer(96, SdpParameterSets(name));
        var units = new List<RtpAccessUnit>();
        foreach (var packet in Recording(name))
        {
            depacketizer.Add(packet, units);
        }

        Assert.Null(depacketizer.Flush());
        lost = depacketizer.LostPackets;
        return units;
    }

    private static (List<byte[][]> Units, List<bool> Keyframes, byte[][] ParameterSets) Mp4AccessUnits(string name)
    {
        using var demuxer = Demuxer.Open(TestSamples.PathOf(name + ".mp4"));
        var avcC = demuxer.Video!.CodecPrivate.Span;
        int lengthSize = (avcC[4] & 3) + 1;

        // The avcC's SPS and PPS, as FFmpeg's SDP gives them (one SPS and one PPS in these samples).
        int spsLength = BinaryPrimitives.ReadUInt16BigEndian(avcC[6..]);
        var sps = avcC.Slice(8, spsLength).ToArray();
        int ppsAt = 8 + spsLength + 1;
        var pps = avcC.Slice(ppsAt + 2, BinaryPrimitives.ReadUInt16BigEndian(avcC[ppsAt..])).ToArray();

        var units = new List<byte[][]>();
        var keyframes = new List<bool>();
        foreach (var packet in demuxer.ReadPackets())
        {
            var data = packet.Data.Span;
            var nals = new List<byte[]>();
            for (int at = 0; at < data.Length;)
            {
                int length = 0;
                for (int i = 0; i < lengthSize; i++)
                {
                    length = (length << 8) | data[at + i];
                }

                nals.Add(data.Slice(at + lengthSize, length).ToArray());
                at += lengthSize + length;
            }

            units.Add([.. nals]);
            keyframes.Add(nals.Any(n => (n[0] & 0x1F) == 5));
        }

        return (units, keyframes, [sps, pps]);
    }

    // The NAL units of an Annex B buffer whose every NAL unit is behind a 4-byte start code (the depacketizer's).
    private static byte[][] SplitAnnexB(byte[] data)
    {
        var starts = new List<int>();
        for (int i = 0; i + 4 <= data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
            {
                starts.Add(i + 4);
                i += 3;
            }
        }

        return starts.Select((start, k) => data[start..(k + 1 < starts.Count ? starts[k + 1] - 4 : data.Length)]).ToArray();
    }

    // The visible samples' MD5, as FFmpeg's framemd5 hashes them, the frame then given back.
    private static string Hash(VideoFrame frame)
    {
        using (frame)
        {
            return FrameMd5.Hash(frame);
        }
    }

    private static List<RtpAccessUnit> Run(params byte[][] packets)
    {
        var depacketizer = new RtpH264Depacketizer();
        var units = new List<RtpAccessUnit>();
        foreach (var packet in packets)
        {
            depacketizer.Add(packet, units);
        }

        return units;
    }

    private static byte[] Packet(ushort sequence, uint timestamp, bool marker, byte[] payload, int payloadType = 96) =>
    [
        0x80, (byte)((marker ? 0x80 : 0) | payloadType), (byte)(sequence >> 8), (byte)sequence,
        (byte)(timestamp >> 24), (byte)(timestamp >> 16), (byte)(timestamp >> 8), (byte)timestamp,
        0x12, 0x34, 0x56, 0x78,
        .. payload,
    ];
}
