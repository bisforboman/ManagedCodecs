using ManagedCodecs.Mjpeg;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// LivePlayer on an RTSP camera that sends JPEG (RFC 2435), replaying GStreamer's committed recordings
/// (samples/gstreamer/): every frame shown is the JPEG that was packetized, decoded and converted; an incomplete frame
/// is dropped and counted; a type no frame can be rebuilt from ends the player (docs/decisions/2026-11-12-live-rtsp-jpeg.md).
/// </summary>
public class LiveRtspJpegTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("jpeg-420-restart", "m=video 0 RTP/AVP 26\r\n")] // the static payload type, no a=rtpmap
    [InlineData("jpeg-422", "m=video 0 RTP/AVP 96\r\na=rtpmap:96 JPEG/90000\r\n")]
    public void ShowsEachFrameAsTheJpegThatWasSent(string name, string media)
    {
        var frames = Frames(RtpJpegDepacketizerTests.GStreamer(name));
        int payloadType = media.Contains("96", StringComparison.Ordinal) ? 96 : 26;
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(Sdp(media), async play =>
        {
            foreach (var frame in frames)
            {
                foreach (var packet in frame)
                {
                    packet[1] = (byte)((packet[1] & 0x80) | payloadType);
                    await play.SendAsync(packet);
                }

                await gate.WaitAsync(play.Token);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        });

        using var player = new LivePlayer(new RtspSource(server.Uri));
        player.Start();
        for (int i = 0; i < frames.Count; i++)
        {
            var shown = WaitFor(player, player.TakeLatest);
            Assert.Equal(Expected(name, i), shown.Pixels);
            player.Return(shown);
            gate.Release();
        }

        var statistics = player.Statistics;
        Assert.Equal((5L, 5L, 0L, 0L, 0L), (statistics.ReceivedFrames, statistics.DecodedFrames, statistics.DamagedFrames, statistics.LostPackets, statistics.SkippedFrames));
        Assert.Equal(LiveState.Playing, player.State);
    }

    [Fact]
    public void DropsAnIncompleteFrameAndCountsIt()
    {
        var frames = Frames(RtpJpegDepacketizerTests.GStreamer("jpeg-422"));
        frames[2].RemoveAt(1); // a packet from the middle of the third frame
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(Sdp("m=video 0 RTP/AVP 26\r\n"), async play =>
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
        });

        using var player = new LivePlayer(new RtspSource(server.Uri));
        player.Start();
        foreach (int i in new[] { 0, 1, 3, 4 })
        {
            if (i == 3)
            {
                WaitFor(player, () => player.Statistics.DamagedFrames == 1 ? player : null);
                gate.Release(); // the dropped frame showed nothing: go on to the next
            }

            var shown = WaitFor(player, player.TakeLatest);
            Assert.Equal(Expected("jpeg-422", i), shown.Pixels);
            player.Return(shown);
            gate.Release();
        }

        var statistics = player.Statistics;
        Assert.Equal((5L, 4L, 1L, 1L), (statistics.ReceivedFrames, statistics.DecodedFrames, statistics.DamagedFrames, statistics.LostPackets));
        Assert.Equal(LiveState.Playing, player.State);
    }

    [Fact]
    public void GivesUpOnATypeNoFrameCanBeRebuiltFrom()
    {
        var packet = (byte[])RtpJpegDepacketizerTests.GStreamer("jpeg-422")[0].Clone();
        packet[12 + 4] = 3; // a reserved type
        using var server = new RtspTestServer(Sdp("m=video 0 RTP/AVP 26\r\n"), async play =>
        {
            await play.SendAsync(packet);
            await Task.Delay(Timeout.Infinite, play.Token);
        });

        using var player = new LivePlayer(new RtspSource(server.Uri), new FakeLiveClock());
        player.Start();
        WaitFor(player, () => player.State == LiveState.Failed ? player : null);
        Assert.IsType<NotSupportedException>(player.Error);
        Assert.Equal(1, server.Connections);
    }

    private static string Sdp(string media) => "v=0\r\ns=-\r\nt=0 0\r\n" + media + "a=control:trackID=1\r\n";

    // The packets of each frame, up to its marker bit (copies: a test may rewrite them).
    private static List<List<byte[]>> Frames(List<byte[]> packets)
    {
        var frames = new List<List<byte[]>> { new() };
        foreach (var packet in packets)
        {
            frames[^1].Add((byte[])packet.Clone());
            if ((packet[1] & 0x80) != 0)
            {
                frames.Add([]);
            }
        }

        frames.RemoveAt(frames.Count - 1);
        return frames;
    }

    // The RGBA the player should show for a frame: the JPEG GStreamer was given, decoded and converted as it converts.
    private static byte[] Expected(string name, int frame)
    {
        using var decoded = new MjpegDecoder().Decode(File.ReadAllBytes(RtpJpegDepacketizerTests.SourcePath(name, frame)));
        var pixels = new byte[decoded.Width * decoded.Height * 4];
        YuvToRgba.For(decoded).Convert(decoded, pixels);
        return pixels;
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
