using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManagedCodecs.Playback;
using ManagedCodecs.Wpf;

namespace ManagedCodecs.WpfSample;

/// <summary>
/// A window that shows the camera with ManagedCodecs.Wpf's <see cref="LiveView"/>, which shows the newest decoded frame
/// on each frame WPF renders (<see cref="CompositionTarget.Rendering"/>); on each of those the status line says what
/// the player is doing.
/// </summary>
/// <remarks>
/// With a number of frames to show (<c>--frames</c>), a check of itself, for a machine nobody watches: the window
/// takes each frame from the player and hands it to the control (<c>LiveView.Show</c>), then compares the decoded
/// frame with what WPF draws of the control's bitmap (<see cref="Drawn"/>), so the control's red and blue swap and its
/// rows are what is checked. After the last frame the status line goes to standard output and the application exits:
/// 0, 1 if the player gave up, 2 if a frame was drawn wrong.
/// </remarks>
internal sealed class LiveWindow : Window
{
    private readonly LivePlayer _player;
    private readonly LiveView _view = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4, 8, 4), FontFamily = new FontFamily("Consolas"), Foreground = Brushes.White };
    private readonly int _frames;
    private long _differentRedAndBlue;
    private string? _drawnWrong;

    /// <param name="uri">The camera's URL.</param>
    /// <param name="frames">Exit after this many frames shown, checking each; 0 to run until closed.</param>
    public LiveWindow(Uri uri, int frames = 0)
    {
        _frames = frames;
        Title = $"mclive-wpf: {uri}";
        Width = 960;
        Height = 600;
        var status = new Border { Background = Brushes.Black, Child = _status };
        DockPanel.SetDock(status, Dock.Bottom);
        var panel = new DockPanel { Background = Brushes.Black };
        panel.Children.Add(status);
        panel.Children.Add(_view);
        Content = panel;

        _player = uri.Scheme is "rtsp" or "rtsps"
            ? new LivePlayer(new RtspSource(uri))
            : new LivePlayer(new MjpegHttpSource(uri));
        _player.Start();

        // Checking itself, the window feeds the control; otherwise the control reads the player on its own.
        if (frames == 0)
        {
            _view.Player = _player;
        }

        CompositionTarget.Rendering += OnRendering;
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _player.Dispose();
        base.OnClosed(e);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_frames > 0 && _player.TakeLatest() is { } frame)
        {
            try
            {
                var decoded = (byte[])frame.Pixels.Clone();
                _view.Show(frame);
                _drawnWrong ??= Compare(decoded, Drawn(_view.Bitmap!), frame.Width, ref _differentRedAndBlue);
            }
            finally
            {
                _player.Return(frame);
            }
        }

        var s = _player.Statistics;
        _status.Text = $"{_player.State}  received {s.ReceivedFrames}  shown {_view.ShownFrames}  skipped {s.SkippedFrames}  dropped {s.DroppedFrames}  "
            + $"damaged {s.DamagedFrames}  reconnects {s.Reconnects}  decode {s.DecodeMilliseconds:F1} ms  convert {s.ConvertMilliseconds:F1} ms"
            + (_player.Error is { } error ? $"  {error.GetType().Name}: {error.Message}" : "");

        if (_frames > 0 && (_drawnWrong is not null || _player.State == LiveState.Failed || _view.ShownFrames >= _frames))
        {
            // Not a single pixel with red unlike blue would mean the check couldn't tell the two swapped.
            _drawnWrong ??= _view.ShownFrames > 0 && _differentRedAndBlue == 0 ? "no pixel had red unlike blue, so the check of their order saw nothing" : null;
            Console.WriteLine(_status.Text);
            Console.WriteLine(_drawnWrong ?? $"every frame drawn as decoded ({_differentRedAndBlue} pixels with red unlike blue)");
            CompositionTarget.Rendering -= OnRendering;
            Application.Current.Shutdown(_drawnWrong is not null ? 2 : _player.State == LiveState.Failed ? 1 : 0);
        }
    }

    /// <summary>
    /// What WPF draws of a bitmap, a pixel for each of its own, as blue, green, red, alpha (<c>Pbgra32</c>, WPF's
    /// render target format): not the bytes written into the bitmap read back, but the picture WPF made of them, so a
    /// wrong order of the colours or a wrong stride shows.
    /// </summary>
    private static byte[] Drawn(WriteableBitmap bitmap)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        }

        var target = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var drawn = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        target.CopyPixels(drawn, bitmap.PixelWidth * 4, 0);
        return drawn;
    }

    // The first pixel drawn unlike the decoded frame (red, green, blue from the player; blue, green, red drawn), or null.
    private static string? Compare(byte[] decoded, byte[] drawn, int width, ref long differentRedAndBlue)
    {
        for (int i = 0; i < decoded.Length; i += 4)
        {
            if (decoded[i] != decoded[i + 2])
            {
                differentRedAndBlue++;
            }

            if (drawn[i + 2] != decoded[i] || drawn[i + 1] != decoded[i + 1] || drawn[i] != decoded[i + 2])
            {
                int pixel = i / 4;
                return $"drawn wrong at ({pixel % width}, {pixel / width}): decoded R {decoded[i]} G {decoded[i + 1]} B {decoded[i + 2]}, "
                    + $"drawn R {drawn[i + 2]} G {drawn[i + 1]} B {drawn[i]}";
            }
        }

        return null;
    }
}
