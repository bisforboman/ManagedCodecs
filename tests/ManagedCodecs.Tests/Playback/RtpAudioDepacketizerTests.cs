using ManagedCodecs.Containers;
using ManagedCodecs.Playback;
using ManagedCodecs.Playback.Rtsp;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// The audio depacketizer (RFC 3640's AAC-hbr and AAC-lbr, RFC 3551's G.711) against FFmpeg's packetizer: the access
/// units it gives back are the file's packets byte for byte, and the SDP's config is the file's AudioSpecificConfig
/// (docs/decisions/2026-11-15-live-rtsp-audio.md). FFmpeg's encoder never writes an access unit larger than a packet,
/// so fragments' rules, AAC-lbr's field sizes and damage are hand-made packets; GStreamer's packetizers, recorded with
/// what went into them (samples/gstreamer/, tools/gst-audio-record.py), send one access unit a packet and access units
/// over several. The SDP's audio track is read here too.
/// </summary>
public class RtpAudioDepacketizerTests
{
    private static readonly SdpAudioTrack Hbr = new(CodecIds.Aac, 97, null, 48000, 2, 13, 3, 3, [0x11, 0x90]);

    [Theory]
    [InlineData("aac-stereo-48k", "aac-stereo-48k.mp4", 3)] // several access units a packet
    [InlineData("aac-stereo-48k-single", "aac-stereo-48k.mp4", 1)] // -max_delay 0: one a packet
    [InlineData("heaac-stereo-44k", "heaac-stereo-44k.mp4", 3)]
    public void GivesBackTheAccessUnitsFfmpegPacketized(string recording, string file, int mostInAPacketAtLeast)
    {
        var track = Track(recording);
        using var demuxer = Demuxer.Open(TestSamples.PathOf(file));
        var expected = demuxer.ReadPackets().Where(p => p.StreamIndex == demuxer.Audio!.StreamIndex).Select(p => p.Data.ToArray()).ToList();

        var depacketizer = new RtpAudioDepacketizer(track);
        var units = new List<byte[]>();
        int most = 0;
        foreach (var packet in RtpH264DepacketizerTests.Recording(recording))
        {
            int before = units.Count;
            depacketizer.Add(packet, units);
            most = Math.Max(most, units.Count - before);
        }

        Assert.Equal((CodecIds.Aac, demuxer.Audio!.SampleRate, demuxer.Audio.Channels), (track.Codec, track.SampleRate, track.Channels));
        Assert.Equal(demuxer.Audio.CodecPrivate.ToArray(), track.Config);
        Assert.Equal((0L, 0L), (depacketizer.LostPackets, depacketizer.Damaged));
        Assert.True(most >= mostInAPacketAtLeast, $"{most} access units in a packet at most.");

        // FFmpeg's muxer sends a packet once it is full, and leaves the access units still waiting at the end of the
        // file unsent: what came is the file's packets from the first on, and most of them.
        Assert.InRange(units.Count, expected.Count * 3 / 4, expected.Count);
        Assert.Equal(expected.Take(units.Count), units);
    }

    [Theory]
    [InlineData("wav-alaw-codes", CodecIds.PcmAlaw, 8, 1)] // the static payload type, no a=rtpmap
    [InlineData("wav-mulaw-codes", CodecIds.PcmMulaw, 97, 2)] // "PCMU/8000/2"
    public void GivesBackTheG711SamplesFfmpegPacketized(string recording, string codec, int payloadType, int channels)
    {
        var track = Track(recording);
        using var demuxer = Demuxer.Open(TestSamples.PathOf(recording + ".wav"));
        var expected = demuxer.ReadPackets().SelectMany(p => p.Data.ToArray()).ToArray();

        var depacketizer = new RtpAudioDepacketizer(track);
        var payloads = new List<byte[]>();
        foreach (var packet in RtpH264DepacketizerTests.Recording(recording))
        {
            depacketizer.Add(packet, payloads);
        }

        Assert.Equal((codec, payloadType, 8000, channels), (track.Codec, track.PayloadType, track.SampleRate, track.Channels));
        Assert.Equal(expected, payloads.SelectMany(p => p).ToArray());

        // Decoded, they are what the file's decode to (checked against FFmpeg's in the PCM decoder's tests).
        var info = new AudioStreamInfo(track.Codec, track.SampleRate, track.Channels, 0, new Rational(1, track.SampleRate), default, 0);
        var fromRtp = new PcmDecoder(info).Decode(payloads.SelectMany(p => p).ToArray());
        var fromFile = new PcmDecoder(demuxer.Audio!).Decode(expected);
        Assert.Equal(fromFile.Samples[0].AsSpan(0, fromFile.SampleCount).ToArray(), fromRtp.Samples[0].AsSpan(0, fromRtp.SampleCount).ToArray());
    }

