using System.Net;
using System.Net.Sockets;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// LivePlayer over RTP over UDP, against the loopback RTSP server set to answer 461 to RTP over the RTSP connection, as
/// mediamtx does with UDP only (docs/decisions/2026-11-14-live-rtsp-udp.md): the fallback, the punch packets, every
/// frame as over TCP, reordering within the window and beyond it, duplicates, packets from another SSRC or address, and
/// a stall that says UDP was in use. The loopback interface loses and reorders nothing, so the server does it on purpose.
/// </summary>
public class LiveRtspUdpTests
{
    private const string Slices = "h264-main-slices";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public void FallsBackToUdpAndShowsEachFrameAsTheDecoderDecodesIt()
    {
        var units = LiveRtspTests.AccessUnits(Slices);
        var expected = LiveRtspTests.ExpectedFrames(Slices, units);
        using var gate = new SemaphoreSlim(0);
        bool udp = false;
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices), async play =>
        {
            udp = play.Udp;
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
            UdpOnly = true,
            Ssrc = SsrcOf(units[0][0]),
        };

        using var player = new LivePlayer(new RtspSource(server.Uri));
        player.Start();
        for (int i = 0; i < units.Count; i++)
        {
            if (expected[i] is { } pixels)
            {
                var frame = WaitFor(player, player.TakeLatest);
                Assert.Equal(pixels, frame.Pixels);
                player.Return(frame);
            }

            gate.Release();
        }

        Assert.True(udp);
        var statistics = player.Statistics;
        Assert.Equal((0L, 0L, 0L, 0L), (statistics.LostPackets, statistics.DamagedFrames, statistics.SkippedFrames, statistics.Reconnects));

        // SETUP twice: RTP over the connection (461), then over UDP from an even port and the next.
        var setups = server.Requests.Where(r => r.Method == "SETUP").Select(r => r.Headers["Transport"]).ToList();
        Assert.Equal(2, setups.Count);
        Assert.StartsWith("RTP/AVP/TCP", setups[0], StringComparison.Ordinal);
        var ports = System.Text.RegularExpressions.Regex.Match(setups[1], @"client_port=(\d+)-(\d+)");
        int rtp = int.Parse(ports.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal((0, rtp + 1), (rtp % 2, int.Parse(ports.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)));

        // The punch packets: an RTP header and an empty receiver report, one to each of the server's ports.
        WaitFor(player, () => server.Punches.Count >= 2 ? server : null);
        Assert.Equal([("rtcp", 8), ("rtp", 12)], server.Punches.Select(p => (p.Port, p.Data.Length)).OrderBy(p => p.Port));
    }

