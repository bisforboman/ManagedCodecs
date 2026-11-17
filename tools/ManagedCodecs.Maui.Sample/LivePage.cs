using ManagedCodecs.Playback;

namespace ManagedCodecs.MauiSample;

/// <summary>The camera, its URL (asked for when not given at the start) and a status line saying what the player is doing.</summary>
/// <remarks>
/// With a number of frames to show (<see cref="LiveOptions.Frames"/>), a check of itself, for a machine nobody
/// watches (.github/workflows/maui.yml): the page takes each frame from the player and hands it to the view's handler,
/// then compares what the platform drew of the view with the decoded frame (<see cref="FrameCheck"/>). After the last
/// frame it writes the status line and the result to standard output (Android's log, tag DOTNET), each starting
/// "mclive-maui:", and exits: 0, 1 if the player gave up, 2 if a frame was drawn wrong.
/// </remarks>
internal sealed class LivePage : ContentPage
{
    private readonly LiveOptions _options;
    private readonly LiveView _view = new();
    private readonly Label _status = new() { TextColor = Colors.White, FontFamily = "monospace", FontSize = 12, Margin = new Thickness(8, 4) };
    private readonly Entry _url = new() { Placeholder = "http://camera/video.mjpg or rtsp://user:password@camera/stream", TextColor = Colors.White, Keyboard = Keyboard.Url };
    private IDispatcherTimer? _timer;
    private LivePlayer? _player;
    private HttpClient? _client;
    private long _cellsRedUnlikeBlue;
    private string? _drawnWrong;
    private bool _checking;
    private bool _done;
#if ANDROID
    private Android.Net.Wifi.WifiManager.MulticastLock? _multicast;
#endif

    public LivePage(LiveOptions options)
    {
        _options = options;
        BackgroundColor = Colors.Black;
        var play = new Button { Text = "Play" };
        play.Clicked += (_, _) => Play();
        _url.Completed += (_, _) => Play();

        var bar = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], Padding = new Thickness(8, 4), IsVisible = options.Uri is null };
        bar.Add(_url, 0);
        bar.Add(play, 1);

        var grid = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
        grid.Add(bar, 0, 0);
        grid.Add(_view, 0, 1);
        grid.Add(_status, 0, 2);
        Content = grid;
        _url.Text = options.Uri?.ToString();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _timer = Dispatcher.CreateTimer();

        // Checking itself, the page takes the frames at about the display's rate; otherwise only the status line.
        _timer.Interval = TimeSpan.FromMilliseconds(_options.Frames > 0 ? 15 : 250);
        _timer.Tick += OnTick;
        _timer.Start();
        if (_options.Uri is not null)
        {
            Play();
        }
    }

    protected override void OnDisappearing()
    {
        _timer?.Stop();
        Stop();
        base.OnDisappearing();
    }

    private void Play()
    {
        if (!LiveOptions.TryUri(_url.Text, out var uri))
        {
            _status.Text = LiveOptions.Usage;
            return;
        }

        Stop();
        LivePlayer player;
        try
        {
            player = uri.Scheme is "rtsp" or "rtsps"
                ? new LivePlayer(new RtspSource(uri) { CertificateHash = _options.CertificateHash })
                : new LivePlayer(MjpegSource(uri));
        }
        catch (ArgumentException e)
        {
            _status.Text = e.Message;
            return;
        }

#if ANDROID
        // RTSP multicast: Android's Wi-Fi drops packets to a group unless an app holds this lock (docs/decisions/2026-11-17-live-maui.md).
        if (uri.Scheme is "rtsp" or "rtsps" && Android.App.Application.Context.GetSystemService(Android.Content.Context.WifiService) is Android.Net.Wifi.WifiManager wifi)
        {
            _multicast = wifi.CreateMulticastLock("mclive-maui");
            _multicast?.Acquire();
        }
#endif
        _player = player;
        player.Start();

        // Checking itself, the page feeds the view; otherwise the view reads the player on its own.
        if (_options.Frames == 0)
        {
            _view.Player = player;
        }
        else
        {
            // Filling the view, the picture is drawn over all of it, so the check compares the whole view with the frame.
            _view.Aspect = Aspect.Fill;
        }
    }

    // iOS and Mac Catalyst: MAUI's HttpClient there (NSUrlSession) takes multipart/x-mixed-replace apart itself and
    // hands over one part as image/jpeg, which the player refuses: a client on .NET's own sockets reads the stream as
    // the camera sends it. Elsewhere the player's own client.
    private MjpegHttpSource MjpegSource(Uri uri)
    {
#if IOS || MACCATALYST
        _client = new HttpClient(new SocketsHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        return new MjpegHttpSource(uri, _client);
#else
        return new MjpegHttpSource(uri);
#endif
    }

    private void Stop()
    {
        _view.Player = null;
        _player?.Dispose();
        _player = null;
        _client?.Dispose();
        _client = null;
#if ANDROID
        _multicast?.Release();
        _multicast = null;
#endif
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_player is not { } player || _done)
        {
            return;
        }

        if (_options.Frames > 0 && !_checking && _view.Handler is LiveViewHandler handler && player.TakeLatest() is { } frame)
        {
            _checking = true;
            try
            {
                var decoded = (byte[])frame.Pixels.Clone();
                int width = frame.Width;
                int height = frame.Height;
                try
                {
                    handler.Show(frame);
                }
                finally
                {
                    player.Return(frame);
                }

                if (await handler.DrawnAsync() is { } drawn)
                {
                    _drawnWrong ??= FrameCheck.Compare(decoded, width, height, drawn, ref _cellsRedUnlikeBlue);
                }
            }
            catch (Exception ex)
            {
                _drawnWrong ??= $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                _checking = false;
            }
        }

        var s = player.Statistics;
        _status.Text = $"{player.State}  received {s.ReceivedFrames}  shown {_view.ShownFrames}  skipped {s.SkippedFrames}  dropped {s.DroppedFrames}  "
            + $"damaged {s.DamagedFrames}  reconnects {s.Reconnects}  decode {s.DecodeMilliseconds:F1} ms  convert {s.ConvertMilliseconds:F1} ms"
            + (player.Error is { } error ? $"  {error.GetType().Name}: {error.Message}" : "");

        if (_options.Frames > 0 && (_drawnWrong is not null || player.State == LiveState.Failed || _view.ShownFrames >= _options.Frames))
        {
            _done = true;

            // Not a single cell with red unlike blue would mean the check couldn't tell the two swapped. The passing line
            // only when every frame asked for was shown: a player that gave up has a result of its own.
            _drawnWrong ??= _view.ShownFrames > 0 && _cellsRedUnlikeBlue == 0 ? "no cell had red unlike blue, so the check of their order saw nothing" : null;
            bool failed = _drawnWrong is null && _view.ShownFrames < _options.Frames;
            string result = $"every frame drawn as decoded ({_cellsRedUnlikeBlue} cells with red unlike blue)";
            if (failed)
            {
                result = $"only {_view.ShownFrames} of {_options.Frames} frames shown: the player {player.State}";
            }

            result = _drawnWrong ?? result;
            Console.WriteLine($"mclive-maui: {_status.Text}");
            Console.WriteLine($"mclive-maui: {result}");
            Console.Out.Flush();
            int code = _drawnWrong is not null ? 2 : failed ? 1 : 0;
            Stop();
            Environment.Exit(code);
        }
    }
}
