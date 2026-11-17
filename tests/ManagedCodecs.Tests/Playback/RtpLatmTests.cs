using ManagedCodecs.Containers;
using ManagedCodecs.Playback;
using ManagedCodecs.Playback.Rtsp;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// AAC as LATM over RTP (RFC 6416's MP4A-LATM; issue #527): the SDP's StreamMuxConfig read for the decoder's
/// AudioSpecificConfig, and the audioMuxElements taken apart, against FFmpeg's packetizer (-rtpflags latm: one access
/// unit an element, an element a packet), whose access units must be the file's packets byte for byte. Elements over
/// several packets, several access units an element, lengths of 255 and over, and damage are hand-made packets.
/// </summary>
public class RtpLatmTests
{
    // FFmpeg's config for 48 kHz stereo: version 0, one access unit an element, the AudioSpecificConfig 11 90.
    private static readonly SdpAudioTrack One = Track("400023203fc0");

    [Fact]
    public void GivesBackTheAccessUnitsFfmpegPacketized()
    {
        var track = RtpAudioDepacketizerTests.Track("aac-stereo-48k-latm");
        using var demuxer = Demuxer.Open(TestSamples.PathOf("aac-stereo-48k.mp4"));
        var expected = demuxer.ReadPackets().Where(p => p.StreamIndex == demuxer.Audio!.StreamIndex).Select(p => p.Data.ToArray()).ToList();

        var depacketizer = new RtpAudioDepacketizer(track);
        var units = new List<byte[]>();
        foreach (var packet in RtpH264DepacketizerTests.Recording("aac-stereo-48k-latm"))
        {
            depacketizer.Add(packet, units);
        }

        Assert.Equal((CodecIds.Aac, 48000, 2, 1), (track.Codec, track.SampleRate, track.Channels, track.LatmSubFrames));

        // The MP4's AudioSpecificConfig goes on with a sync extension (no SBR); FFmpeg's StreamMuxConfig has its two
        // first bytes, which say the same.
        Assert.Equal(demuxer.Audio!.CodecPrivate.Span.Slice(0, 2).ToArray(), track.Config);
        Assert.Equal((0L, 0L), (depacketizer.LostPackets, depacketizer.Damaged));
        Assert.Equal(expected, units);
    }

    [Theory]
    [InlineData("400023203fc0", "1190", 1)] // FFmpeg: 48 kHz stereo
    [InlineData("400028103fc0", "1408", 1)] // FFmpeg: 16 kHz mono
    [InlineData("40002320adca00", "119056e500", 1)] // GStreamer's rtpmp4apay: the sync extension (no SBR) with it
    [InlineData("41002320adca07e0", "119056e500", 2)] // the same with two access units an element (numSubFrames 1)
    [InlineData("7f0023200fc0", "1190", 64)]
    [InlineData("400056231007e0", "2b118800", 1)] // SBR signalled first (object type 5: 24 kHz core, 48 kHz out)
    [InlineData("40002320adcb3a9107e0", "119056e59d4880", 1)] // the sync extension with SBR at 48 kHz and parametric stereo
    [InlineData("855800010119007e00", "1190", 1)] // audioMuxVersion 1: taraBufferFullness, then the config's length, 16 bits
    [InlineData("855800028119056e50007e00", "119056e500", 1)] // the same with 40 bits: the sync extension and fill bits
    public void ReadsTheStreamMuxConfig(string config, string audioSpecificConfig, int subFrames)
    {
        Assert.True(LatmConfig.TryParse(Convert.FromHexString(config), out var found, out int count));
        Assert.Equal((audioSpecificConfig, subFrames), (Convert.ToHexStringLower(found), count));
    }

    [Theory]
    [InlineData("")]
    [InlineData("40")]
    [InlineData("400023")] // the AudioSpecificConfig cut short
    [InlineData("40002320c000")] // frameLengthType 3: frames of a fixed length
    [InlineData("000023200fc0")] // allStreamsSameTimeFraming 0
    [InlineData("401023200fc0")] // two programs
    [InlineData("400223200fc0")] // two layers
    [InlineData("e000119007e0")] // audioMuxVersionA 1
    [InlineData("400056230807e0")] // SBR over object type 1 (AAC Main), not AAC LC
    [InlineData("400023000fc0")] // channel configuration 0: a program config element
    [InlineData("855800028119")] // version 1: the config's length is more than is there
    public void RefusesAStreamMuxConfigItDoesNotRead(string config) =>
        Assert.False(LatmConfig.TryParse(Convert.FromHexString(config), out _, out _));

    [Theory]
    [InlineData("a=fmtp:96 profile-level-id=41;cpresent=0;config=400023203fc0", true)]
    [InlineData("a=fmtp:96 cpresent=0; Config=400023203FC0", true)]
    [InlineData("a=fmtp:96 profile-level-id=41;config=400023203fc0", false)] // cpresent is 1 when left out: the config is in the stream
    [InlineData("a=fmtp:96 cpresent=1;config=400023203fc0", false)]
    [InlineData("a=fmtp:96 cpresent=0", false)]
    [InlineData("a=fmtp:96 cpresent=0;config=40002320c000", false)]
    public void PlaysLatmWhoseConfigTheSdpGives(string fmtp, bool plays)
    {
        var sdp = Sdp.Parse("v=0\r\ns=-\r\nt=0 0\r\nm=audio 0 RTP/AVP 96\r\na=rtpmap:96 MP4A-LATM/48000/2\r\n" + fmtp + "\r\n");

        Assert.Equal(plays ? null : "MP4A-LATM", sdp.SkippedAudio);
        Assert.Equal(plays ? (CodecIds.Aac, 48000, 2, 1, "1190") : default, sdp.Audio is { } a ? (a.Codec, a.SampleRate, a.Channels, a.LatmSubFrames, Convert.ToHexStringLower(a.Config)) : default);
    }

