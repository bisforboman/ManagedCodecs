using ManagedCodecs.Containers;
using ManagedCodecs.Mjpeg;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// RFC 2435's depacketizer against FFmpeg's and GStreamer's packetizers: each JPEG it rebuilds must decode to exactly
/// what the JPEG that was packetized decodes to (docs/decisions/2026-11-12-live-rtsp-jpeg.md). FFmpeg's recording is
/// type 1; GStreamer's, committed in samples/gstreamer/ (tools/gst-jpeg-record.py), are types 0, 64 and 65. Both send Q
/// 255 (tables in every frame): Q below 128 and reused tables are those recordings rewritten.
/// </summary>
public class RtpJpegDepacketizerTests
{
    public static TheoryData<string> GStreamerRecordings() => ["jpeg-422", "jpeg-420-restart", "jpeg-422-restart"];

    [Fact]
    public void GivesBackFfmpegsJpegs()
    {
        // FFmpeg's packetizer on testsrc-stdhuff.avi's JPEGs as they are (-c copy, make-samples.sh).
        using var demuxer = Demuxer.Open(TestSamples.PathOf("testsrc-stdhuff.avi"));
        var expected = demuxer.ReadPackets().Select(p => Hash(p.Data.Span)).ToList();
        var frames = Depacketize(RtpH264DepacketizerTests.Recording("testsrc-stdhuff"), out long lost);

        Assert.Equal(0, lost);
        Assert.Equal(expected, frames.Select(f => Hash(f.Jpeg!)));
    }

    [Theory]
    [MemberData(nameof(GStreamerRecordings))]
    public void GivesBackGStreamersJpegs(string name)
    {
        var frames = Depacketize(GStreamer(name), out long lost);

        Assert.Equal(0, lost);
        Assert.Equal(Sources(name), frames.Select(f => Hash(f.Jpeg!)));
    }

    [Fact]
    public void GivesBackGStreamersJpegsWiderThanTheHeaderCanSay()
    {
        // 2048x64: GStreamer's packetizer sends the size as 0 and its RTSP server puts it in the SDP
        // (a=x-dimensions:2048,64; samples/gstreamer/jpeg-large.sdp is its answer to DESCRIBE).
        var packets = GStreamer("jpeg-large");
        var track = ManagedCodecs.Playback.Rtsp.Sdp.Parse(File.ReadAllText(TestSamples.PathOf(Path.Combine("gstreamer", "jpeg-large.sdp")))).Track!;
        Assert.Equal((0, 0), (packets[0][12 + 6], packets[0][12 + 7]));
        Assert.Equal((ManagedCodecs.Playback.Rtsp.SdpCodec.Jpeg, 26, 2048, 64), (track.Codec, track.PayloadType, track.Width, track.Height));

        var depacketizer = new RtpJpegDepacketizer(track.PayloadType, track.Width, track.Height);
        var frames = new List<RtpJpegFrame>();
        foreach (var packet in packets)
        {
            depacketizer.Add(packet, frames);
        }

        Assert.Equal(0, depacketizer.LostPackets);
        Assert.Equal(Sources("jpeg-large"), frames.Select(f => Hash(f.Jpeg!)));

        // With no size from anywhere, no frame can be rebuilt.
        Assert.Throws<NotSupportedException>(() => new RtpJpegDepacketizer().Add(packets[0], []));
    }