    [Theory]
    [InlineData("aac-single", 1)] // one access unit a packet, as cameras send
    [InlineData("aac-fragmented", 3)] // mtu=200: every access unit over several packets
    public void GivesBackTheAccessUnitsGStreamerPacketized(string recording, int packetsAnAccessUnitAtLeast)
    {
        // GStreamer's rtpmp4gpay on its AAC encoder's access units, which were recorded beside the packets
        // (tools/gst-audio-record.py): the same access units come back, and the AAC decoder takes them.
        string path = Path.Combine("gstreamer", recording);
        var track = Track(path);
        var expected = Lengthed(File.ReadAllBytes(TestSamples.PathOf(path + ".frames")));
        var packets = RtpH264DepacketizerTests.Recording(path);

        var depacketizer = new RtpAudioDepacketizer(track);
        var units = new List<byte[]>();
        int fewest = int.MaxValue;
        int since = 0;
        foreach (var packet in packets)
        {
            int before = units.Count;
            depacketizer.Add(packet, units);
            since++;
            Assert.InRange(units.Count - before, 0, 1);
            if (units.Count > before)
            {
                fewest = Math.Min(fewest, since);
                since = 0;
            }
        }

        Assert.Equal((CodecIds.Aac, 97, 48000, 2), (track.Codec, track.PayloadType, track.SampleRate, track.Channels));
        Assert.Equal((0L, 0L), (depacketizer.LostPackets, depacketizer.Damaged));
        Assert.True(fewest >= packetsAnAccessUnitAtLeast, $"An access unit came in {fewest} packets.");
        Assert.Equal(expected, units);

        var decoder = AudioPipeline.CreateDecoder(new AudioStreamInfo(track.Codec, track.SampleRate, track.Channels, 0, new Rational(1, track.SampleRate), track.Config, 0));
        Assert.All(units, unit => Assert.InRange(decoder.Decode(unit).SampleCount, 0, 1024));
    }

    [Theory]
    [InlineData("alaw", CodecIds.PcmAlaw, 8)]
    [InlineData("mulaw", CodecIds.PcmMulaw, 0)]
    public void GivesBackTheG711SamplesGStreamerPacketized(string recording, string codec, int payloadType)
    {
        // rtppcmapay and rtppcmupay, described by their static payload types alone: the encoder's bytes come back.
        string path = Path.Combine("gstreamer", recording);
        var track = Track(path);
        var depacketizer = new RtpAudioDepacketizer(track);
        var payloads = new List<byte[]>();
        foreach (var packet in RtpH264DepacketizerTests.Recording(path))
        {
            depacketizer.Add(packet, payloads);
        }

        Assert.Equal((codec, payloadType, 8000, 1), (track.Codec, track.PayloadType, track.SampleRate, track.Channels));
        Assert.Equal((0L, 0L), (depacketizer.LostPackets, depacketizer.Damaged));
        Assert.Equal(File.ReadAllBytes(TestSamples.PathOf(path + ".frames")), payloads.SelectMany(p => p).ToArray());
    }

