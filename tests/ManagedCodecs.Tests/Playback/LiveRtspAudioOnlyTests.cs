using ManagedCodecs.Playback;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// LivePlayer against a loopback RTSP server whose SDP has sound and no picture
/// (docs/decisions/2026-11-16-live-audio-only.md): with <see cref="RtspSource.Audio"/> the sound is the session's one
/// track (channels 0 and 1, or the first port pair), decoded as beside a video, and it is what makes the player Playing
/// and what the stall timeout watches; without Audio, or with sound that doesn't play, the player fails and says why.
/// </summary>
public class LiveRtspAudioOnlyTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Unstalled = TimeSpan.FromMinutes(10);

    [Theory]
    [InlineData("aac-stereo-48k", false, 48000)]
    [InlineData("aac-stereo-48k", true, 48000)]
    [InlineData("heaac-stereo-44k", true, 44100)]
    [InlineData("wav-alaw-codes", false, 8000)]
    public void PlaysTheSoundAsTheDecoderDecodesIt(string recording, bool udp, int chunkRate)
    {
        var track = RtpAudioDepacketizerTests.Track(recording);
        var packets = RtpH264DepacketizerTests.Recording(recording);
        var expected = LiveRtspAudioTests.Expected(track, packets);
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(SoundOnly(recording), async play =>
        {
            foreach (var packet in packets)
            {
                await play.SendAsync(packet);
                await gate.WaitAsync(play.Token);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        })
        {
            UdpOnly = udp,
        };

        using var player = new LivePlayer(new RtspSource(server.Uri) { Audio = true, StallTimeout = Unstalled });
        player.Start();
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
        Assert.Equal((LiveState.Playing, 1), (player.State, server.Connections));
        Assert.Null(player.Error);
        Assert.Equal((track.Codec, track.SampleRate, track.Channels), (player.Audio!.Codec, player.Audio.SampleRate, player.Audio.Channels));

        // No picture, ever: nothing to take and no video frame counted.
        Assert.Null(player.TakeLatest());
        Assert.Equal((0L, 0L, total), (player.Statistics.ReceivedFrames, player.Statistics.DecodedFrames, player.Statistics.DecodedAudioFrames));

        // One SETUP (after a 461 over UDP), for the sound, on the channels or the ports a video would have had.
        var setups = server.Requests.Where(r => r.Method == "SETUP").ToList();
        Assert.Equal(udp ? 2 : 1, setups.Count);
        Assert.All(setups, setup => Assert.EndsWith("trackID=1", setup.Url, StringComparison.Ordinal));
        Assert.Equal(udp ? "RTP/AVP;unicast;client_port=" : "RTP/AVP/TCP;unicast;interleaved=0-1", udp ? setups[1].Headers["Transport"][..28] : setups[0].Headers["Transport"]);
    }

    [Fact]
    public void FailsWithoutAudioSayingSo()
    {
        using var server = new RtspTestServer(SoundOnly("aac-stereo-48k"), play => Task.Delay(Timeout.Infinite, play.Token));
        using var player = new LivePlayer(new RtspSource(server.Uri) { StallTimeout = Unstalled });
        player.Start();
        WaitFor(player, () => player.State == LiveState.Failed ? player : null);

        Assert.Contains("RtspSource.Audio", Assert.IsType<NotSupportedException>(player.Error).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(server.Requests, r => r.Method == "SETUP");
        Assert.Equal(1, server.Connections);
    }

    [Theory]
    [InlineData("m=audio 0 RTP/AVP 98\r\na=rtpmap:98 G726-32/8000\r\n", "G726-32")]

    // An AudioSpecificConfig the decoder refuses: the same on every connection.
    [InlineData("m=audio 0 RTP/AVP 96\r\na=rtpmap:96 MPEG4-GENERIC/48000/2\r\na=fmtp:96 mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=0190\r\n", null)]
    public void FailsWhenTheSoundDoesNotPlay(string media, string? skipped)
    {
        string sdp = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=No Name\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n" + media + "a=control:trackID=1\r\n";
        using var server = new RtspTestServer(sdp, play => Task.Delay(Timeout.Infinite, play.Token));
        using var player = new LivePlayer(new RtspSource(server.Uri) { Audio = true, StallTimeout = Unstalled });
        player.Start();
        WaitFor(player, () => player.State == LiveState.Failed ? player : null);

        Assert.NotNull(player.Error);
        if (skipped is not null)
        {
            Assert.Contains(skipped, player.Error.Message, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(server.Requests, r => r.Method == "SETUP");
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public void FailsOnInterleavedAccessUnits()
    {
        // The first AU header's index (its low 3 bits after the 13 of the size) made non-zero: interleaved (RFC 3640,
        // 3.2.3.1), which beside a video only turns the sound off.
        var packet = (byte[])RtpH264DepacketizerTests.Recording("aac-stereo-48k")[0].Clone();
        packet[15] |= 1;
        using var server = new RtspTestServer(SoundOnly("aac-stereo-48k"), async play =>
        {
            await play.SendAsync(packet);
            await Task.Delay(Timeout.Infinite, play.Token);
        });

        using var player = new LivePlayer(new RtspSource(server.Uri) { Audio = true, StallTimeout = Unstalled });
        player.Start();
        WaitFor(player, () => player.State == LiveState.Failed ? player : null);

        Assert.Contains("interleaves", Assert.IsType<NotSupportedException>(player.Error).Message, StringComparison.Ordinal);
        Assert.Equal(1, server.Connections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheStallTimeoutWatchesTheSound(bool udp)
    {
        var packets = RtpH264DepacketizerTests.Recording("aac-stereo-48k-single");
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(SoundOnly("aac-stereo-48k-single"), async play =>
        {
            await play.SendAsync(packets[0]);
            if (play.Connection == 0)
            {
                await gate.WaitAsync(play.Token);
                await play.SendAsync(packets[1]);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        })
        {
            UdpOnly = udp,
        };

        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new RtspSource(server.Uri) { Audio = true, StallTimeout = TimeSpan.FromSeconds(3) }, clock);
        player.Start();
        WaitFor(player, () => player.State == LiveState.Playing ? player : null);
        WaitFor(player, () => clock.WaitingFor.Contains(TimeSpan.FromSeconds(3)) ? player : null);

        // Two seconds on, more sound; two more on, the first wait is over, but the sound came a second before: the
        // watchdog waits the second that is left rather than ending the connection.
        clock.Advance(TimeSpan.FromSeconds(2));
        gate.Release();
        WaitFor(player, () => player.Statistics.DecodedAudioFrames == 2 ? player : null);
        clock.Advance(TimeSpan.FromSeconds(2));
        WaitFor(player, () => clock.WaitingFor.Contains(TimeSpan.FromSeconds(1)) ? player : null);
        Assert.Equal((LiveState.Playing, 1), (player.State, server.Connections));

        // Then three seconds without sound: a stall.
        clock.Advance(TimeSpan.FromSeconds(1));
        WaitFor(player, () => player.State == LiveState.Reconnecting ? player : null);
        Assert.IsType<TimeoutException>(player.Error);
    }

    // The audio recording's SDP, its one track with a control URL.
    private static string SoundOnly(string recording) =>
        File.ReadAllText(TestSamples.PathOf(recording + ".sdp")).TrimEnd() + "\r\na=control:trackID=1\r\n";

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
