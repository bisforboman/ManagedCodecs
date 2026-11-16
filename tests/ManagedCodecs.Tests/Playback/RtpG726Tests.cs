using ManagedCodecs.Playback;
using ManagedCodecs.Playback.Rtsp;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// G.726 over RTP (RFC 3551, 4.5.4; docs/decisions/2026-11-15-g726.md) against FFmpeg's packetizer, which make-samples.sh
/// runs on the wav-g726-itu samples' codes at each rate: as <c>G726-N</c> (each byte filled from its least significant
/// bit) and as <c>AAL2-G726-N</c> (from its most significant, as the .wav has them). The SDP names the codec and the bits
/// a code, the payloads joined are the file's codes in that packing, and through <see cref="LivePlayer"/> the chunks
/// taken are <see cref="G726Decoder"/>'s for the packets in order, over the RTSP connection and over UDP.
/// </summary>
public class RtpG726Tests
{
    private const string Slices = "h264-main-slices";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Unstalled = TimeSpan.FromMinutes(10);

    [Theory]
    [InlineData("rtp-g726", 2)]
    [InlineData("rtp-g726", 3)]
    [InlineData("rtp-g726", 4)]
    [InlineData("rtp-g726", 5)]
    [InlineData("rtp-aal2-g726", 2)]
    [InlineData("rtp-aal2-g726", 3)]
    [InlineData("rtp-aal2-g726", 4)]
    [InlineData("rtp-aal2-g726", 5)]
    public void GivesBackTheCodesFfmpegPacketized(string kind, int bits)
    {
        string recording = $"{kind}-{8 * bits}";
        bool leastSignificantFirst = kind == "rtp-g726";
        var track = RtpAudioDepacketizerTests.Track(recording);
        Assert.Equal((leastSignificantFirst ? CodecIds.AdpcmG726Le : CodecIds.AdpcmG726, 8000, 1), (track.Codec, track.SampleRate, track.Channels));
        Assert.Equal([(byte)bits], track.Config);

        var depacketizer = new RtpAudioDepacketizer(track);
        var payloads = new List<byte[]>();
        foreach (var packet in RtpH264DepacketizerTests.Recording(recording))
        {
            depacketizer.Add(packet, payloads);
        }

        // Whole codes in each packet (RFC 3551: a packet holds a whole number of them), the file's in order.
        Assert.True(payloads.Count > 1);
        Assert.All(payloads, payload => Assert.Equal(0, (payload.Length * 8) % bits));
        var codes = G726DecoderTests.Unpack(G726DecoderTests.Data($"wav-g726-itu-{8 * bits}.wav"), bits, leastSignificantFirst: false);
        var sent = G726DecoderTests.Unpack(payloads.SelectMany(p => p).ToArray(), bits, leastSignificantFirst);
        Assert.Equal(codes.Take(sent.Length), sent);
        Assert.InRange(sent.Length, codes.Length - (8 * 1024 / bits), codes.Length);
        Assert.Equal((0L, 0L), (depacketizer.LostPackets, depacketizer.Damaged));

        // Decoded packet by packet, as the player does: the file's samples.
        var decoder = new G726Decoder(G726DecoderTests.Stream(track.Codec, bits));
        var decoded = payloads.SelectMany(p => decoder.Decode(p).Samples[0]).ToArray();
        var whole = new G726Decoder(G726DecoderTests.Stream(CodecIds.AdpcmG726, bits)).Decode(G726DecoderTests.Data($"wav-g726-itu-{8 * bits}.wav")).Samples[0];
        Assert.Equal(whole.Take(decoded.Length), decoded);
    }