    [Fact]
    public void ShowsJpegOverUdp()
    {
        var frames = LiveRtspJpegTests.Frames(RtpJpegDepacketizerTests.GStreamer("jpeg-420-restart"));
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(JpegSdp, async play =>
        {
            foreach (var frame in frames)
            {
                foreach (var packet in frame)
                {
                    await play.SendAsync(packet);
                }

                await gate.WaitAsync(play.Token);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        })
        {
            UdpOnly = true,
        };

        using var player = new LivePlayer(new RtspSource(server.Uri));
        player.Start();
        for (int i = 0; i < frames.Count; i++)
        {
            var shown = WaitFor(player, player.TakeLatest);
            Assert.Equal(LiveRtspJpegTests.Expected("jpeg-420-restart", i), shown.Pixels);
            player.Return(shown);
            gate.Release();
        }

        Assert.Equal((0L, 0L), (player.Statistics.LostPackets, player.Statistics.DamagedFrames));
    }

    [Fact]
    public void PutsPacketsBackInOrderAndIgnoresDuplicatesAndStrangers()
    {
        // Each frame's second and third packets swapped, every packet sent twice, and between them packets of another
        // SSRC and from another address, numbered as the next packet: taken in, they would push that one out as a
        // duplicate. None of it may change a frame or count as lost.
        var frames = LiveRtspJpegTests.Frames(RtpJpegDepacketizerTests.GStreamer("jpeg-422"));
        uint ssrc = SsrcOf(frames[0][0]);
        using var stranger = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(JpegSdp, async play =>
        {
            foreach (var frame in frames)
            {
                var order = frame.ToList();
                (order[1], order[2]) = (order[2], order[1]);
                foreach (var packet in order)
                {
                    await play.SendAsync(packet);
                    await play.SendAsync(packet);
                    await play.SendAsync(WithSsrc(Garbled(packet), ssrc + 1));
                    await stranger.SendAsync(Garbled(packet), play.UdpClient!, play.Token);
                }

                await gate.WaitAsync(play.Token);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        })
        {
            UdpOnly = true,
            Ssrc = ssrc,
        };

        using var player = new LivePlayer(new RtspSource(server.Uri));
        player.Start();
        for (int i = 0; i < frames.Count; i++)
        {
            var shown = WaitFor(player, player.TakeLatest);
            Assert.Equal(LiveRtspJpegTests.Expected("jpeg-422", i), shown.Pixels);
            player.Return(shown);
            gate.Release();
        }

        Assert.Equal((0L, 0L), (player.Statistics.LostPackets, player.Statistics.DamagedFrames));
    }

    [Fact]
    public void CountsAPacketLaterThanTheWindowAsLost()
    {
        // The third frame's second packet comes 300 ms after the rest of the frame (the window is 50 ms): by then its
        // frame was given up, incomplete, and the packet itself is dropped.
        var frames = LiveRtspJpegTests.Frames(RtpJpegDepacketizerTests.GStreamer("jpeg-422"));
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(JpegSdp, async play =>
        {
            for (int f = 0; f < frames.Count; f++)
            {
                for (int i = 0; i < frames[f].Count; i++)
                {
                    if (f != 2 || i != 1)
                    {
                        await play.SendAsync(frames[f][i]);
                    }
                }

                if (f == 2)
                {
                    await Task.Delay(300, play.Token);
                    await play.SendAsync(frames[f][1]);
                }

                await gate.WaitAsync(play.Token);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        })
        {
            UdpOnly = true,
        };

        using var player = new LivePlayer(new RtspSource(server.Uri));
        player.Start();
        foreach (int i in new[] { 0, 1, 3, 4 })
        {
            if (i == 3)
            {
                WaitFor(player, () => player.Statistics.DamagedFrames == 1 ? player : null);
                Thread.Sleep(400); // past the late packet
                gate.Release();
            }

            var shown = WaitFor(player, player.TakeLatest);
            Assert.Equal(LiveRtspJpegTests.Expected("jpeg-422", i), shown.Pixels);
            player.Return(shown);
            gate.Release();
        }

        Assert.Equal((1L, 1L, 4L), (player.Statistics.LostPackets, player.Statistics.DamagedFrames, player.Statistics.DecodedFrames));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void CountsExactlyThePacketsARandomNetworkDrops(int seed)
    {
        // A network by the seed: of the three middle frames' packets a quarter are dropped and a fifth sent twice, and
        // every frame after the first arrives shuffled. The frames that lost nothing are shown as they were sent, the
        // others are dropped, and the packets counted lost are the ones dropped, no more (a duplicate, a late packet)
        // and no fewer. tools/rtsp-nightly.py does the same to mediamtx's stream (tools/rtsp-lossy-relay.py).
        var frames = LiveRtspJpegTests.Frames(RtpJpegDepacketizerTests.GStreamer("jpeg-420-restart"));
        var random = new Random(seed);
        var sent = new List<List<byte[]>>();
        var whole = new List<bool>();
        int dropped = 0;
        for (int f = 0; f < frames.Count; f++)
        {
            bool middle = f > 0 && f < frames.Count - 1;
            var packets = new List<byte[]>();
            foreach (var packet in frames[f])
            {
                double choice = middle ? random.NextDouble() : 1;
                if (choice < 0.25)
                {
                    dropped++;
                    continue;
                }

                packets.Add(packet);
                if (choice < 0.45)
                {
                    packets.Add(packet);
                }
            }

            whole.Add(packets.Distinct().Count() == frames[f].Count);
            if (f > 0)
            {
                random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(packets));
            }

            sent.Add(packets);
        }

        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(JpegSdp, async play =>
        {
            for (int f = 0; f < sent.Count; f++)
            {
                foreach (var packet in sent[f])
                {
                    await play.SendAsync(packet);
                }

                if (whole[f])
                {
                    await gate.WaitAsync(play.Token);
                }
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        })
        {
            UdpOnly = true,
        };

        using var player = new LivePlayer(new RtspSource(server.Uri));
        player.Start();
        for (int f = 0; f < frames.Count; f++)
        {
            if (whole[f])
            {
                var shown = WaitFor(player, player.TakeLatest);
                Assert.Equal(LiveRtspJpegTests.Expected("jpeg-420-restart", f), shown.Pixels);
                player.Return(shown);
                gate.Release();
            }
        }

        var statistics = player.Statistics;
        Assert.Equal((dropped, whole.Count(w => !w), whole.Count(w => w)), (statistics.LostPackets, statistics.DamagedFrames, statistics.DecodedFrames));
        Assert.Equal((LiveState.Playing, 1), (player.State, server.Connections));
    }

    [Fact]
    public void PlaysOnWhenTheFirstIdrPictureLostAPacket()
    {
        // The 720p recording, whose parameter sets come only in the SDP, twice over; a packet of the first IDR picture
        // never arrives. That unit, which the parameter sets came before, is skipped while the player waits for an IDR
        // picture: the second pass's must bring them again, or no picture is ever shown on this connection.
        const string Name = "h264-bench-720p";
        var once = RtpH264DepacketizerTests.Recording(Name);
        const uint length = 90000 * 2; // its 50 frames at 25 a second
        var packets = once.Concat(once.Select((p, i) => Renumbered(p, (ushort)(SequenceOf(once[0]) + once.Count + i), TimestampOf(p) + length))).ToList();
        var units = LiveRtspTests.AccessUnitsOf(packets);
        var expected = LiveRtspTests.ExpectedFrames(Name, units);
        int second = units.FindIndex(u => SequenceOf(u[0]) == (ushort)(SequenceOf(once[0]) + once.Count));
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Name), async play =>
        {
            for (int i = 0; i < units.Count; i++)
            {
                foreach (var packet in units[i].Where(p => p != units[0][1]))
                {
                    await play.SendAsync(packet);
                }

                if (i >= second)
                {
                    await gate.WaitAsync(play.Token);
                }
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        })
        {
            UdpOnly = true,
            Ssrc = SsrcOf(once[0]),
        };

        using var player = new LivePlayer(new RtspSource(server.Uri) { MaxLag = TimeSpan.FromHours(1), StallTimeout = TimeSpan.FromMinutes(10) });
        player.Start();
        for (int i = second; i < units.Count; i++)
        {
            var frame = WaitFor(player, player.TakeLatest);
            Assert.Equal(expected[i], frame.Pixels);
            player.Return(frame);
            gate.Release();
        }

        var statistics = player.Statistics;
        Assert.Equal((1L, 0L), (statistics.LostPackets, statistics.Reconnects));
        Assert.True(statistics.SkippedFrames >= second - 1, $"{statistics.SkippedFrames} skipped.");
    }

    [Fact]
    public void SaysUdpWasInUseWhenNothingCame()
    {
        using var server = new RtspTestServer(JpegSdp, play => Task.Delay(Timeout.Infinite, play.Token)) { UdpOnly = true };
        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new RtspSource(server.Uri) { StallTimeout = TimeSpan.FromSeconds(3) }, clock);
        player.Start();
        WaitFor(player, () => server.Requests.Any(r => r.Method == "PLAY") && clock.WaitingFor.Contains(TimeSpan.FromSeconds(3)) ? player : null);
        clock.Advance(TimeSpan.FromSeconds(3));
        WaitFor(player, () => player.State == LiveState.Reconnecting ? player : null);
        var error = Assert.IsType<TimeoutException>(player.Error);
        Assert.Contains("UDP", error.Message, StringComparison.Ordinal);
    }

    private static string JpegSdp => "v=0\r\ns=-\r\nt=0 0\r\nm=video 0 RTP/AVP 26\r\na=control:trackID=1\r\n";

    private static ushort SequenceOf(byte[] packet) => (ushort)((packet[2] << 8) | packet[3]);

    private static uint TimestampOf(byte[] packet) => (uint)((packet[4] << 24) | (packet[5] << 16) | (packet[6] << 8) | packet[7]);

    private static byte[] Renumbered(byte[] packet, ushort sequence, uint timestamp)
    {
        var copy = (byte[])packet.Clone();
        copy[2] = (byte)(sequence >> 8);
        copy[3] = (byte)sequence;
        copy[4] = (byte)(timestamp >> 24);
        copy[5] = (byte)(timestamp >> 16);
        copy[6] = (byte)(timestamp >> 8);
        copy[7] = (byte)timestamp;
        return copy;
    }

    private static uint SsrcOf(byte[] packet) => (uint)((packet[8] << 24) | (packet[9] << 16) | (packet[10] << 8) | packet[11]);

    private static byte[] WithSsrc(byte[] packet, uint ssrc)
    {
        var copy = (byte[])packet.Clone();
        copy[8] = (byte)(ssrc >> 24);
        copy[9] = (byte)(ssrc >> 16);
        copy[10] = (byte)(ssrc >> 8);
        copy[11] = (byte)ssrc;
        return copy;
    }

    // The packet numbered as the next one, its payload overwritten: taken in, it would wreck a frame.
    private static byte[] Garbled(byte[] packet)
    {
        var copy = (byte[])packet.Clone();
        int next = ((copy[2] << 8) | copy[3]) + 1;
        copy[2] = (byte)(next >> 8);
        copy[3] = (byte)next;
        copy.AsSpan(20).Fill(0x5A);
        return copy;
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
