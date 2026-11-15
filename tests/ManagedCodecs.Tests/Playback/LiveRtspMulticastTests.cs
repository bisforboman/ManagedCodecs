using System.Net;
using System.Net.Sockets;
using ManagedCodecs.Playback;
using ManagedCodecs.Playback.Rtsp;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// LivePlayer against a camera that sends to a multicast group only (issue #527), as mediamtx with
/// <c>rtspTransports: [multicast]</c> does: it answers 461 to RTP over the RTSP connection and over UDP to the reader
/// alone, and to a SETUP for multicast names the group and its ports. The player joins the group, shows every frame
/// as over the other transports, plays the sound from a group of its own, sends its receiver reports to the group,
/// and holds packets to the SSRC the answer names.
/// </summary>
public class LiveRtspMulticastTests
{
    private const string Slices = "h264-main-slices";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Unstalled = TimeSpan.FromMinutes(10);

    [Fact]
    public void JoinsTheGroupAndShowsEachFrameAsTheDecoderDecodesIt()
    {
        var units = LiveRtspTests.AccessUnits(Slices);
        var expected = LiveRtspTests.ExpectedFrames(Slices, units);
        using var gate = new SemaphoreSlim(0);
        using var server = Multicast(new RtspTestServer(RtspTestServer.SdpOf(Slices), async play =>
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
        }));

        using var player = new LivePlayer(new RtspSource(server.Uri) { StallTimeout = Unstalled });
        player.Start();
        foreach (var frame in expected)
        {
            var shown = WaitFor(player, player.TakeLatest);
            Assert.Equal(frame, shown.Pixels);
            player.Return(shown);
            gate.Release();
        }

        Assert.Equal((0L, 0L, LiveState.Playing, 1), (player.Statistics.LostPackets, player.Statistics.DamagedFrames, player.State, server.Connections));

