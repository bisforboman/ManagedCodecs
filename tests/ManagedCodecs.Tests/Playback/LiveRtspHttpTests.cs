using System.Net;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// RTSP over HTTP (<see cref="RtspSource.HttpTunnel"/>; docs/decisions/2026-11-16-live-rtsp-http.md) against the
/// loopback server set to take only tunnelled connections: a GET and a POST joined by one cookie, every frame as over
/// the RTSP connection, Digest and the receiver reports inside the POST's base64, over HTTPS too; a refused GET, and a
/// 461 that can't fall back to UDP; the tunnel's default ports.
/// </summary>
public class LiveRtspHttpTests
{
    private const string Slices = "h264-main-slices";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Unstalled = TimeSpan.FromMinutes(10);

    [Fact]
    public void ShowsEachFrameThroughTheTunnel()
    {
        var units = LiveRtspTests.AccessUnits(Slices);
        var expected = LiveRtspTests.ExpectedFrames(Slices, units);
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
            HttpTunnel = true,
            Credential = new NetworkCredential("viewer", "s3cret"),
        };

        using var player = new LivePlayer(new RtspSource(server.Uri, new NetworkCredential("viewer", "s3cret")) { HttpTunnel = true, StallTimeout = Unstalled });
        player.Start();
        foreach (var frame in expected)
        {
            var shown = WaitFor(player, player.TakeLatest);
            Assert.Equal(frame, shown.Pixels);
            player.Return(shown);
            gate.Release();
        }

        Assert.Equal((0L, 0L, 0L, LiveState.Playing), (player.Statistics.LostPackets, player.Statistics.DamagedFrames, player.Statistics.Reconnects, player.State));

        // Two connections: a GET for what the server sends, a POST for the requests, the stream's path and one cookie.
        var http = server.TunnelRequests.ToList();
        Assert.Equal(["GET", "POST"], http.Select(r => r.Method).Order(StringComparer.Ordinal));
        Assert.All(http, r => Assert.Equal("/stream", r.Url));
        Assert.Single(http.Select(r => r.Headers["x-sessioncookie"]).Distinct());
        Assert.Matches("^[A-Za-z0-9]{22}$", http[0].Headers["x-sessioncookie"]);
        Assert.Equal("application/x-rtsp-tunnelled", http.Single(r => r.Method == "GET").Headers["Accept"]);
        Assert.Equal("application/x-rtsp-tunnelled", http.Single(r => r.Method == "POST").Headers["Content-Type"]);
        Assert.Equal(2, server.Connections);

        // Inside, the session as over the RTSP connection, Digest included, RTP interleaved in the GET's answer.
        var methods = server.Requests.Select(r => r.Method).ToList();
        Assert.Equal(["OPTIONS", "OPTIONS", "DESCRIBE", "SETUP", "PLAY"], methods.Take(5));
        Assert.Equal("RTP/AVP/TCP;unicast;interleaved=0-1", server.Requests.Single(r => r.Method == "SETUP").Headers["Transport"]);
        Assert.StartsWith("Digest ", server.Requests.Last(r => r.Method == "PLAY").Headers["Authorization"], StringComparison.Ordinal);
    }

    [Fact]
    public void SendsItsReceiverReportsThroughThePost()
    {
        var units = LiveRtspTests.AccessUnits(Slices);
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices), async play =>
        {
            foreach (var packet in units[0])
            {
                await play.SendAsync(packet);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        })
        {
            HttpTunnel = true,
        };

        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new RtspSource(server.Uri) { HttpTunnel = true, StallTimeout = Unstalled }, clock);
        player.Start();
        Assert.NotNull(WaitFor(player, player.TakeLatest));
        WaitFor(player, () => clock.WaitingFor.Contains(TimeSpan.FromSeconds(5)) ? player : null);
        clock.Advance(TimeSpan.FromSeconds(5));
        WaitFor(player, () => server.Interleaved.Any(p => p.Channel == 1) ? player : null);

        var report = server.Interleaved.First(p => p.Channel == 1).Data;
        Assert.Equal(201, report[1]);
        Assert.Equal(units[0][0].AsSpan(8, 4).ToArray(), report.AsSpan(8, 4).ToArray());
    }

    [Fact]
    public void TunnelsOverHttps()
    {
        using var certificate = LiveRtspsTests.SelfSigned("localhost");
        var packets = RtpH264DepacketizerTests.Recording(Slices);
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices), async play =>
        {
            foreach (var packet in packets)
            {
                await play.SendAsync(packet);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        })
        {
            HttpTunnel = true,
            Certificate = certificate,
        };

        var source = new RtspSource(server.Uri) { HttpTunnel = true, CertificateHash = LiveRtspsTests.Hash(certificate), StallTimeout = Unstalled };
        using var player = new LivePlayer(source);
        player.Start();
        WaitFor(player, () => player.Statistics.DecodedFrames >= 20 ? player : null);
        Assert.Equal((0L, 0L, LiveState.Playing), (player.Statistics.LostPackets, player.Statistics.DamagedFrames, player.State));
        Assert.Equal(2, server.TunnelRequests.Count);
    }

    [Theory]
    [InlineData(404, LiveState.Failed)] // no such stream, or no tunnel: the same on the next try
    [InlineData(401, LiveState.Failed)]
    [InlineData(503, LiveState.Reconnecting)] // busy: tried again
    public void SaysWhatTheGetWasAnswered(int status, LiveState state)
    {
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices), play => Task.Delay(Timeout.Infinite, play.Token))
        {
            HttpTunnel = true,
            TunnelStatus = status,
        };

        using var player = new LivePlayer(new RtspSource(server.Uri) { HttpTunnel = true });
        player.Start();
        WaitFor(player, () => player.State == state ? player : null);

        var error = Assert.IsAssignableFrom<IOException>(player.Error);
        Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(server.TunnelRequests, r => r.Method == "POST");
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void DoesNotFallBackToUdpBesideTheTunnel()
    {
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices), play => Task.Delay(Timeout.Infinite, play.Token))
        {
            HttpTunnel = true,
            UdpOnly = true,
        };

        using var player = new LivePlayer(new RtspSource(server.Uri) { HttpTunnel = true });
        player.Start();
        WaitFor(player, () => player.State == LiveState.Failed ? player : null);

        var error = Assert.IsType<NotSupportedException>(player.Error);
        Assert.Contains("tunnel", error.Message, StringComparison.Ordinal);
        Assert.Single(server.Requests, r => r.Method == "SETUP");
        Assert.Empty(server.Punches);
    }

    [Theory]
    [InlineData("rtsp://camera/stream1", false, 554, null)]
    [InlineData("rtsp://camera/stream1", true, 80, "/stream1")]
    [InlineData("rtsps://camera/stream1", true, 443, "/stream1")]
    [InlineData("rtsp://camera:8080/live/ch0?profile=sub", true, 8080, "/live/ch0?profile=sub")]
    [InlineData("rtsp://camera:554/stream1", true, 554, "/stream1")]
    public void TunnelsToHttpPortUnlessTheUrlGivesOne(string url, bool tunnel, int port, string? path)
    {
        var source = new RtspSource(new Uri(url)) { HttpTunnel = tunnel };
        Assert.Equal((port, path), (source.Port, source.TunnelPath));
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
