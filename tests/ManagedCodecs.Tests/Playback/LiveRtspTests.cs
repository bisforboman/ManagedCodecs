using System.Net;
using ManagedCodecs.H264;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// LivePlayer over RTSP against a loopback RTSP server that replays FFmpeg's recorded RTP (make-samples.sh): every
/// frame it shows is H264Decoder's for the access units so far (the depacketizer is checked against the MP4 in
/// RtpH264DepacketizerTests); Basic and Digest; the answers that are final and the ones that are retried; keep-alives;
/// a stall, a dropped connection, lost packets, and falling behind past MaxLag (docs/decisions/2026-11-10-live-rtsp-h264.md).
/// </summary>
public class LiveRtspTests
{
    private const string Slices = "h264-main-slices";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void ShowsEachFrameAsTheDecoderDecodesIt(bool digest, bool qop)
    {
        var units = AccessUnits(Slices);
        var expected = ExpectedFrames(Slices, units);
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices), async play =>
        {
            foreach (var unit in units)
            {
                foreach (var packet in unit)
                {
                    await play.SendAsync(packet);
                }

                await gate.WaitAsync(play.Token);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        })
        {
            Credential = new NetworkCredential("admin", "s3cret:pass"),
            Digest = digest,
            Qop = qop,
        };

        var uri = new UriBuilder(server.Uri) { UserName = "admin", Password = Uri.EscapeDataString("s3cret:pass") }.Uri;
        using var player = new LivePlayer(new RtspSource(uri));
        player.Start();
        int shown = 0;
        for (int i = 0; i < units.Count; i++)
        {
            if (expected[i] is { } pixels)
            {
                var frame = WaitFor(player, player.TakeLatest);
                Assert.Equal(pixels, frame.Pixels);
                player.Return(frame);
                shown++;
            }

            gate.Release();
        }

        Assert.True(shown >= units.Count - 4, $"Only {shown} of {units.Count} units gave a frame.");
        Assert.Equal(LiveState.Playing, player.State);
        var statistics = player.Statistics;
        Assert.Equal((0L, 0L, 0L), (statistics.LostPackets, statistics.DamagedFrames, statistics.SkippedFrames));
        Assert.Null(player.Error);

