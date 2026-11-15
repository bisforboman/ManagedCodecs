using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using ManagedCodecs.Avalonia;
using ManagedCodecs.Playback;

namespace ManagedCodecs.AvaloniaSample;

/// <summary>
/// A window that shows the camera with ManagedCodecs.Avalonia's <see cref="LiveView"/>, which shows the newest decoded
/// frame on each frame Avalonia renders; on each of those the status line says what the player is doing.
/// </summary>
internal sealed class LiveWindow : Window
{
    private readonly LivePlayer _player;
    private readonly Options _options;
    private readonly LiveView _view = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4), FontFamily = FontFamily.Parse("monospace") };

    public LiveWindow(Options options)
    {
        _options = options;
        Title = $"mclive: {options.Uri}";
        Width = 960;
        Height = 600;
        var status = new Border { Background = Brushes.Black, Child = _status };
        DockPanel.SetDock(status, Dock.Bottom);
        _status.Foreground = Brushes.White;
        Content = new DockPanel { Background = Brushes.Black, Children = { status, _view } };

        _player = options.Uri.Scheme is "rtsp" or "rtsps"
            ? new LivePlayer(new RtspSource(options.Uri))
            : new LivePlayer(new MjpegHttpSource(options.Uri));
        _player.Start();
        _view.Player = _player;
        RequestAnimationFrame(OnFrame);
    }

    protected override void OnClosed(EventArgs e)
    {
        _player.Dispose();
        base.OnClosed(e);
    }

    private void OnFrame(TimeSpan time)
    {
        var s = _player.Statistics;
        _status.Text = $"{_player.State}  received {s.ReceivedFrames}  shown {_view.ShownFrames}  skipped {s.SkippedFrames}  dropped {s.DroppedFrames}  "
            + $"damaged {s.DamagedFrames}  reconnects {s.Reconnects}  decode {s.DecodeMilliseconds:F1} ms  convert {s.ConvertMilliseconds:F1} ms"
            + (_player.Error is { } error ? $"  {error.GetType().Name}: {error.Message}" : "");

        // With --frames, a smoke test: exit after them, or at once if the player gave up. Without, the window stays to
        // show the error.
        if (_options.Frames > 0 && (_player.State == LiveState.Failed || _view.ShownFrames >= _options.Frames))
        {
            Console.WriteLine(_status.Text);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(_player.State == LiveState.Failed ? 1 : 0);
            }

            return;
        }

        RequestAnimationFrame(OnFrame);
    }
}