    [Theory]
    [InlineData(0xFFD8, 0)] // the extension starts the frame's JPEG header
    [InlineData(0xFFFF, 0)] // or goes on with it
    [InlineData(0xFFD8, 5)] // padded with 0xFF before the segments too
    public void TakesALargeFramesSizeFromOnvifsHeaderExtension(int profile, int padding)
    {
        // ONVIF's way for JPEG over 2040 pixels (hand-made: no sender in reach writes it): the size as 0 in RFC 2435's
        // header, and the JPEG's SOF0 segment, among others, in an RTP header extension on the frame's first packet.
        // The SDP gives no size here, and an SDP's wrong one loses to the extension's.
        byte[] segments =
        [
            .. Enumerable.Repeat((byte)0xFF, padding),
            0xFF, 0xDB, 0, 3, 0, // another segment before it, skipped by its length
            0xFF, 0xC0, 0, 17, 8, 0, 64, 2048 >> 8, 2048 & 0xFF, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
        ];
        var packets = GStreamer("jpeg-large").Select(p => Offset(p) == 0 ? WithExtension(p, profile, segments) : p).ToList();
        foreach (var (width, height) in new[] { (0, 0), (640, 480) })
        {
            var depacketizer = new RtpJpegDepacketizer(26, width, height);
            var frames = new List<RtpJpegFrame>();
            foreach (var packet in packets)
            {
                depacketizer.Add(packet, frames);
            }

            Assert.Equal(Sources("jpeg-large"), frames.Select(f => Hash(f.Jpeg!)));
        }
    }

    [Theory]
    [InlineData(0xBEDE, new byte[] { 0xFF, 0xC0, 0, 17, 8, 0, 64, 8, 0, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 })] // another extension (RFC 8285's)
    [InlineData(0xFFD8, new byte[] { 0xFF, 0xDB, 0, 3, 0 })] // ONVIF's, with no SOF0
    [InlineData(0xFFD8, new byte[] { 0xFF, 0xC0, 0, 17, 8, 0 })] // a SOF0 cut short
    [InlineData(0xFFD8, new byte[] { 1, 2, 3, 4 })] // not marker segments
    public void RefusesALargeFrameWhoseExtensionGivesNoSize(int profile, byte[] segments)
    {
        var first = WithExtension(GStreamer("jpeg-large")[0], profile, segments);
        Assert.Throws<NotSupportedException>(() => new RtpJpegDepacketizer().Add(first, []));
    }

    [Theory]
    [InlineData("a=x-dimensions:2560,1440\r\n", 2560, 1440)] // GStreamer's
    [InlineData("a=framesize:26 2592-1944\r\n", 2592, 1944)] // RFC 6064's
    [InlineData("a=x-dimensions:2560,1440\r\na=framesize:26 2592-1944\r\n", 2592, 1944)]
    [InlineData("a=framesize:96 2592-1944\r\n", 0, 0)] // another payload type's
    [InlineData("a=x-dimensions:2560\r\n", 0, 0)]
    [InlineData("a=x-dimensions:0,1440\r\n", 0, 0)]
    [InlineData("a=x-dimensions:2560,70000\r\n", 0, 0)]
    [InlineData("a=framesize:26 2592x1944\r\n", 0, 0)]
    [InlineData("", 0, 0)]
    public void ReadsAJpegTracksSizeFromTheSdp(string attributes, int width, int height)
    {
        var track = ManagedCodecs.Playback.Rtsp.Sdp.Parse("v=0\r\ns=-\r\nt=0 0\r\nm=video 0 RTP/AVP 26\r\n" + attributes + "a=control:trackID=1\r\n").Track!;
        Assert.Equal((width, height), (track.Width, track.Height));
    }

    [Fact]
    public void ComputesTheTablesForQBelow128AsLibjpegDoes()
    {
        // Pillow's quality 50 is libjpeg's scaling, the one RFC 2435 Appendix A gives, and at 50 it leaves K.1's tables
        // as they are. The recording rewritten to Q 50, its in-band tables taken out, must give the same frames.
        var source = File.ReadAllBytes(SourcePath("jpeg-422", 0));
        Assert.Equal(Dqt(source), RtpJpegDepacketizer.ComputedTables(50));

        var packets = GStreamer("jpeg-422").Select(p => WithQ(p, 50)).ToList();
        Assert.Equal(Sources("jpeg-422"), Depacketize(packets, out _).Select(f => Hash(f.Jpeg!)));
    }

    [Theory]
    [InlineData(1, 0, 255)] // 5000 % at Q 1: K.1's 16 x 50 clamped to 255
    [InlineData(25, 0, 32)] // 200 %
    [InlineData(99, 0, 1)] // 2 %: 16 x 0.02 rounds to 0, kept at 1
    [InlineData(75, 63, 50)] // 50 %: the last zig-zag entry is K.1's 99
    public void ScalesK1sTablesAsAppendixADoes(int q, int index, int expected) =>
        Assert.Equal(expected, RtpJpegDepacketizer.ComputedTables(q)[index]);

