using System.Net.Http;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// LivePlayer against a loopback server serving the JPEGs of an MJPEG sample: every frame it shows is the decoder's
/// for one of them (the decoder is checked against FFmpeg elsewhere), the newest wins, damaged parts are skipped, and a
/// dropped, stalled or refused connection is retried, or not, as docs/decisions/2026-11-10-live-mjpeg-over-http.md
/// says. Stalls and backoffs run on a fake clock.
/// </summary>
public class LivePlayerTests
{
    // The real-clock tests serve their parts behind a gate the test opens, so a loaded runner (CI's tests without hardware
    // intrinsics, every class in parallel) can go past the default 5 s stall between two parts: the player then reconnects
    // and the server starts its parts over, showing frame 0 where the test waits for a later one. Stalls are tested on
    // the fake clock; here no stall may end a connection.
    private static MjpegHttpSource Unstalled(Uri uri) => new(uri) { StallTimeout = TimeSpan.FromMinutes(10) };

    private const string Sample = "testsrc-420.avi";
    private const string Multipart = "multipart/x-mixed-replace; boundary=frame";

    // Long enough for a loaded CI runner.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public static TheoryData<bool, string, string> Layouts() => new()
    {
        { true, "\r\n", Multipart },
        { false, "\r\n", Multipart },
        { true, "\n", Multipart },
        { false, "\n", "multipart/x-mixed-replace;boundary=\"frame\"" },
        { true, "\r\n", "multipart/x-mixed-replace; boundary=--frame" }, // the delimiter's dashes in the header
        { false, "\r\n", "multipart/x-mixed-replace; boundary=other" }, // a boundary the body doesn't use
        { true, "\r\n", "multipart/x-mixed-replace" }, // no boundary at all
        { true, "\r\n", "application/octet-stream" }, // FFmpeg's HTTP server (-f mpjpeg -listen 1)
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void ShowsEachFrameAsTheDecoderDecodesIt(bool length, string newline, string contentType)
    {
        var jpegs = LiveSamples.Jpegs(Sample, 8);
        using var gate = new SemaphoreSlim(0);
        using var server = new LoopbackServer(async (stream, _, token) =>
        {
            // Each part followed by the next delimiter line: without a Content-Length a part ends only there. (Not the
            // closing one after the last, which would end the stream.)
            await LoopbackServer.WriteAsync(stream, LiveSamples.Head(contentType) + $"--frame{newline}", token);
            for (int i = 0; i < jpegs.Length; i++)
            {
                var part = LiveSamples.Part("frame", jpegs[i], length: length, newline: newline);
                await stream.WriteAsync(part.AsMemory($"--frame{newline}".Length), token);
                await LoopbackServer.WriteAsync(stream, $"--frame{newline}", token);
                await gate.WaitAsync(token);
            }

            await Task.Delay(Timeout.Infinite, token);
        });

        using var player = new LivePlayer(Unstalled(server.Uri));
        player.Start();
        foreach (var jpeg in jpegs)
        {
            var frame = WaitFor(player, player.TakeLatest);
            Assert.Equal(LiveSamples.Rgba(jpeg), frame.Pixels);
            Assert.Equal((320, 240), (frame.Width, frame.Height));
            player.Return(frame);
            gate.Release();
        }

        Assert.Equal(LiveState.Playing, player.State);
        var statistics = player.Statistics;
        Assert.Equal((jpegs.Length, jpegs.Length, jpegs.Length), (statistics.ReceivedFrames, statistics.DecodedFrames, statistics.TakenFrames));
        Assert.Equal((0, 0, 0, 0), (statistics.SkippedFrames, statistics.DroppedFrames, statistics.DamagedFrames, statistics.Reconnects));
    }

    [Fact]
    public void ShowsTheNewestFrameWhenTheCameraIsFasterThanTheDecoder()
    {
        var jpegs = LiveSamples.Jpegs(Sample, 40);
        using var server = new LoopbackServer(async (stream, _, token) =>
        {
            await LoopbackServer.WriteAsync(stream, LiveSamples.Head(Multipart), token);
            await stream.WriteAsync(jpegs.SelectMany(j => LiveSamples.Part("frame", j)).ToArray(), token);
            await Task.Delay(Timeout.Infinite, token);
        });

        using var player = new LivePlayer(Unstalled(server.Uri));
        player.Start();
        WaitFor(player, () => player.Statistics is { ReceivedFrames: 40 } s && s.DecodedFrames + s.SkippedFrames == 40 ? player : null);
        var frame = player.TakeLatest();
        Assert.NotNull(frame);
        Assert.Equal(LiveSamples.Rgba(jpegs[^1]), frame.Pixels);
        var statistics = player.Statistics;
        Assert.Equal(statistics.DecodedFrames - 1, statistics.DroppedFrames);
        Assert.Null(player.TakeLatest());
    }

    [Fact]
    public void DropsFramesNotTakenBeforeANewerOne()
    {
        var jpegs = LiveSamples.Jpegs(Sample, 6);
        using var gate = new SemaphoreSlim(0);
        using var server = new LoopbackServer(async (stream, _, token) =>
        {
            await LoopbackServer.WriteAsync(stream, LiveSamples.Head(Multipart), token);
            foreach (var jpeg in jpegs)
            {
                await stream.WriteAsync(LiveSamples.Part("frame", jpeg), token);
                await gate.WaitAsync(token);
            }

            await Task.Delay(Timeout.Infinite, token);
        });

        // A part a second, so that each frame's time tells which it is (the pixels alone don't: a dropped frame's array
        // is reused for a later one).
        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new MjpegHttpSource(server.Uri), clock);
        player.Start();
        for (int i = 1; i <= jpegs.Length; i++)
        {
            WaitFor(player, () => player.Statistics.DecodedFrames == i ? player : null);
            clock.Advance(TimeSpan.FromSeconds(1));
            gate.Release();
        }

        var frame = player.TakeLatest();
        Assert.Equal(TimeSpan.FromSeconds(jpegs.Length - 1), frame!.Time);
        Assert.Equal(LiveSamples.Rgba(jpegs[^1]), frame.Pixels);
        Assert.Equal((jpegs.Length, 0L, jpegs.Length - 1L, 1L), (player.Statistics.DecodedFrames, player.Statistics.SkippedFrames, player.Statistics.DroppedFrames, player.Statistics.TakenFrames));
    }

