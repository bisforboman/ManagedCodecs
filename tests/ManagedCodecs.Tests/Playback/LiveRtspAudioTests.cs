using System.Text.RegularExpressions;
using ManagedCodecs.Playback;
using ManagedCodecs.Playback.Rtsp;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// LivePlayer with <see cref="RtspSource.Audio"/> against the loopback RTSP server, which serves a video recording and
/// one of FFmpeg's audio recordings as two tracks (docs/decisions/2026-11-15-live-rtsp-audio.md): the chunks taken are
/// the decoder's output for the access units in order, over the RTSP connection and over UDP; no second SETUP without
/// Audio; a refused audio SETUP, or audio that doesn't play, leaves the video playing; a full queue drops its oldest;
/// and audio alone doesn't keep a stalled connection.
/// </summary>
public class LiveRtspAudioTests
{
    private const string Slices = "h264-main-slices";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Unstalled = TimeSpan.FromMinutes(10);

    [Theory]
    [InlineData("aac-stereo-48k", false, 48000)] // several access units a packet
    [InlineData("aac-stereo-48k", true, 48000)]
    [InlineData("heaac-stereo-44k", false, 44100)]
    [InlineData("wav-alaw-codes", false, 8000)] // the static payload type 8
    [InlineData("wav-mulaw-codes", true, 8000)] // PCMU/8000/2
    [InlineData("aac-stereo-48k-latm", false, 48000)] // MP4A-LATM
    [InlineData("aac-stereo-48k-latm", true, 48000)]
    public void PlaysTheAudioAsTheDecoderDecodesIt(string recording, bool udp, int chunkRate)
    {
        var track = RtpAudioDepacketizerTests.Track(recording);
        var packets = RtpH264DepacketizerTests.Recording(recording);
        var expected = Expected(track, packets);
        var video = LiveRtspTests.AccessUnits(Slices)[0];
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(TwoTracks(recording), async play =>
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
        Assert.Null(player.Audio);
        player.Start();
        Assert.NotNull(WaitFor(player, player.TakeLatest));
        int total = 0;
        foreach (var chunks in expected)
        {
            foreach (var samples in chunks)
            {
                var chunk = WaitFor(player, player.TakeAudio);
                Assert.Equal(chunkRate, chunk.SampleRate);
                Assert.Equal(samples, chunk.Samples);
                total++;
            }

            gate.Release();
        }

        Assert.True(total > 0);
        Assert.Null(player.TakeAudio());
        Assert.Equal((track.Codec, track.SampleRate, track.Channels), (player.Audio!.Codec, player.Audio.SampleRate, player.Audio.Channels));
        Assert.Null(player.SkippedAudioCodec);
        var statistics = player.Statistics;
        Assert.Equal((total, 0L, 0L, 0L), (statistics.DecodedAudioFrames, statistics.DroppedAudioFrames, statistics.DamagedAudioFrames, statistics.LostPackets));
        Assert.Equal((LiveState.Playing, 1), (player.State, server.Connections));

        // The audio's SETUP follows the video's, in its session and on its transport: channels 2 and 3 of the
        // connection, or a port pair of its own.
        var setups = server.Requests.Where(r => r.Method == "SETUP").Skip(udp ? 1 : 0).ToList();
        Assert.Equal(2, setups.Count);
        Assert.EndsWith("trackID=2", setups[1].Url, StringComparison.Ordinal);
        Assert.StartsWith("S0ession", setups[1].Headers["Session"], StringComparison.Ordinal);
        if (udp)
        {
            int Port(RtspRequest request) => int.Parse(Regex.Match(request.Headers["Transport"], @"client_port=(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(0, Port(setups[1]) % 2);
            Assert.NotEqual(Port(setups[0]), Port(setups[1]));
        }
        else
        {
            Assert.Equal("RTP/AVP/TCP;unicast;interleaved=2-3", setups[1].Headers["Transport"]);
        }
    }

    [Fact]
    public void SetsUpNoAudioUnlessAskedTo()
    {
        var video = LiveRtspTests.AccessUnits(Slices)[0];
        var audio = RtpH264DepacketizerTests.Recording("aac-stereo-48k");
        using var server = new RtspTestServer(TwoTracks("aac-stereo-48k"), async play =>
        {
            foreach (var packet in video)
            {
                await play.SendAsync(packet);
            }

            await play.SendAsync(audio[0], 2); // a camera that sends it all the same
            await Task.Delay(Timeout.Infinite, play.Token);
        });

        using var player = new LivePlayer(new RtspSource(server.Uri) { StallTimeout = Unstalled });
        player.Start();
        Assert.NotNull(WaitFor(player, player.TakeLatest));

        Assert.Single(server.Requests, r => r.Method == "SETUP");
        Assert.Null(player.Audio);
        Assert.Null(player.SkippedAudioCodec);
        Assert.Null(player.TakeAudio());
        Assert.Equal(0, player.Statistics.DecodedAudioFrames);
    }

    [Fact]
    public void PlaysTheVideoWhenTheAudiosSetupIsRefused()
    {
        var video = LiveRtspTests.AccessUnits(Slices)[0];
        using var server = new RtspTestServer(TwoTracks("aac-stereo-48k"), async play =>
        {
            foreach (var packet in video)
            {
                await play.SendAsync(packet);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        })
        {
            StatusOf = request => request.Method == "SETUP" && request.Url.EndsWith("trackID=2", StringComparison.Ordinal) ? (461, "Unsupported Transport") : null,
        };

        using var player = new LivePlayer(new RtspSource(server.Uri) { Audio = true, StallTimeout = Unstalled });
        player.Start();
        Assert.NotNull(WaitFor(player, player.TakeLatest));

        Assert.Equal(2, server.Requests.Count(r => r.Method == "SETUP"));
        Assert.Null(player.Audio);
        Assert.Contains("audio", Assert.IsType<IOException>(player.Error).Message, StringComparison.Ordinal);
        Assert.Equal((LiveState.Playing, 1), (player.State, server.Connections));
    }

    [Theory]
    [InlineData("m=audio 0 RTP/AVP 96\r\na=rtpmap:96 MP4A-LATM/16000/1\r\na=fmtp:96 cpresent=1;config=400028103fc0\r\n", "MP4A-LATM", null)] // its config in the stream, which is not read

    // An AudioSpecificConfig the decoder refuses (object type 5 bits of 0): no SETUP for it, and Error says why.
    [InlineData("m=audio 0 RTP/AVP 96\r\na=rtpmap:96 MPEG4-GENERIC/48000/2\r\na=fmtp:96 mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=0190\r\n", null, "config")]
    public void PlaysTheVideoWhenTheAudioDoesNotPlay(string media, string? skipped, string? error)
    {
        var video = LiveRtspTests.AccessUnits(Slices)[0];
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices).TrimEnd() + "\r\n" + media + "a=control:trackID=2\r\n", async play =>
        {
            foreach (var packet in video)
            {
                await play.SendAsync(packet);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        });

        using var player = new LivePlayer(new RtspSource(server.Uri) { Audio = true, StallTimeout = Unstalled });
        player.Start();
        Assert.NotNull(WaitFor(player, player.TakeLatest));

        Assert.Single(server.Requests, r => r.Method == "SETUP");
        Assert.Null(player.Audio);
        Assert.Equal(skipped, player.SkippedAudioCodec);
        Assert.Equal(error is null, player.Error is null);
        Assert.Equal(LiveState.Playing, player.State);
    }

    [Fact]
    public void DropsTheOldestAudioPastHalfASecond()
    {
        var track = RtpAudioDepacketizerTests.Track("aac-stereo-48k-single");
        var packets = RtpH264DepacketizerTests.Recording("aac-stereo-48k-single");
        var expected = Expected(track, packets).SelectMany(chunks => chunks).ToList();
        var video = LiveRtspTests.AccessUnits(Slices)[0];
        using var server = new RtspTestServer(TwoTracks("aac-stereo-48k-single"), async play =>
        {
            foreach (var packet in video)
            {
                await play.SendAsync(packet);
            }

            foreach (var packet in packets)
            {
                await play.SendAsync(packet, 2);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        });

        using var player = new LivePlayer(new RtspSource(server.Uri) { Audio = true, StallTimeout = Unstalled });
        player.Start();
        WaitFor(player, () => player.Statistics.DecodedAudioFrames == expected.Count ? player : null);

        // 1024 samples at 48 kHz: 23 chunks are 0.49 s, and a 24th would pass half a second.
        Assert.All(expected, samples => Assert.Equal(2048, samples.Length));
        Assert.Equal(expected.Count - 23, player.Statistics.DroppedAudioFrames);
        foreach (var samples in expected.Skip(expected.Count - 23))
        {
            Assert.Equal(samples, player.TakeAudio()!.Samples);
        }

        Assert.Null(player.TakeAudio());
    }

    [Fact]
    public void CountsADamagedAudioFrameAndPlaysOn()
    {
        var track = RtpAudioDepacketizerTests.Track("aac-stereo-48k-single");
        var packets = RtpH264DepacketizerTests.Recording("aac-stereo-48k-single").Take(6).Select(p => (byte[])p.Clone()).ToList();
        packets.RemoveAt(2); // lost
        Array.Fill(packets[3], (byte)0xFF, 16, packets[3].Length - 16); // an access unit the decoder rejects
        var video = LiveRtspTests.AccessUnits(Slices)[0];
        using var server = new RtspTestServer(TwoTracks("aac-stereo-48k-single"), async play =>
        {
            foreach (var packet in video)
            {
                await play.SendAsync(packet);
            }

            foreach (var packet in packets)
            {
                await play.SendAsync(packet, 2);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        });

        using var player = new LivePlayer(new RtspSource(server.Uri) { Audio = true, StallTimeout = Unstalled });
        player.Start();
        WaitFor(player, () => player.Statistics.DecodedAudioFrames + player.Statistics.DamagedAudioFrames == 5 ? player : null);

        var statistics = player.Statistics;
        Assert.Equal((4L, 1L, 1L), (statistics.DecodedAudioFrames, statistics.DamagedAudioFrames, statistics.LostPackets));
        Assert.Equal(LiveState.Playing, player.State);
        Assert.Equal(track.Codec, player.Audio!.Codec);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConnectsAgainWhenTheVideoStallsThoughAudioStillComes(bool udp)
    {
        var video = LiveRtspTests.AccessUnits(Slices)[0];
        var audio = RtpH264DepacketizerTests.Recording("aac-stereo-48k-single");
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(TwoTracks("aac-stereo-48k-single"), async play =>
        {
            foreach (var packet in video)
            {
                await play.SendAsync(packet);
            }

            if (play.Connection == 0)
            {
                await gate.WaitAsync(play.Token);
                await play.SendAsync(audio[0], 2);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        })
        {
            UdpOnly = udp,
        };

        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new RtspSource(server.Uri) { Audio = true, StallTimeout = TimeSpan.FromSeconds(3) }, clock);
        player.Start();
        Assert.NotNull(WaitFor(player, player.TakeLatest));
        WaitFor(player, () => clock.WaitingFor.Contains(TimeSpan.FromSeconds(3)) ? player : null);

        // Two seconds on, audio comes; a second later the video has been silent for three.
        clock.Advance(TimeSpan.FromSeconds(2));
        gate.Release();
        WaitFor(player, () => player.Statistics.DecodedAudioFrames == 1 ? player : null);
        clock.Advance(TimeSpan.FromSeconds(1));
        WaitFor(player, () => player.State == LiveState.Reconnecting ? player : null);
        Assert.IsType<TimeoutException>(player.Error);
    }

    // The video recording's SDP with the audio recording's track after it, each with a control URL.
    internal static string TwoTracks(string recording)
    {
        string audio = File.ReadAllText(TestSamples.PathOf(recording + ".sdp"));
        return RtspTestServer.SdpOf(Slices).TrimEnd() + "\r\n" + audio.Substring(audio.IndexOf("m=audio", StringComparison.Ordinal)).TrimEnd() + "\r\na=control:trackID=2\r\n";
    }

    // For each packet, the stereo samples of the chunks its access units decode to, by a decoder of the test's own.
    internal static List<List<float[]>> Expected(SdpAudioTrack track, List<byte[]> packets)
    {
        var decoder = AudioPipeline.CreateDecoder(new AudioStreamInfo(track.Codec, track.SampleRate, track.Channels, 0, new Rational(1, track.SampleRate), track.Config, 0));
        var depacketizer = new RtpAudioDepacketizer(track);
        var expected = new List<List<float[]>>();
        var units = new List<byte[]>();
        foreach (var packet in packets)
        {
            depacketizer.Add(packet, units);
            expected.Add([.. units.Select(unit => decoder.Decode(unit)).Where(frame => frame.SampleCount > 0).Select(frame => AudioPipeline.ToStereo(frame).Samples)]);
            units.Clear();
        }

        return expected;
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
