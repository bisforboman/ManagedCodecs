using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Wpf;

/// <summary>
/// Shows a <see cref="LivePlayer"/>'s newest frame, drawn as an <see cref="Image"/> draws its source
/// (<see cref="Stretch"/>, <see cref="StretchDirection"/>). On each frame WPF renders while the control is loaded
/// (<see cref="CompositionTarget.Rendering"/>), it takes the newest decoded frame, if there is one
/// (<see cref="LivePlayer.TakeLatest"/>), writes it into a <see cref="WriteableBitmap"/> and gives it back; frames the
/// player decoded in between are dropped, as a live picture should be. WPF has no RGBA format, so each frame's red and
/// blue are swapped into BGRA first, in place: the frame is the control's until it goes back to the player.
/// </summary>
/// <remarks>
/// The application makes the player (its source: MJPEG over HTTP, RTSP or RTSPS, with credentials, a certificate pin,
/// sound), starts it and disposes it: the control only shows frames, and never starts, stops or disposes the player
/// (docs/decisions/2026-11-15-live-controls.md). Sound, if any, is the application's to play
/// (<see cref="LivePlayer.TakeAudio"/>). For the UI thread only.
/// </remarks>
public sealed class LiveView : FrameworkElement
{
    /// <summary>The player whose frames are shown; null shows nothing.</summary>
    public static readonly DependencyProperty PlayerProperty =
        DependencyProperty.Register(nameof(Player), typeof(LivePlayer), typeof(LiveView));

    /// <summary>How the picture fills the control, as <see cref="Image.StretchProperty"/>: <see cref="Stretch.Uniform"/> by default.</summary>
    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(nameof(Stretch), typeof(Stretch), typeof(LiveView), new FrameworkPropertyMetadata(Stretch.Uniform, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Whether the picture may be scaled up, down or both, as <see cref="Image.StretchDirectionProperty"/>: both by default.</summary>
    public static readonly DependencyProperty StretchDirectionProperty =
        DependencyProperty.Register(nameof(StretchDirection), typeof(StretchDirection), typeof(LiveView), new FrameworkPropertyMetadata(StretchDirection.Both, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private WriteableBitmap? _bitmap;
    private bool _subscribed;

    /// <summary>A view that shows frames while it is loaded.</summary>
    public LiveView()
    {
        Loaded += (_, _) => Subscribe(true);
        Unloaded += (_, _) => Subscribe(false);
    }

    /// <summary>The player whose frames are shown; null shows nothing. The application starts and disposes it.</summary>
    public LivePlayer? Player
    {
        get => (LivePlayer?)GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    /// <inheritdoc cref="StretchProperty"/>
    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    /// <inheritdoc cref="StretchDirectionProperty"/>
    public StretchDirection StretchDirection
    {
        get => (StretchDirection)GetValue(StretchDirectionProperty);
        set => SetValue(StretchDirectionProperty, value);
    }

    /// <summary>The frames shown since the control was made.</summary>
    public long ShownFrames { get; private set; }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize) =>
        _bitmap is { } bitmap ? Fit(availableSize, new Size(bitmap.PixelWidth, bitmap.PixelHeight), Stretch, StretchDirection) : default;

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize) =>
        _bitmap is { } bitmap ? Fit(finalSize, new Size(bitmap.PixelWidth, bitmap.PixelHeight), Stretch, StretchDirection.Both) : default;

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_bitmap is not { } bitmap || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // As Image draws its source: scaled by Stretch, centred, and cut to the control.
        var size = Fit(new Size(ActualWidth, ActualHeight), new Size(bitmap.PixelWidth, bitmap.PixelHeight), Stretch, StretchDirection);
        var destination = new Rect((ActualWidth - size.Width) / 2, (ActualHeight - size.Height) / 2, size.Width, size.Height);
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        drawingContext.DrawImage(bitmap, destination);
        drawingContext.Pop();
    }

    /// <summary>The size a <paramref name="source"/>-sized picture is drawn at in <paramref name="available"/>, as Image sizes its source.</summary>
    internal static Size Fit(Size available, Size source, Stretch stretch, StretchDirection direction)
    {
        double scaleX = 1;
        double scaleY = 1;
        bool widthFree = double.IsInfinity(available.Width);
        bool heightFree = double.IsInfinity(available.Height);
        if (stretch != Stretch.None && (!widthFree || !heightFree) && source.Width > 0 && source.Height > 0)
        {
            scaleX = available.Width / source.Width;
            scaleY = available.Height / source.Height;
            if (widthFree)
            {
                scaleX = scaleY;
            }
            else if (heightFree)
            {
                scaleY = scaleX;
            }
            else if (stretch == Stretch.Uniform)
            {
                scaleX = scaleY = Math.Min(scaleX, scaleY);
            }
            else if (stretch == Stretch.UniformToFill)
            {
                scaleX = scaleY = Math.Max(scaleX, scaleY);
            }

            if (direction == StretchDirection.UpOnly)
            {
                (scaleX, scaleY) = (Math.Max(1, scaleX), Math.Max(1, scaleY));
            }
            else if (direction == StretchDirection.DownOnly)
            {
                (scaleX, scaleY) = (Math.Min(1, scaleX), Math.Min(1, scaleY));
            }
        }

        return new Size(source.Width * scaleX, source.Height * scaleY);
    }

    private void Subscribe(bool on)
    {
        if (on && !_subscribed)
        {
            CompositionTarget.Rendering += OnRendering;
        }
        else if (!on && _subscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
        }

        _subscribed = on;
    }

    // Each rendered frame while loaded: the newest decoded frame into the bitmap.
    private void OnRendering(object? sender, EventArgs e)
    {
        if (Player?.TakeLatest() is not { } frame)
        {
            return;
        }

        try
        {
            if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
            {
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                InvalidateMeasure();
            }

            var pixels = frame.Pixels;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), pixels, frame.Width * 4, 0);
        }
        finally
        {
            Player.Return(frame);
        }

        ShownFrames++;
        InvalidateVisual();
    }
}