    [Fact]
    public void SkipsDamagedPartsAndPartsThatArentJpegs()
    {
        var jpegs = LiveSamples.Jpegs(Sample, 2);
        byte[][] parts =
        [
            LiveSamples.Part("frame", jpegs[0]),
            LiveSamples.Part("frame", [0xFF, 0xD8, 1, 2, 3, 4, 0xFF, 0xD9]), // a JPEG in name only
            LiveSamples.Part("frame", jpegs[0].AsSpan(0, 40).ToArray()), // cut inside its headers (cut later, it is concealed)
            LiveSamples.Part("frame", "{\"motion\": true}"u8.ToArray(), type: "application/json"),
            LiveSamples.Part("frame", jpegs[1]),
        ];
        using var gate = new SemaphoreSlim(0);
        using var server = new LoopbackServer(async (stream, _, token) =>
        {
            await LoopbackServer.WriteAsync(stream, LiveSamples.Head(Multipart), token);
            foreach (var part in parts)
            {
                await stream.WriteAsync(part, token);
                await gate.WaitAsync(token);
            }

            await Task.Delay(Timeout.Infinite, token);
        });

        using var player = new LivePlayer(Unstalled(server.Uri));
        player.Start();
        Assert.Equal(LiveSamples.Rgba(jpegs[0]), WaitFor(player, player.TakeLatest).Pixels);
        for (int damaged = 1; damaged <= 3; damaged++)
        {
            gate.Release();
            WaitFor(player, () => player.Statistics.DamagedFrames == damaged ? player : null);
        }

        gate.Release();
        Assert.Equal(LiveSamples.Rgba(jpegs[1]), WaitFor(player, player.TakeLatest).Pixels);
        Assert.Equal(LiveState.Playing, player.State);
        Assert.Equal((5L, 2L, 3L), (player.Statistics.ReceivedFrames, player.Statistics.DecodedFrames, player.Statistics.DamagedFrames));
    }