    [Fact]
    public void JoinsAnAccessUnitFragmentedOverPackets()
    {
        var unit = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();
        var depacketizer = new RtpAudioDepacketizer(Hbr);
        var units = new List<byte[]>();
        depacketizer.Add(Aac(1, 1000, false, [(unit.Length, 0)], unit[..1400]), units);
        depacketizer.Add(Aac(2, 1000, false, [(unit.Length, 0)], unit[1400..2800]), units);
        Assert.Empty(units);
        depacketizer.Add(Aac(3, 1000, true, [(unit.Length, 0)], unit[2800..]), units);
        depacketizer.Add(Aac(4, 2024, true, [(3, 0), (2, 0)], [1, 2, 3, 4, 5]), units);

        Assert.Equal([unit, [1, 2, 3], [4, 5]], units);
        Assert.Equal((0L, 0L), (depacketizer.LostPackets, depacketizer.Damaged));
    }

    [Theory]
    [InlineData(2)] // the middle fragment lost
    [InlineData(3)] // the last one, with the marker: the next unit's timestamp ends it
    public void DropsAFragmentedAccessUnitMissingAPacket(int lost)
    {
        var unit = new byte[3000];
        var depacketizer = new RtpAudioDepacketizer(Hbr);
        var units = new List<byte[]>();
        byte[][] packets =
        [
            Aac(1, 1000, false, [(unit.Length, 0)], unit[..1400]),
            Aac(2, 1000, false, [(unit.Length, 0)], unit[1400..2800]),
            Aac(3, 1000, true, [(unit.Length, 0)], unit[2800..]),
            Aac(4, 2024, true, [(2, 0)], [8, 9]),
        ];
        foreach (var packet in packets.Where((_, i) => i + 1 != lost))
        {
            depacketizer.Add(packet, units);
        }

        Assert.Equal([[8, 9]], units);
        Assert.Equal((1L, 1L), (depacketizer.LostPackets, depacketizer.Damaged));
    }

    [Fact]
    public void DropsAFragmentedAccessUnitWhoseMarkerComesEarly()
    {
        var depacketizer = new RtpAudioDepacketizer(Hbr);
        var units = new List<byte[]>();
        depacketizer.Add(Aac(1, 1000, false, [(3000, 0)], new byte[1400]), units);
        depacketizer.Add(Aac(2, 1000, true, [(3000, 0)], new byte[1400]), units); // the end, 200 bytes short
        depacketizer.Add(Aac(3, 2024, true, [(1, 0)], [7]), units);

        Assert.Equal([[7]], units);
        Assert.Equal((0L, 1L), (depacketizer.LostPackets, depacketizer.Damaged));
    }

    [Fact]
    public void DropsAFragmentedAccessUnitWhenAnotherStartsBeforeItsEnd()
    {
        // No packet lost by the sequence numbers: the sender never sent the end. The next unit's timestamp ends it.
        var depacketizer = new RtpAudioDepacketizer(Hbr);
        var units = new List<byte[]>();
        depacketizer.Add(Aac(1, 1000, false, [(3000, 0)], new byte[1400]), units);
        depacketizer.Add(Aac(2, 1000, false, [(3000, 0)], new byte[1400]), units);
        depacketizer.Add(Aac(3, 2024, true, [(2, 0)], [8, 9]), units);

        Assert.Equal([[8, 9]], units);
        Assert.Equal((0L, 1L), (depacketizer.LostPackets, depacketizer.Damaged));
    }

    [Fact]
    public void ReadsAacLbrsFieldSizes()
    {
        // AAC-lbr: 6 bits of size, 2 of index (RFC 3640, 3.3.5): a byte a header.
        var depacketizer = new RtpAudioDepacketizer(Hbr with { SizeLength = 6, IndexLength = 2, IndexDeltaLength = 2 });
        var units = new List<byte[]>();
        depacketizer.Add(Rtp(1, 0, true, [0, 16, 2 << 2, 3 << 2, 1, 2, 3, 4, 5]), units);

        Assert.Equal([[1, 2], [3, 4, 5]], units);
    }