    [Fact]
    public void JoinsAnElementOverPacketsAndTakesItsAccessUnitsApart()
    {
        // Two access units an element, of 600 and 3 bytes: 600 is sent as 255, 255, 90.
        var first = Enumerable.Range(0, 600).Select(i => (byte)(i * 3)).ToArray();
        byte[] element = [255, 255, 90, .. first, 3, 7, 8, 9];
        var depacketizer = new RtpAudioDepacketizer(Track("41002320adca07e0"));
        var units = new List<byte[]>();
        depacketizer.Add(Rtp(1, 1000, false, element[..200]), units);
        depacketizer.Add(Rtp(2, 1000, false, element[200..400]), units);
        Assert.Empty(units);
        depacketizer.Add(Rtp(3, 1000, true, element[400..]), units);
        depacketizer.Add(Rtp(4, 3048, true, [2, 1, 2, 0]), units); // the second one empty: nothing to decode

        Assert.Equal([first, [7, 8, 9], [1, 2]], units);
        Assert.Equal((0L, 0L), (depacketizer.LostPackets, depacketizer.Damaged));
    }

    [Fact]
    public void LeavesOtherDataAfterTheAccessUnitsAlone()
    {
        var depacketizer = new RtpAudioDepacketizer(One);
        var units = new List<byte[]>();
        depacketizer.Add(Rtp(1, 0, true, [2, 5, 6, 0xAA, 0xBB]), units);

        Assert.Equal([[5, 6]], units);
        Assert.Equal(0, depacketizer.Damaged);
    }

    [Theory]
    [InlineData(1)] // the element's first packet lost: what follows has no start
    [InlineData(2)] // a middle one
    [InlineData(3)] // its last, with the marker: the next element's timestamp ends it
    public void DropsAnElementMissingAPacket(int lost)
    {
        var unit = Enumerable.Range(0, 500).Select(i => (byte)(i * 5)).ToArray();
        byte[] element = [255, 245, .. unit];
        var depacketizer = new RtpAudioDepacketizer(One);
        var units = new List<byte[]>();
        byte[][] packets =
        [
            Rtp(1, 0, true, [1, 42]),
            Rtp(2, 1024, false, element[..200]),
            Rtp(3, 1024, false, element[200..400]),
            Rtp(4, 1024, true, element[400..]),
            Rtp(5, 2048, true, [2, 8, 9]),
        ];
        foreach (var packet in packets.Where((_, i) => i != lost))
        {
            depacketizer.Add(packet, units);
        }

        Assert.Equal([[42], [8, 9]], units);
        Assert.Equal((1L, 1L), (depacketizer.LostPackets, depacketizer.Damaged));
    }

    [Fact]
    public void GivesAWholeElementAfterAGap()
    {
        // The packet lost was an element of its own: the next one is whole, its length fits it, and it is given.
        var depacketizer = new RtpAudioDepacketizer(One);
        var units = new List<byte[]>();
        depacketizer.Add(Rtp(1, 0, true, [1, 42]), units);
        depacketizer.Add(Rtp(3, 2048, true, [2, 8, 9]), units);

        Assert.Equal([[42], [8, 9]], units);
        Assert.Equal((1L, 0L), (depacketizer.LostPackets, depacketizer.Damaged));
    }

    [Theory]
    [InlineData(new byte[] { 5, 1, 2 })] // a length past the element's end
    [InlineData(new byte[] { 255, 255 })] // a length that never ends
    [InlineData(new byte[0])]
    public void CountsAnElementWhoseLengthsDoNotFitAsDamaged(byte[] element)
    {
        var depacketizer = new RtpAudioDepacketizer(One);
        var units = new List<byte[]>();
        depacketizer.Add(Rtp(1, 0, true, element), units);
        depacketizer.Add(Rtp(2, 1024, true, [1, 7]), units);

        Assert.Equal([[7]], units);
        Assert.Equal(1, depacketizer.Damaged);
    }

    [Fact]
    public void DropsAnElementLargerThanItsAccessUnitsCanBe()
    {
        var depacketizer = new RtpAudioDepacketizer(One);
        var units = new List<byte[]>();
        for (int i = 0; i < 40; i++)
        {
            depacketizer.Add(Rtp((ushort)i, 0, false, new byte[1400]), units);
        }

        depacketizer.Add(Rtp(40, 0, true, new byte[10]), units);
        depacketizer.Add(Rtp(41, 1024, true, [1, 7]), units);

        Assert.Equal([[7]], units);
        Assert.Equal(1, depacketizer.Damaged);
    }

    private static SdpAudioTrack Track(string config) =>
        Sdp.Parse($"v=0\r\ns=-\r\nt=0 0\r\nm=audio 0 RTP/AVP 97\r\na=rtpmap:97 MP4A-LATM/48000/2\r\na=fmtp:97 cpresent=0;config={config}\r\n").Audio!;

    private static byte[] Rtp(ushort sequence, uint timestamp, bool marker, byte[] payload) => RtpAudioDepacketizerTests.Rtp(sequence, timestamp, marker, payload);
}
