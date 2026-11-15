using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ManagedCodecs.Avalonia;
using ManagedCodecs.Playback;
using ManagedCodecs.Tests.Playback;

namespace ManagedCodecs.Tests.Avalonia;

/// <summary>
/// ManagedCodecs.Avalonia's <see cref="LiveView"/> in Avalonia's headless platform with Skia drawing, no display: a
/// window holding it, its player reading a loopback MJPEG server, rendered; the window's pixels are the decoded frame's
/// (docs/decisions/2026-11-15-live-controls.md).
/// </summary>
public sealed class LiveViewTests
{
    private const string Sample = "testsrc-420.avi";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task DrawsThePlayersNewestFrame()
    {
        byte[] jpeg = LiveSamples.Jpegs(Sample, 1)[0];
        byte[] expected = LiveSamples.Rgba(jpeg);
        using var server = new LoopbackServer(async (stream, _, token) =>
        {
            await LoopbackServer.WriteAsync(stream, LiveSamples.Head("multipart/x-mixed-replace; boundary=frame"), token);
            await stream.WriteAsync(LiveSamples.Part("frame", jpeg), token);
            await Task.Delay(Timeout.Infinite, token);
        });
        using var player = new LivePlayer(new MjpegHttpSource(server.Uri) { StallTimeout = TimeSpan.FromMinutes(10) });
        player.Start();

        using var session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
        var (pixels, width, height, shown) = await session.Dispatch(
            async () =>
            {
                // The window the frame's size, so that Uniform draws it one to one.
                var view = new LiveView { Player = player };
                var window = new Window { Width = 320, Height = 240, Content = view, Background = Brushes.Black };
                window.Show();
                var deadline = DateTime.UtcNow + Patience;
                while (view.ShownFrames == 0)
                {
                    Assert.True(DateTime.UtcNow < deadline, $"No frame shown: {player.State}, {player.Error?.Message}");
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(10);
                }

                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing rendered.");
                var result = Read(frame);
                window.Close();
                return (result.Pixels, result.Width, result.Height, view.ShownFrames);
            },
            CancellationToken.None);

        Assert.Equal((320, 240), (width, height));
        Assert.True(shown >= 1);

        // Skia draws the bitmap one to one: each pixel the decoded frame's, within a level (blending opaque pixels).
        int worst = 0;
        for (int i = 0; i < expected.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                worst = Math.Max(worst, Math.Abs(pixels[i + c] - expected[i + c]));
            }
        }

        Assert.True(worst <= 1, $"A pixel differs from the decoded frame's by {worst}.");
    }

    [Fact]
    public void TakesNoRoomWithoutAFrame()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
        var size = session.Dispatch(
            () =>
            {
                var view = new LiveView();
                view.Measure(new Size(640, 480));
                return view.DesiredSize;
            },
            CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(default, size);
    }

    // The captured frame's pixels as RGBA, whatever the bitmap's own format (Skia's is BGRA on most platforms).
    private static (byte[] Pixels, int Width, int Height) Read(WriteableBitmap bitmap)
    {
        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        var pixels = new byte[width * height * 4];
        using var buffer = bitmap.Lock();
        bool bgra = buffer.Format == global::Avalonia.Platform.PixelFormats.Bgra8888;
        var row = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (y * buffer.RowBytes), row, 0, row.Length);
            for (int x = 0; x < width; x++)
            {
                int i = x * 4;
                int o = ((y * width) + x) * 4;
                pixels[o] = bgra ? row[i + 2] : row[i];
                pixels[o + 1] = row[i + 1];
                pixels[o + 2] = bgra ? row[i] : row[i + 2];
                pixels[o + 3] = row[i + 3];
            }
        }

        return (pixels, width, height);
    }

    /// <summary>The application the headless session runs: Skia drawing, so that a capture holds real pixels.</summary>
    public sealed class TestApp : Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<TestApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