    [Fact]
    public void ReusesTablesSentOnceForTheSameQ()
    {
        // Q 128 to 254: a frame may send a table length of 0 and use the tables an earlier frame with that Q sent.
        var packets = GStreamer("jpeg-422");
        bool first = true;
        var rewritten = new List<byte[]>();
        foreach (var packet in packets)
        {
            var p = (byte[])packet.Clone();
            p[12 + 5] = 200;
            if (Offset(p) == 0 && !first)
            {
                p = WithoutTables(p);
            }

            first &= Offset(p) != 0;
            rewritten.Add(p);
        }

        Assert.Equal(Sources("jpeg-422"), Depacketize(rewritten, out _).Select(f => Hash(f.Jpeg!)));

        // Q 255 says the tables change every frame: none is kept, so a length of 0 there drops the frame.
        var frames = Depacketize(packets.Select(p => Offset(p) == 0 ? WithoutTables(p) : p).ToList(), out _);
        Assert.All(frames, f => Assert.True(f.Damaged));
    }

    [Fact]
    public void DropsAFrameMissingAPacketAndKeepsTheOthers()
    {
        var packets = GStreamer("jpeg-422");
        var starts = Enumerable.Range(0, packets.Count).Where(i => Offset(packets[i]) == 0).ToList();
        Assert.True(starts.Count == 5 && starts.Zip(starts.Skip(1)).All(s => s.Second - s.First >= 3), "Each frame needs three packets or more.");

        // Frame 1 loses its first packet, frame 2 one in the middle, frame 3 its last (with the marker bit).
        var lose = new HashSet<int> { starts[1], starts[2] + 1, starts[4] - 1 };
        var frames = Depacketize(packets.Where((_, i) => !lose.Contains(i)).ToList(), out long lost);

        Assert.Equal(3, lost);
        var sources = Sources("jpeg-422");
        Assert.Equal(5, frames.Count);
        Assert.Equal(sources[0], Hash(frames[0].Jpeg!));
        Assert.True(frames[1].Damaged && frames[2].Damaged && frames[3].Damaged);
        Assert.Equal(sources[4], Hash(frames[4].Jpeg!));
    }

    [Theory]
    [InlineData(2, 255)] // types 2 to 63 are reserved
    [InlineData(66, 255)]
    [InlineData(128, 255)] // dynamic types, defined out of band
    [InlineData(1, 0)] // Q 0 and 100 to 127 are reserved
    [InlineData(1, 100)]
    [InlineData(1, 127)]
    public void RefusesWhatItCantRebuild(int type, int q)
    {
        var packet = (byte[])GStreamer("jpeg-422")[0].Clone();
        packet[12 + 4] = (byte)type;
        packet[12 + 5] = (byte)q;
        Assert.Throws<NotSupportedException>(() => new RtpJpegDepacketizer().Add(packet, []));
    }

    [Fact]
    public void KeepsT81sTablesAsTheMjpegDecoderHasThem()
    {
        Assert.Equal(JpegTables.ZigZag.ToArray(), RtpJpegDepacketizer.ZigZag.ToArray());
        Assert.Equal(JpegTables.DcLuminanceBits.ToArray(), RtpJpegDepacketizer.DcLuminanceBits.ToArray());
        Assert.Equal(JpegTables.DcLuminanceValues.ToArray(), RtpJpegDepacketizer.DcLuminanceValues.ToArray());
        Assert.Equal(JpegTables.DcChrominanceBits.ToArray(), RtpJpegDepacketizer.DcChrominanceBits.ToArray());
        Assert.Equal(JpegTables.DcChrominanceValues.ToArray(), RtpJpegDepacketizer.DcChrominanceValues.ToArray());
        Assert.Equal(JpegTables.AcLuminanceBits.ToArray(), RtpJpegDepacketizer.AcLuminanceBits.ToArray());
        Assert.Equal(JpegTables.AcLuminanceValues.ToArray(), RtpJpegDepacketizer.AcLuminanceValues.ToArray());
        Assert.Equal(JpegTables.AcChrominanceBits.ToArray(), RtpJpegDepacketizer.AcChrominanceBits.ToArray());
        Assert.Equal(JpegTables.AcChrominanceValues.ToArray(), RtpJpegDepacketizer.AcChrominanceValues.ToArray());
    }