    [Theory]
    [InlineData(new byte[] { 0 })] // no room for the headers' length
    [InlineData(new byte[] { 0, 8, 0 })] // fewer bits than a header
    [InlineData(new byte[] { 0, 24, 0, 16, 0 })] // not a whole number of headers
    [InlineData(new byte[] { 0, 32, 0, 16, 0 })] // the headers run past the packet
    [InlineData(new byte[] { 0, 32, 0, 16, 0, 16, 1, 2, 3 })] // two units of 2 bytes, 3 bytes sent
    public void CountsAPacketWhoseHeadersDoNotFitAsDamaged(byte[] payload)
    {
        var depacketizer = new RtpAudioDepacketizer(Hbr);
        var units = new List<byte[]>();
        depacketizer.Add(Rtp(1, 0, true, payload), units);
        depacketizer.Add(Aac(2, 1024, true, [(1, 0)], [7]), units);

        Assert.Equal(1, depacketizer.Damaged);
        Assert.Equal([7], units[^1]);
    }

    [Fact]
    public void RefusesInterleavedAccessUnits()
    {
        var depacketizer = new RtpAudioDepacketizer(Hbr);
        Assert.Throws<NotSupportedException>(() => depacketizer.Add(Aac(1, 0, true, [(1, 0), (1, 1)], [1, 2]), []));
    }

    [Fact]
    public void IgnoresOtherPayloadTypesAndOldPacketsAndCountsLostOnes()
    {
        var depacketizer = new RtpAudioDepacketizer(new SdpAudioTrack(CodecIds.PcmAlaw, 8, null, 8000, 1, 0, 0, 0, []));
        var payloads = new List<byte[]>();
        depacketizer.Add(Rtp(10, 0, false, [1, 2], 8), payloads);
        depacketizer.Add(Rtp(11, 0, false, [9, 9], 0), payloads); // µ-law: another stream's
        depacketizer.Add(Rtp(10, 0, false, [1, 2], 8), payloads); // a repeat
        depacketizer.Add(Rtp(13, 0, false, [5, 6], 8), payloads); // 11 and 12 lost
        depacketizer.Add([1, 2, 3], payloads); // not RTP

        Assert.Equal([[1, 2], [5, 6]], payloads);
        Assert.Equal(2, depacketizer.LostPackets);
    }

    [Theory]
    [InlineData("m=audio 0 RTP/AVP 0\r\n", CodecIds.PcmMulaw, 8000, 1, null)]
    [InlineData("m=audio 0 RTP/AVP 8\r\n", CodecIds.PcmAlaw, 8000, 1, null)]
    [InlineData("m=audio 0 RTP/AVP 96\r\na=rtpmap:96 pcma/16000\r\n", CodecIds.PcmAlaw, 16000, 1, null)]
    [InlineData("m=audio 0 RTP/AVP 96\r\na=rtpmap:96 mpeg4-generic/16000/1\r\na=fmtp:96 streamtype=5; profile-level-id=15; mode=AAC-hbr; config=1408; SizeLength=13; IndexLength=3; IndexDeltaLength=3\r\n", CodecIds.Aac, 16000, 1, null)]

    // The first format that plays, in the SDP's order, across audio tracks.
    [InlineData("m=audio 0 RTP/AVP 98 8\r\na=rtpmap:98 G726-32/8000\r\n", CodecIds.PcmAlaw, 8000, 1, null)]
    [InlineData("m=audio 0 RTP/AVP 98\r\na=rtpmap:98 opus/48000/2\r\nm=audio 0 RTP/AVP 0\r\n", CodecIds.PcmMulaw, 8000, 1, null)]

