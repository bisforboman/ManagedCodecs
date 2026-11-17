using ManagedCodecs.Containers;
using ManagedCodecs.Playback;
using ManagedCodecs.Playback.Rtsp;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// The audio depacketizer (RFC 3640's AAC-hbr and AAC-lbr, RFC 3551's G.711) against FFmpeg's packetizer: the access
/// units it gives back are the file's packets byte for byte, and the SDP's config is the file's AudioSpecificConfig
/// (docs/decisions/2026-11-15-live-rtsp-audio.md). FFmpeg's encoder never writes an access unit larger than a packet,
/// so fragments' rules, AAC-lbr's field sizes, interleaving (by a packetizer of the tests' own, from RFC 3640) and
/// damage are hand-made packets; GStreamer's packetizers, recorded with
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
    public void DeinterleavesAccessUnitsInTheirDecodingOrder()
    {
        // FFmpeg's access units, interleaved as RFC 3640's 2.5 shows: groups of 9, 3 in a packet, a third of a group apart.
        // They come out in their order, each at its own time, as soon as those before it have come. At the start one
        // before the first to come could still come within the bound (5 units); the first packet already spans 6 (0,
        // 3, 6), so 0 comes at once. Then 1 after the second packet (1, 4, 7), the rest of the group after the third
        // (2, 5, 8), 9 after the fourth.
        var (track, units) = Interleavable();
        var packets = Interleaved(units, 1000, 7);
        var depacketizer = new RtpAudioDepacketizer(track);
        var output = new List<byte[]>();
        var times = new List<(uint Timestamp, int Index)>();
        var counts = new List<int>();
        foreach (var packet in packets)
        {
            depacketizer.Add(packet, output, times);
            counts.Add(output.Count);
        }

        int whole = units.Count / 9 * 9;
        Assert.Equal(units.Take(whole), output);
        Assert.Equal(Enumerable.Range(0, whole).Select(n => (1000u + ((uint)n * 1024), 0)), times);
        Assert.Equal([1, 2, 9, 10, 11, 18], counts.Take(6));
        Assert.Equal((0L, 0L), (depacketizer.LostPackets, depacketizer.Damaged));
    }

    [Theory]
    [InlineData(5 * 1024, new[] { 1, 3, 7 })] // the SDP's maxDisplacement: 5 access units, as 2.5's pattern needs
    [InlineData(480000, new[] { 0, 0, 3 })] // 10 s asked: held to a quarter of a second, 12000 ticks (11.7 units)
    public void GivesUpOnALostPacketsAccessUnitsOnceTheBoundIsPassed(int maxDisplacement, int[] firstCounts)
    {
        // The packet with 1, 4 and 7 is lost: they never come, and the rest comes in order, each once the newest held
        // is further past the one missing than the bound (3.2.3.3). With 5 units: 0 at once, 2 and 3 when 8 comes (7
        // past 1), 5, 6, 8 and 9 when 15 does. With 12000 ticks nothing until 15 is 15 past 0, then 0, 2 and 3.
        var (track, units) = Interleavable(maxDisplacement);
        var packets = Interleaved(units, 0, 100);
        packets.RemoveAt(1);
        var depacketizer = new RtpAudioDepacketizer(track);
        var output = new List<byte[]>();
        var counts = new List<int>();
        foreach (var packet in packets)
        {
            depacketizer.Add(packet, output);
            counts.Add(output.Count);
        }

        Assert.Equal(firstCounts, counts.Take(3));
        var expected = units.Take(units.Count / 9 * 9).Where((_, n) => n is not (1 or 4 or 7)).ToList();
        Assert.Equal(expected.Take(output.Count), output);
        Assert.True(output.Count >= expected.Count - 12, $"{output.Count} of {expected.Count} given.");
        Assert.Equal((1L, 0L), (depacketizer.LostPackets, depacketizer.Damaged));
    }

    [Fact]
    public void DropsAnAccessUnitThatComesAfterItWasGivenUpOn()
    {
        // A sender that interleaves further than a quarter of a second: groups of 24, units 8 apart in a packet, so that
        // 16 comes 15 units (15360 ticks) before 1. Held no longer than 12000 ticks, some are given up on before they
        // come: they are dropped and counted, and those given are the stream's, in order. (A late packet never gets
        // this far: its sequence number is old, and the depacketizer drops it as it did before.)
        var (track, units) = Interleavable(480000);
        var depacketizer = new RtpAudioDepacketizer(track);
        var output = new List<byte[]>();
        foreach (var packet in Interleaved(units, 0, 0, group: 24, perPacket: 3))
        {
            depacketizer.Add(packet, output);
        }

        int at = 0;
        foreach (var unit in output)
        {
            while (!units[at].AsSpan().SequenceEqual(unit))
            {
                at++;
            }

            at++;
        }

        Assert.True(depacketizer.Damaged > 0);
        Assert.Equal(units.Count / 24 * 24, output.Count + depacketizer.Damaged + (units.Count / 24 * 24) - at);
    }

    [Fact]
    public void FollowsTheTimestampAcrossItsWrap()
    {
        var (track, units) = Interleavable();
        uint start = uint.MaxValue - (4 * 1024) + 1;
        var output = new List<byte[]>();
        var times = new List<(uint Timestamp, int Index)>();
        var depacketizer = new RtpAudioDepacketizer(track);
        foreach (var packet in Interleaved(units, start, 65530))
        {
            depacketizer.Add(packet, output, times);
        }

        int whole = units.Count / 9 * 9;
        Assert.Equal(units.Take(whole), output);
        Assert.Equal(Enumerable.Range(0, whole).Select(n => (unchecked(start + ((uint)n * 1024)), 0)), times);
    }

    [Fact]
    public void DeinterleavesWhatAnSdpWithoutTheParametersAnnounces()
    {
        // A sender that leaves out maxDisplacement and constantDuration, as it must not when interleaving: the deltas
        // show it, AAC's 1024 samples stand for the duration and a quarter of a second for the bound.
        var (_, units) = Interleavable();
        var track = RtpAudioDepacketizerTests.Track("aac-stereo-48k-single");
        var output = new List<byte[]>();
        var depacketizer = new RtpAudioDepacketizer(track);
        foreach (var packet in Interleaved(units, 0, 0))
        {
            depacketizer.Add(packet, output);
        }

        Assert.Equal(units.Take(output.Count), output);
        Assert.True(output.Count >= (units.Count / 9 * 9) - 12, $"{output.Count} given.");
    }

    [Fact]
    public void PlacesAFragmentedAccessUnitAmongInterleavedOnes()
    {
        // A unit too large for a packet goes alone, fragmented, at its packet's timestamp (3.2.3.1); the one after it
        // in time came first and waits for it. A unit past the bound lets them go.
        var track = Hbr with { ConstantDuration = 1024, MaxDisplacement = 4096 };
        var depacketizer = new RtpAudioDepacketizer(track);
        var output = new List<byte[]>();
        depacketizer.Add(Aac(1, 1024, true, [(2, 0)], [8, 9]), output);
        depacketizer.Add(Aac(2, 0, false, [(4, 0)], [1, 2]), output);
        depacketizer.Add(Aac(3, 0, true, [(4, 0)], [3, 4]), output);
        Assert.Empty(output);
        depacketizer.Add(Aac(4, 5 * 1024, true, [(1, 0)], [5]), output);
        Assert.Equal([[1, 2, 3, 4], [8, 9]], output);
    }

    [Fact]
    public void CountsAPacketWithAnAuIndexOtherThanZeroAsDamaged()
    {
        // The AAC modes code the first AU-Index 0 (RFC 3640, 3.3.5 and 3.3.6): another is no AAC sender's.
        var depacketizer = new RtpAudioDepacketizer(Hbr);
        var output = new List<byte[]>();
        depacketizer.Add(Aac(1, 0, true, [(1, 1), (1, 0)], [1, 2]), output);
        depacketizer.Add(Aac(2, 2048, true, [(1, 0)], [3]), output);
        Assert.Equal([[3]], output);
        Assert.Equal(1, depacketizer.Damaged);
    }

    [Fact]
    public void ReadsTheSdpsInterleavingParameters()
    {
        const string Media = "m=audio 0 RTP/AVP 96\r\na=rtpmap:96 mpeg4-generic/48000/6\r\n"
            + "a=fmtp:96 streamtype=5; profile-level-id=16; mode=AAC-hbr; config=11B0; sizeLength=13; indexLength=3; indexDeltaLength=3; constantDuration=1024; MaxDisplacement=5120\r\n";
        var track = Sdp.Parse("v=0\r\ns=-\r\nt=0 0\r\n" + Media).Audio!;
        Assert.Equal((1024, 5120, 6), (track.ConstantDuration, track.MaxDisplacement, track.Channels));

        var plain = Sdp.Parse("v=0\r\ns=-\r\nt=0 0\r\n" + Media.Replace("; constantDuration=1024; MaxDisplacement=5120", "", StringComparison.Ordinal)).Audio!;
        Assert.Equal((0, 0), (plain.ConstantDuration, plain.MaxDisplacement));
    }

    // FFmpeg's access units of one a packet, and the track with the parameters a sender that interleaves gives.
    internal static (SdpAudioTrack Track, List<byte[]> Units) Interleavable(int maxDisplacement = 5 * 1024)
    {
        const string Name = "aac-stereo-48k-single";
        var track = Track(Name);
        var units = new List<byte[]>();
        var depacketizer = new RtpAudioDepacketizer(track);
        foreach (var packet in RtpH264DepacketizerTests.Recording(Name))
        {
            depacketizer.Add(packet, units);
        }

        return (track with { ConstantDuration = 1024, MaxDisplacement = maxDisplacement }, units);
    }

    // A packetizer of the tests' own, from RFC 3640's 2.5 and 3.2.1.1: in each group of 9 access units (by default),
    // packet j holds j, j + 3 and j + 6, its timestamp the first's (start, 1024 a unit), the others' AU-Index-deltas 2.
    // The groups whole only.
    internal static List<byte[]> Interleaved(List<byte[]> units, uint start, ushort sequence, int group = 9, int perPacket = 3)
    {
        var packets = new List<byte[]>();
        int stride = group / perPacket;
        for (int first = 0; first + group <= units.Count; first += group)
        {
            for (int j = 0; j < stride; j++)
            {
                int[] members = [.. Enumerable.Range(0, perPacket).Select(k => first + j + (k * stride))];
                var headers = members.Select((n, k) => (units[n].Length, k == 0 ? 0 : stride - 1)).ToArray();
                packets.Add(Aac(sequence++, unchecked(start + ((uint)(first + j) * 1024)), true, headers, [.. members.SelectMany(n => units[n])]));
            }
        }

        return packets;
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
    [InlineData("m=audio 0 RTP/AVP 98 8\r\na=rtpmap:98 G729/8000\r\n", CodecIds.PcmAlaw, 8000, 1, null)]
    [InlineData("m=audio 0 RTP/AVP 98\r\na=rtpmap:98 opus/48000/2\r\nm=audio 0 RTP/AVP 0\r\n", CodecIds.PcmMulaw, 8000, 1, null)]

    // None that plays: the first one's name.
    [InlineData("m=audio 0 RTP/AVP 96\r\na=rtpmap:96 MP4A-LATM/16000/1\r\na=fmtp:96 cpresent=1;config=400028103fc0\r\n", null, 0, 0, "MP4A-LATM")] // its config in the stream, which is not read
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