    [Theory]
    [InlineData("rtp-g726-32", false)]
    [InlineData("rtp-aal2-g726-24", true)]
    [InlineData("rtp-g726-40", true)]
    public void PlaysTheSoundAsTheDecoderDecodesIt(string recording, bool udp)
    {
        var track = RtpAudioDepacketizerTests.Track(recording);
        var packets = RtpH264DepacketizerTests.Recording(recording);
        var expected = LiveRtspAudioTests.Expected(track, packets);
        var video = LiveRtspTests.AccessUnits(Slices)[0];
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(LiveRtspAudioTests.TwoTracks(recording), async play =>
        {
            foreach (var packet in video)
            {
                await play.SendAsync(packet);
            }

            foreach (var packet in packets)
            {
                await play.SendAsync(packet, 2);
                await gate.WaitAsync(play.Token);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        })
        {
            UdpOnly = udp,
        };

        using var player = new LivePlayer(new RtspSource(server.Uri) { Audio = true, StallTimeout = Unstalled });
        player.Start();
        Assert.NotNull(WaitFor(player, player.TakeLatest));
        int total = 0;
        foreach (var chunks in expected)
        {
            foreach (var samples in chunks)
            {
                var chunk = WaitFor(player, player.TakeAudio);
                Assert.Equal(8000, chunk.SampleRate);
                Assert.Equal(samples, chunk.Samples);
                total++;
            }

            gate.Release();
        }

        Assert.Equal(packets.Count, total);
        Assert.Equal((track.Codec, 8000, 1), (player.Audio!.Codec, player.Audio.SampleRate, player.Audio.Channels));
        Assert.Equal(track.Config, player.Audio.CodecPrivate.ToArray());
        Assert.Null(player.SkippedAudioCodec);
        Assert.Equal((total, 0L, 0L, 0L), (player.Statistics.DecodedAudioFrames, player.Statistics.DroppedAudioFrames, player.Statistics.DamagedAudioFrames, player.Statistics.LostPackets));
    }

    [Theory]
    [InlineData("a=rtpmap:96 G726-16/8000", CodecIds.AdpcmG726Le, 2)]
    [InlineData("a=rtpmap:96 G726-24/8000", CodecIds.AdpcmG726Le, 3)]
    [InlineData("a=rtpmap:96 g726-32/8000", CodecIds.AdpcmG726Le, 4)]
    [InlineData("a=rtpmap:96 G726-40/8000/1", CodecIds.AdpcmG726Le, 5)]
    [InlineData("a=rtpmap:96 AAL2-G726-32/8000", CodecIds.AdpcmG726, 4)]
    [InlineData("a=rtpmap:96 aal2-g726-16/8000", CodecIds.AdpcmG726, 2)]
    [InlineData("a=rtpmap:96 G726-32/8000/2", null, 0)] // G.726 has one channel
    [InlineData("a=rtpmap:96 G726-48/8000", null, 0)] // no such rate
    [InlineData("a=rtpmap:96 G726/8000", null, 0)]
    [InlineData("a=rtpmap:96 AAL2-PCMU/8000", null, 0)]
    public void ReadsTheSdpsG726Track(string rtpMap, string? codec, int bits)
    {
        var sdp = Sdp.Parse("v=0\r\ns=-\r\nt=0 0\r\nm=video 0 RTP/AVP 26\r\na=control:trackID=1\r\nm=audio 0 RTP/AVP 96\r\n" + rtpMap + "\r\na=control:trackID=2\r\n");

        Assert.Equal(codec, sdp.Audio?.Codec);
        if (codec is null)
        {
            Assert.Equal(rtpMap.Split(' ')[1].Split('/')[0], sdp.SkippedAudio);
        }
        else
        {
            Assert.Equal([(byte)bits], sdp.Audio!.Config);
            Assert.Equal((8000, 1), (sdp.Audio.SampleRate, sdp.Audio.Channels));
        }
    }

    private static T WaitFor<T>(LivePlayer player, Func<T?> poll)
        where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (poll() is { } value)
            {
                return value;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Timed out: {player.State}, {player.Error}, {player.Statistics}.");
            Thread.Sleep(2);
        }
    }
}