        // The URL went out without its user info; with Digest, every request after the 401 carried its own answer.
        Assert.All(server.Requests, r => Assert.DoesNotContain("admin", r.Url));
        Assert.Equal(["OPTIONS", "OPTIONS", "DESCRIBE", "SETUP", "PLAY"], server.Requests.Select(r => r.Method).Take(5));
        Assert.Equal(server.Uri + "/trackID=1", server.Requests.Single(r => r.Method == "SETUP").Url);
    }

    [Theory]
    [InlineData("rtsp://127.0.0.1:1/elsewhere/track", "rtsp://127.0.0.1:1/elsewhere/track")] // absolute: as it is
    [InlineData("*", null)] // the base itself
    public void SetsUpTheTrackAtItsControlUrl(string control, string? setupUrl)
    {
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices, control), play => Task.Delay(Timeout.Infinite, play.Token));
        using var player = new LivePlayer(new RtspSource(server.Uri));
        player.Start();
        WaitFor(player, () => server.Requests.FirstOrDefault(r => r.Method == "SETUP"));
        Assert.Equal(setupUrl ?? server.Uri + "/", server.Requests.First(r => r.Method == "SETUP").Url);
    }

    [Theory]
    [InlineData("DESCRIBE", 404, typeof(IOException))]
    [InlineData("DESCRIBE", 403, typeof(IOException))]
    [InlineData("PLAY", 454, typeof(IOException))]
    [InlineData("SETUP", 461, typeof(NotSupportedException))]
    public void GivesUpOnAnswersARetryWontChange(string method, int status, Type error)
    {
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices), play => Task.Delay(Timeout.Infinite, play.Token))
        {
            Status = (m, _) => m == method ? (status, "No") : null,
        };

        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new RtspSource(server.Uri), clock);
        player.Start();
        WaitFor(player, () => player.State == LiveState.Failed ? player : null);
        Assert.IsType(error, player.Error);
        clock.Advance(TimeSpan.FromMinutes(1));
        Thread.Sleep(20);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public void GivesUpOnAWrongPassword()
    {
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices), play => Task.Delay(Timeout.Infinite, play.Token))
        {
            Credential = new NetworkCredential("admin", "right"),
        };

        using var player = new LivePlayer(new RtspSource(server.Uri, new NetworkCredential("admin", "wrong")), new FakeLiveClock());
        player.Start();
        WaitFor(player, () => player.State == LiveState.Failed ? player : null);
        Assert.Contains("401", player.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(2, server.Requests.Count); // OPTIONS, then once more with the challenge's answer
    }

    [Theory]
    [InlineData("m=audio 0 RTP/AVP 97\r\na=rtpmap:97 MPEG4-GENERIC/48000/2\r\n")] // no video at all
    [InlineData("m=video 0 RTP/AVP 98\r\na=rtpmap:98 H265/90000\r\n")] // video, not H.264
    [InlineData("m=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\na=fmtp:96 packetization-mode=2\r\n")] // interleaved mode
    public void RefusesAStreamItCantPlay(string media)
    {
        using var server = new RtspTestServer("v=0\r\ns=-\r\nt=0 0\r\n" + media, play => Task.Delay(Timeout.Infinite, play.Token));
        using var player = new LivePlayer(new RtspSource(server.Uri), new FakeLiveClock());
        player.Start();
        WaitFor(player, () => player.State == LiveState.Failed ? player : null);
        Assert.IsType<NotSupportedException>(player.Error);
    }

    [Fact]
    public void KeepsTheSessionAliveAtHalfItsTimeout()
    {
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices), play => Task.Delay(Timeout.Infinite, play.Token))
        {
            SessionTimeout = 10,
        };

        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new RtspSource(server.Uri) { StallTimeout = TimeSpan.FromMinutes(5) }, clock);
        player.Start();
        WaitFor(player, () => clock.WaitingFor.Contains(TimeSpan.FromSeconds(5)) ? player : null);
        clock.Advance(TimeSpan.FromSeconds(5));
        var keepAlive = WaitFor(player, () => server.Requests.FirstOrDefault(r => r.Method == "GET_PARAMETER"));
        Assert.Equal("S0ession", keepAlive.Headers["Session"]);
        Assert.Equal(server.Uri + "/", keepAlive.Url);
    }

    [Fact]
    public void ConnectsAgainWhenTheStreamStallsOrEnds()
    {
        var units = AccessUnits(Slices);
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices), async play =>
        {
            foreach (var packet in units[0])
            {
                await play.SendAsync(packet);
            }

            // The first connection stalls; the second ends after its first unit.
            play.CloseAfter = play.Connection == 1;
            if (play.Connection == 0)
            {
                await Task.Delay(Timeout.Infinite, play.Token);
            }
        });

        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new RtspSource(server.Uri) { StallTimeout = TimeSpan.FromSeconds(3) }, clock);
        player.Start();
        Assert.NotNull(WaitFor(player, player.TakeLatest));

        WaitFor(player, () => clock.WaitingFor.Contains(TimeSpan.FromSeconds(3)) ? player : null);
        clock.Advance(TimeSpan.FromSeconds(3));
        WaitFor(player, () => player.State == LiveState.Reconnecting && clock.WaitingFor.SequenceEqual([TimeSpan.FromSeconds(0.5)]) ? player : null);
        Assert.IsType<TimeoutException>(player.Error);

        clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.NotNull(WaitFor(player, player.TakeLatest));
        WaitFor(player, () => player.State == LiveState.Reconnecting && player.Error is EndOfStreamException ? player : null);
        Assert.Equal((2, 1L), (server.Connections, player.Statistics.Reconnects));
    }

    [Fact]
    public void ConcealsLostPacketsAndCountsThem()
    {
        var packets = RtpH264DepacketizerTests.Recording("h264-bench-720p");
        using var server = new RtspTestServer(RtspTestServer.SdpOf("h264-bench-720p"), async play =>
        {
            for (int i = 0; i < packets.Count; i++)
            {
                if (i % 37 != 36)
                {
                    await play.SendAsync(packets[i]);
                }
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        });

        // The whole recording arrives at once on the real clock: a slow runner (ARM64 without SIMD) falls more than the
        // default MaxLag behind and rightly skips, which isn't what this test is about.
        using var player = new LivePlayer(new RtspSource(server.Uri) { MaxLag = TimeSpan.FromHours(1) });
        player.Start();
        WaitFor(player, () => player.Statistics.ReceivedFrames == 50 && player.Statistics.DecodedFrames > 0 ? player : null);
        Assert.NotNull(WaitFor(player, player.TakeLatest));
        var statistics = player.Statistics;
        Assert.Equal(packets.Count / 37, statistics.LostPackets);
        Assert.True(statistics.DamagedFrames > 0);

        // Units left with no picture are passed over, not fed to the decoder: it would refuse them, and the player
        // would then wait for an IDR picture, skipping everything after (this stream has one, at its start).
        WaitFor(player, () => player.Statistics.DecodedFrames + player.Statistics.DroppedFrames >= 25 ? player : null);
        Assert.Equal(0, player.Statistics.SkippedFrames);
        Assert.Equal(LiveState.Playing, player.State);
        Assert.Null(player.Error);
    }

    [Fact]
    public void SkipsToTheNextIdrPictureWhenDecodingFallsBehind()
    {
        // The recording twice, the second time with later sequence numbers and timestamps, so that it starts with an
        // IDR picture of its own: decoding is held while the first arrives, and released 2 s (past MaxLag) later.
        var units = AccessUnits(Slices);
        var again = units.Select(u => u.Select(p => Shifted(p, (ushort)units.Sum(x => x.Count), 90000)).ToList()).ToList();
        var expected = ExpectedFrames(Slices, units);
        using var sent = new SemaphoreSlim(0);
        using var gate = new SemaphoreSlim(0);
        var clock = new FakeLiveClock();
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices), async play =>
        {
            foreach (var packet in units.SelectMany(u => u))
            {
                await play.SendAsync(packet);
            }

            sent.Release();
            await gate.WaitAsync(play.Token);
            foreach (var packet in again.SelectMany(u => u))
            {
                await play.SendAsync(packet);
            }

            sent.Release();
            await Task.Delay(Timeout.Infinite, play.Token);
        });

        using var player = new LivePlayer(new RtspSource(server.Uri) { StallTimeout = TimeSpan.FromMinutes(5) }, clock)
        {
            DecodeGate = new SemaphoreSlim(0),
        };
        player.Start();
        Assert.True(sent.Wait(Patience));
        WaitFor(player, () => player.Statistics.ReceivedFrames == units.Count ? player : null);
        clock.Advance(TimeSpan.FromSeconds(2));
        gate.Release();
        Assert.True(sent.Wait(Patience));
        WaitFor(player, () => player.Statistics.ReceivedFrames == 2 * units.Count ? player : null);
        player.DecodeGate.Release(int.MaxValue / 2);

        // The first pass's IDR picture is decoded, though late; the rest of it is skipped; the second pass decodes whole.
        WaitFor(player, () => player.Statistics.SkippedFrames == units.Count - 1 ? player : null);
        var last = expected.Last(e => e is not null)!;
        WaitFor(player, () => player.TakeLatest() is { } frame && frame.Pixels.SequenceEqual(last) ? player : null);
        Assert.Equal(units.Count - 1, player.Statistics.SkippedFrames);
    }

    // The recording's packets grouped by access unit (a unit ends at the marker bit).
    private static List<List<byte[]>> AccessUnits(string name)
    {
        var units = new List<List<byte[]>> { new() };
        foreach (var packet in RtpH264DepacketizerTests.Recording(name))
        {
            units[^1].Add(packet);
            if ((packet[1] & 0x80) != 0)
            {
                units.Add([]);
            }
        }

        units.RemoveAt(units.Count - 1);
        return units;
    }

    // For each unit, the RGBA of the newest frame H264Decoder gives for it (null for none), as the player converts it.
    private static List<byte[]?> ExpectedFrames(string name, List<List<byte[]>> units)
    {
        var depacketizer = new RtpH264Depacketizer(96, RtpH264DepacketizerTests.SdpParameterSets(name));
        var decoder = new H264Decoder(ReadOnlySpan<byte>.Empty) { ConcealErrors = true };
        var expected = new List<byte[]?>();
        long pts = 0;
        foreach (var unit in units)
        {
            var done = new List<RtpAccessUnit>();
            foreach (var packet in unit)
            {
                depacketizer.Add(packet, done);
            }

            var frames = decoder.Decode(Assert.Single(done).Data, pts++);
            if (frames.Count == 0)
            {
                expected.Add(null);
                continue;
            }

            var frame = frames[^1];
            var pixels = new byte[frame.Width * frame.Height * 4];
            YuvToRgba.For(frame).Convert(frame, pixels);
            expected.Add(pixels);
            foreach (var f in frames)
            {
                f.Dispose();
            }
        }

        return expected;
    }

    private static byte[] Shifted(byte[] packet, ushort sequence, uint timestamp)
    {
        var shifted = packet.ToArray();
        ushort s = (ushort)(((shifted[2] << 8) | shifted[3]) + sequence);
        uint t = (uint)((shifted[4] << 24) | (shifted[5] << 16) | (shifted[6] << 8) | shifted[7]) + timestamp;
        (shifted[2], shifted[3]) = ((byte)(s >> 8), (byte)s);
        (shifted[4], shifted[5], shifted[6], shifted[7]) = ((byte)(t >> 24), (byte)(t >> 16), (byte)(t >> 8), (byte)t);
        return shifted;
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