    // None that plays: the first one's name.
    [InlineData("m=audio 0 RTP/AVP 96\r\na=rtpmap:96 MP4A-LATM/16000/1\r\na=fmtp:96 cpresent=0;config=400028103fc0\r\n", null, 0, 0, "MP4A-LATM")]
    [InlineData("m=audio 0 RTP/AVP 2\r\n", null, 0, 0, "payload type 2")]
    [InlineData("m=audio 0 RTP/AVP 96\r\na=rtpmap:96 MPEG4-GENERIC/48000/2\r\na=fmtp:96 mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3\r\n", null, 0, 0, "MPEG4-GENERIC")] // no config
    [InlineData("m=audio 0 RTP/AVP 96\r\na=rtpmap:96 MPEG4-GENERIC/48000/2\r\na=fmtp:96 mode=CELP-vbr;sizelength=6;config=1190\r\n", null, 0, 0, "MPEG4-GENERIC")]
    [InlineData("m=audio 0 RTP/AVP 96\r\na=rtpmap:96 MPEG4-GENERIC/48000/2\r\na=fmtp:96 mode=AAC-hbr;sizelength=13;config=119\r\n", null, 0, 0, "MPEG4-GENERIC")] // not hexadecimal bytes
    [InlineData("", null, 0, 0, null)]
    public void ReadsTheSdpsAudioTrack(string media, string? codec, int rate, int channels, string? skipped)
    {
        var sdp = Sdp.Parse("v=0\r\ns=-\r\nt=0 0\r\nm=video 0 RTP/AVP 26\r\na=control:trackID=1\r\n" + media + (media.Length > 0 ? "a=control:trackID=2\r\n" : ""));

        Assert.Equal(SdpCodec.Jpeg, sdp.Track!.Codec);
        Assert.Equal((codec, rate, channels), (sdp.Audio?.Codec, sdp.Audio?.SampleRate ?? 0, sdp.Audio?.Channels ?? 0));
        Assert.Equal(skipped, sdp.SkippedAudio);
        if (codec == CodecIds.Aac)
        {
            Assert.Equal((13, 3, 3, "1408"), (sdp.Audio!.SizeLength, sdp.Audio.IndexLength, sdp.Audio.IndexDeltaLength, Convert.ToHexString(sdp.Audio.Config)));
        }
    }

    /// <summary>The audio track of a recording's SDP (make-samples.sh's -sdp_file).</summary>
    // Buffers each after its 16-bit length.
    private static List<byte[]> Lengthed(byte[] data)
    {
        var buffers = new List<byte[]>();
        for (int at = 0; at < data.Length;)
        {
            int length = (data[at] << 8) | data[at + 1];
            buffers.Add(data.AsSpan(at + 2, length).ToArray());
            at += 2 + length;
        }

        return buffers;
    }

    internal static SdpAudioTrack Track(string recording) =>
        Sdp.Parse(File.ReadAllText(TestSamples.PathOf(recording + ".sdp"))).Audio ?? throw new InvalidDataException($"{recording}.sdp has no audio track that plays.");

    /// <summary>An RFC 3640 packet in AAC-hbr's sizes: a 13-bit size and a 3-bit index for each access unit, then the data.</summary>
    internal static byte[] Aac(ushort sequence, uint timestamp, bool marker, (int Size, int Index)[] headers, byte[] data, int payloadType = 97)
    {
        var payload = new List<byte> { (byte)((headers.Length * 16) >> 8), (byte)(headers.Length * 16) };
        foreach (var (size, index) in headers)
        {
            payload.Add((byte)(size >> 5));
            payload.Add((byte)((size << 3) | index));
        }

        payload.AddRange(data);
        return Rtp(sequence, timestamp, marker, [.. payload], payloadType);
    }

    internal static byte[] Rtp(ushort sequence, uint timestamp, bool marker, byte[] payload, int payloadType = 97) =>
    [
        0x80, (byte)((marker ? 0x80 : 0) | payloadType), (byte)(sequence >> 8), (byte)sequence,
        (byte)(timestamp >> 24), (byte)(timestamp >> 16), (byte)(timestamp >> 8), (byte)timestamp,
        0, 0, 0, 1,
        .. payload,
    ];
}