        // Three SETUPs: over the connection, over UDP to this reader, and for multicast, with no ports of the reader's.
        var asked = server.Requests.Where(r => r.Method == "SETUP").Select(r => r.Headers["Transport"]).ToList();
        Assert.Equal(3, asked.Count);
        Assert.StartsWith("RTP/AVP/TCP;unicast;interleaved=0-1", asked[0], StringComparison.Ordinal);
        Assert.StartsWith("RTP/AVP;unicast;client_port=", asked[1], StringComparison.Ordinal);
        Assert.Equal("RTP/AVP;multicast", asked[2]);
        Assert.Empty(server.Punches);
    }

    [Theory]
    [InlineData("aac-stereo-48k")]
    [InlineData("wav-alaw-codes")]
    public void PlaysTheSoundFromAGroupOfItsOwn(string recording)
    {
        var track = RtpAudioDepacketizerTests.Track(recording);
        var packets = RtpH264DepacketizerTests.Recording(recording);
        var expected = LiveRtspAudioTests.Expected(track, packets);
        var video = LiveRtspTests.AccessUnits(Slices)[0];
        using var gate = new SemaphoreSlim(0);
        using var server = Multicast(new RtspTestServer(LiveRtspAudioTests.TwoTracks(recording), async play =>
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
        }));

        using var player = new LivePlayer(new RtspSource(server.Uri) { Audio = true, StallTimeout = Unstalled });
        player.Start();
        Assert.NotNull(WaitFor(player, player.TakeLatest));
        int total = 0;
        foreach (var chunks in expected)
        {
            foreach (var samples in chunks)
            {
                Assert.Equal(samples, WaitFor(player, player.TakeAudio).Samples);
                total++;
            }

            gate.Release();
        }

        Assert.True(total > 0);
        Assert.Equal((total, 0L, 0L), (player.Statistics.DecodedAudioFrames, player.Statistics.DamagedAudioFrames, player.Statistics.LostPackets));
        Assert.Equal(["RTP/AVP;multicast", "RTP/AVP;multicast"], server.Requests.Where(r => r.Method == "SETUP").Select(r => r.Headers["Transport"]).Skip(2));
    }

    [Fact]
    public void SendsItsReceiverReportsToTheGroup()
    {
        var units = LiveRtspTests.AccessUnits(Slices);
        using var server = Multicast(new RtspTestServer(RtspTestServer.SdpOf(Slices), async play =>
        {
            foreach (var packet in units[0])
            {
                await play.SendAsync(packet);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        }));

        // Another member of the group, on its RTCP port: what the player reports arrives there.
        using var member = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        member.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        member.Bind(new IPEndPoint(OperatingSystem.IsWindows() ? IPAddress.Any : server.MulticastGroup!, server.MulticastPort + 1));
        member.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(server.MulticastGroup!, IPAddress.Loopback));
        member.ReceiveTimeout = (int)Patience.TotalMilliseconds;

        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new RtspSource(server.Uri) { StallTimeout = Unstalled }, clock);
        player.Start();
        Assert.NotNull(WaitFor(player, player.TakeLatest));
        WaitFor(player, () => clock.WaitingFor.Contains(TimeSpan.FromSeconds(5)) ? player : null);
        clock.Advance(TimeSpan.FromSeconds(5));

        var report = new byte[1500];
        int length = member.Receive(report);
        Assert.True(length >= 32);
        Assert.Equal(201, report[1]); // a receiver report, about the stream's SSRC
        Assert.Equal(units[0][0].AsSpan(8, 4).ToArray(), report.AsSpan(8, 4).ToArray());
    }

    [Fact]
    public void TakesOnlyTheStreamTheAnswerNames()
    {
        // Another sender to the same group and port, numbered as the next packet: taken in, it would push that one out
        // as a duplicate and wreck the frame. The answer's SSRC keeps it out, whatever address it comes from.
        var frames = LiveRtspJpegTests.Frames(RtpJpegDepacketizerTests.GStreamer("jpeg-422"));
        uint ssrc = (uint)((frames[0][0][8] << 24) | (frames[0][0][9] << 16) | (frames[0][0][10] << 8) | frames[0][0][11]);
        using var gate = new SemaphoreSlim(0);
        using var server = Multicast(new RtspTestServer("v=0\r\ns=-\r\nt=0 0\r\nm=video 0 RTP/AVP 26\r\na=control:trackID=1\r\n", async play =>
        {
            foreach (var frame in frames)
            {
                foreach (var packet in frame)
                {
                    var stranger = (byte[])packet.Clone();
                    int next = ((stranger[2] << 8) | stranger[3]) + 1;
                    (stranger[2], stranger[3], stranger[11]) = ((byte)(next >> 8), (byte)next, (byte)(stranger[11] + 1));
                    stranger.AsSpan(20).Fill(0x5A);
                    await play.SendAsync(packet);
                    await play.SendAsync(stranger);
                }

                await gate.WaitAsync(play.Token);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        }));
        server.Ssrc = ssrc;

        using var player = new LivePlayer(new RtspSource(server.Uri) { StallTimeout = Unstalled });
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
    public void RetriesWhenTheAnswerNamesNoGroup()
    {
        using var server = Multicast(new RtspTestServer(RtspTestServer.SdpOf(Slices), play => Task.Delay(Timeout.Infinite, play.Token)));
        server.MulticastUnnamed = true;
        using var player = new LivePlayer(new RtspSource(server.Uri));
        player.Start();
        WaitFor(player, () => player.State == LiveState.Reconnecting ? player : null);

        Assert.IsType<InvalidDataException>(player.Error);
        Assert.DoesNotContain(server.Requests, r => r.Method == "PLAY");
    }

    [Theory]
    [InlineData("RTP/AVP;multicast;destination=224.1.0.1;port=8002-8003;ttl=127;ssrc=A6AA2A77", "224.1.0.1", 8002, 8003, 127)] // mediamtx 1.21.3
    [InlineData("RTP/AVP;multicast;destination=239.0.0.5;port=5000", "239.0.0.5", 5000, 5001, 1)]
    [InlineData("RTP/AVP;multicast;destination=239.0.0.5;port=5000-5002;ttl=0", "239.0.0.5", 5000, 5002, 1)]
    [InlineData("RTP/AVP;multicast;destination=camera;port=x", null, null, null, 1)]
    [InlineData("RTP/AVP;multicast", null, null, null, 1)]
    public void ReadsTheGroupOfAMulticastAnswer(string header, string? group, int? rtp, int? rtcp, int ttl)
    {
        var transport = RtspTransport.Parse(header);
        Assert.Equal((group, rtp, rtcp, ttl), (transport.Destination?.ToString(), transport.RtpPort, transport.RtcpPort, transport.TimeToLive));
    }

    // The server set to multicast only, with groups and a port of this test's own (other tests run beside it).
    private static RtspTestServer Multicast(RtspTestServer server)
    {
        server.MulticastGroup = new IPAddress([239, 255, (byte)Random.Shared.Next(1, 255), (byte)Random.Shared.Next(1, 200)]);
        server.MulticastPort = 20000 + (2 * Random.Shared.Next(0, 5000));
        return server;
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