    [Fact]
    public void ConnectsAgainWhenTheServerEndsTheStream()
    {
        var jpegs = LiveSamples.Jpegs(Sample, 2);
        using var server = new LoopbackServer(async (stream, index, token) =>
        {
            await LoopbackServer.WriteAsync(stream, LiveSamples.Head(Multipart), token);
            await stream.WriteAsync(LiveSamples.Part("frame", jpegs[index]), token);
            if (index > 0)
            {
                await Task.Delay(Timeout.Infinite, token);
            }
        });

        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new MjpegHttpSource(server.Uri), clock);
        player.Start();
        Assert.Equal(LiveSamples.Rgba(jpegs[0]), WaitFor(player, player.TakeLatest).Pixels);
        WaitFor(player, () => player.State == LiveState.Reconnecting && clock.Waiting == 1 ? player : null);
        Assert.IsType<EndOfStreamException>(player.Error);
        Assert.Equal(1, server.Connections);

        clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(LiveSamples.Rgba(jpegs[1]), WaitFor(player, player.TakeLatest).Pixels);
        Assert.Equal(LiveState.Playing, player.State);
        Assert.Equal((2, 1L), (server.Connections, player.Statistics.Reconnects));
    }

    [Fact]
    public void WaitsLongerAfterEachRefusal()
    {
        using var server = new LoopbackServer((stream, _, token) =>
            LoopbackServer.WriteAsync(stream, "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", token));

        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new MjpegHttpSource(server.Uri), clock);
        player.Start();
        foreach (double backoff in new[] { 0.5, 1, 2, 4, 8, 10, 10 })
        {
            // The backoff alone waits: not a connection's stall watchdog (5 s, a length no backoff has).
            WaitFor(player, () => player.State == LiveState.Reconnecting && clock.WaitingFor.SequenceEqual([TimeSpan.FromSeconds(backoff)]) ? player : null);
            int connections = server.Connections;
            Assert.IsType<HttpRequestException>(player.Error);
            clock.Advance(TimeSpan.FromSeconds(backoff) - TimeSpan.FromMilliseconds(1));
            Thread.Sleep(20);
            Assert.Equal(connections, server.Connections);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            WaitFor(player, () => server.Connections == connections + 1 ? player : null);
        }
    }

    [Fact]
    public void StartsTheWaitAgainOnceAFramePlays()
    {
        var jpegs = LiveSamples.Jpegs(Sample, 1);
        using var server = new LoopbackServer(async (stream, index, token) =>
        {
            if (index < 2)
            {
                await LoopbackServer.WriteAsync(stream, "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", token);
                return;
            }

            await LoopbackServer.WriteAsync(stream, LiveSamples.Head(Multipart), token);
            await stream.WriteAsync(LiveSamples.Part("frame", jpegs[0]), token);
        });

        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new MjpegHttpSource(server.Uri), clock);
        player.Start();
        foreach (double backoff in new[] { 0.5, 1 })
        {
            WaitFor(player, () => player.State == LiveState.Reconnecting && clock.WaitingFor.SequenceEqual([TimeSpan.FromSeconds(backoff)]) ? player : null);
            clock.Advance(TimeSpan.FromSeconds(backoff));
        }

        // The third connection played a frame, then ended: the next wait is the first's again, not 2 s.
        Assert.NotNull(WaitFor(player, player.TakeLatest));
        WaitFor(player, () => player.State == LiveState.Reconnecting && clock.WaitingFor.SequenceEqual([TimeSpan.FromSeconds(0.5)]) ? player : null);
        Assert.IsType<EndOfStreamException>(player.Error);
    }

    [Fact]
    public void ConnectsAgainWhenTheStreamStalls()
    {
        var jpegs = LiveSamples.Jpegs(Sample, 3);
        using var gate = new SemaphoreSlim(0);
        using var server = new LoopbackServer(async (stream, index, token) =>
        {
            await LoopbackServer.WriteAsync(stream, LiveSamples.Head(Multipart), token);
            await stream.WriteAsync(LiveSamples.Part("frame", jpegs[index]), token);
            if (index == 0)
            {
                await gate.WaitAsync(token);
                await stream.WriteAsync(LiveSamples.Part("frame", jpegs[1]), token);
            }

            await Task.Delay(Timeout.Infinite, token);
        });

        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new MjpegHttpSource(server.Uri) { StallTimeout = TimeSpan.FromSeconds(3) }, clock);
        player.Start();
        Assert.Equal(LiveSamples.Rgba(jpegs[0]), WaitFor(player, player.TakeLatest).Pixels);

        // A part at 2 s: the stall is counted from it, so the connection holds until 5 s.
        WaitFor(player, () => clock.Waiting == 1 ? player : null);
        clock.Advance(TimeSpan.FromSeconds(2));
        gate.Release();
        Assert.Equal(LiveSamples.Rgba(jpegs[1]), WaitFor(player, player.TakeLatest).Pixels);
        clock.Advance(TimeSpan.FromSeconds(2.9));
        WaitFor(player, () => clock.WaitingFor.SequenceEqual([TimeSpan.FromSeconds(0.1)]) ? player : null);
        Assert.Equal(LiveState.Playing, player.State);
        clock.Advance(TimeSpan.FromSeconds(0.1));
        WaitFor(player, () => player.State == LiveState.Reconnecting ? player : null);
        Assert.IsType<TimeoutException>(player.Error);

        WaitFor(player, () => clock.WaitingFor.SequenceEqual([TimeSpan.FromSeconds(0.5)]) ? player : null);
        clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(LiveSamples.Rgba(jpegs[1]), WaitFor(player, player.TakeLatest).Pixels);
    }

    [Theory]
    [InlineData("401 Unauthorized", typeof(HttpRequestException))]
    [InlineData("403 Forbidden", typeof(HttpRequestException))]
    [InlineData("404 Not Found", typeof(HttpRequestException))]
    [InlineData("200 OK\r\nContent-Type: image/jpeg", typeof(NotSupportedException))] // a snapshot URL
    [InlineData("200 OK\r\nContent-Type: text/html", typeof(NotSupportedException))] // a login page
    public void GivesUpOnAnswersARetryWontChange(string status, Type error)
    {
        using var server = new LoopbackServer((stream, _, token) =>
            LoopbackServer.WriteAsync(stream, $"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", token));

        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new MjpegHttpSource(server.Uri), clock);
        player.Start();
        WaitFor(player, () => player.State == LiveState.Failed ? player : null);
        Assert.IsType(error, player.Error);
        clock.Advance(TimeSpan.FromMinutes(1));
        Thread.Sleep(20);
        Assert.Equal((1, LiveState.Failed), (server.Connections, player.State));
    }

    [Fact]
    public void StopsOnDispose()
    {
        using var server = new LoopbackServer(async (stream, _, token) =>
        {
            await LoopbackServer.WriteAsync(stream, LiveSamples.Head(Multipart), token);
            await Task.Delay(Timeout.Infinite, token);
        });

        var player = new LivePlayer(new MjpegHttpSource(server.Uri));
        player.Start();
        WaitFor(player, () => server.Connections == 1 ? player : null);
        player.Dispose();
        Assert.Equal(LiveState.Stopped, player.State);
        Assert.Throws<ObjectDisposedException>(player.Start);

        var unstarted = new LivePlayer(new MjpegHttpSource(server.Uri));
        unstarted.Dispose();
        Assert.Equal(LiveState.Stopped, unstarted.State);
    }

    [Fact]
    public void RefusesAUrlThatIsntHttp()
    {
        Assert.Throws<ArgumentException>(() => new MjpegHttpSource(new Uri("rtsp://camera/stream")));
        Assert.Throws<ArgumentException>(() => new MjpegHttpSource(new Uri("/video.mjpg", UriKind.Relative)));
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
