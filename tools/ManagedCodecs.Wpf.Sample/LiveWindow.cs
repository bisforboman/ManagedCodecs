using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ManagedCodecs.Playback;
using ManagedCodecs.Wpf;

namespace ManagedCodecs.WpfSample;

/// <summary>
/// A window that shows the camera with ManagedCodecs.Wpf's <see cref="LiveView"/>, which shows the newest decoded frame
/// on each frame WPF renders (<see cref="CompositionTarget.Rendering"/>); on each of those the status line says what
/// the player is doing.
/// </summary>
internal sealed class LiveWindow : Window
{
    private readonly LivePlayer _player;
    private readonly LiveView _view = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4, 8, 4), FontFamily = new FontFamily("Consolas"), Foreground = Brushes.White };

    public LiveWindow(Uri uri)
    {
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
        _view.Player = _player;
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
        var s = _player.Statistics;
        _status.Text = $"{_player.State}  received {s.ReceivedFrames}  shown {_view.ShownFrames}  skipped {s.SkippedFrames}  dropped {s.DroppedFrames}  "
            + $"damaged {s.DamagedFrames}  reconnects {s.Reconnects}  decode {s.DecodeMilliseconds:F1} ms  convert {s.ConvertMilliseconds:F1} ms"
            + (_player.Error is { } error ? $"  {error.GetType().Name}: {error.Message}" : "");
    }
}