    internal static List<byte[]> GStreamer(string name) => RtpH264DepacketizerTests.Recording(Path.Combine("gstreamer", name));

    internal static string SourcePath(string name, int frame) => TestSamples.PathOf(Path.Combine("gstreamer", name, $"{frame:00}.jpg"));

    // The decoded frames' MD5s of the JPEGs GStreamer was given.
    internal static List<string> Sources(string name) =>
        Enumerable.Range(0, 5).Select(i => Hash(File.ReadAllBytes(SourcePath(name, i)))).ToList();

    internal static List<RtpJpegFrame> Depacketize(List<byte[]> packets, out long lost)
    {
        var depacketizer = new RtpJpegDepacketizer();
        var frames = new List<RtpJpegFrame>();
        foreach (var packet in packets)
        {
            depacketizer.Add(packet, frames);
        }

        lost = depacketizer.LostPackets;
        return frames;
    }

    private static string Hash(ReadOnlySpan<byte> jpeg)
    {
        using var frame = new MjpegDecoder().Decode(jpeg);
        return FrameMd5.Hash(frame);
    }

    // The packet with an RTP header extension (RFC 3550, 5.3.1) of that profile, its data padded with 0xFF to whole words.
    private static byte[] WithExtension(byte[] packet, int profile, byte[] data)
    {
        int words = (data.Length + 3) / 4;
        byte[] extension = [(byte)(profile >> 8), (byte)profile, (byte)(words >> 8), (byte)words, .. data, .. Enumerable.Repeat((byte)0xFF, (words * 4) - data.Length)];
        byte[] p = [.. packet.AsSpan(0, 12), .. extension, .. packet.AsSpan(12)];
        p[0] |= 0x10;
        return p;
    }

    private static int Offset(byte[] packet) => (packet[13] << 16) | (packet[14] << 8) | packet[15];

    // A packet with Q set to a value below 128: its first fragment's quantization header goes.
    private static byte[] WithQ(byte[] packet, int q)
    {
        var p = Offset(packet) == 0 ? WithoutHeader(packet) : (byte[])packet.Clone();
        p[12 + 5] = (byte)q;
        return p;
    }

    // The first fragment without its tables: the quantization header's length set to 0.
    private static byte[] WithoutTables(byte[] packet)
    {
        int at = 12 + 8 + (packet[12 + 4] >= 64 ? 4 : 0);
        int length = (packet[at + 2] << 8) | packet[at + 3];
        byte[] p = [.. packet.AsSpan(0, at + 4), .. packet.AsSpan(at + 4 + length)];
        p[at + 2] = 0;
        p[at + 3] = 0;
        return p;
    }

    // The first fragment without its quantization header at all (as for Q below 128).
    private static byte[] WithoutHeader(byte[] packet)
    {
        int at = 12 + 8 + (packet[12 + 4] >= 64 ? 4 : 0);
        int length = (packet[at + 2] << 8) | packet[at + 3];
        return [.. packet.AsSpan(0, at), .. packet.AsSpan(at + 4 + length)];
    }

    // A JPEG's two DQT tables, luma then chroma, in zig-zag order as DQT has them.
    private static byte[] Dqt(byte[] jpeg)
    {
        var tables = new List<byte>();
        for (int at = 2; at + 4 < jpeg.Length && tables.Count < 128;)
        {
            int length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            if (jpeg[at + 1] == 0xDB)
            {
                for (int t = at + 4; t < at + 2 + length; t += 65)
                {
                    tables.AddRange(jpeg.AsSpan(t + 1, 64).ToArray());
                }
            }

            at += 2 + length;
        }

        return [.. tables];
    }
}
