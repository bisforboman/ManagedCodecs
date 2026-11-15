using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Avalonia;

/// <summary>
/// Shows a <see cref="LivePlayer"/>'s newest frame, drawn as an <see cref="Image"/> draws its source
/// (<see cref="Stretch"/>, <see cref="StretchDirection"/>). On each frame Avalonia renders while the control is in a
/// window, it takes the newest decoded frame, if there is one (<see cref="LivePlayer.TakeLatest"/>), copies it into a
/// bitmap and gives it back; frames the player decoded in between are dropped, as a live picture should be.
/// </summary>
/// <remarks>
/// The application makes the player (its source: MJPEG over HTTP, RTSP or RTSPS, with credentials, a certificate pin,
/// sound), starts it and disposes it: the control only shows frames, and never starts, stops or disposes the player
/// (docs/decisions/2026-11-15-live-controls.md). Sound, if any, is the application's to play
/// (<see cref="LivePlayer.TakeAudio"/>). For the UI thread only.
/// </remarks>
public sealed class LiveView : Control
{
    /// <summary>The player whose frames are shown; null shows nothing.</summary>
    public static readonly StyledProperty<LivePlayer?> PlayerProperty =
        AvaloniaProperty.Register<LiveView, LivePlayer?>(nameof(Player));

    /// <summary>How the picture fills the control, as <see cref="Image.StretchProperty"/>: <see cref="Stretch.Uniform"/> by default.</summary>
    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<LiveView, Stretch>(nameof(Stretch), Stretch.Uniform);

    /// <summary>Whether the picture may be scaled up, down or both, as <see cref="Image.StretchDirectionProperty"/>: both by default.</summary>
    public static readonly StyledProperty<StretchDirection> StretchDirectionProperty =
        AvaloniaProperty.Register<LiveView, StretchDirection>(nameof(StretchDirection), StretchDirection.Both);

    private WriteableBitmap? _bitmap;
    private TopLevel? _topLevel;

    static LiveView()
    {
        AffectsRender<LiveView>(StretchProperty, StretchDirectionProperty);
        AffectsMeasure<LiveView>(StretchProperty, StretchDirectionProperty);
    }

    /// <summary>The player whose frames are shown; null shows nothing. The application starts and disposes it.</summary>
    public LivePlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    /// <inheritdoc cref="StretchProperty"/>
    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    /// <inheritdoc cref="StretchDirectionProperty"/>
    public StretchDirection StretchDirection
    {
        get => GetValue(StretchDirectionProperty);
        set => SetValue(StretchDirectionProperty, value);
    }

    /// <summary>The frames shown since the control was made.</summary>
    public long ShownFrames { get; private set; }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        if (_bitmap is not { } bitmap || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        // As Image draws its source: scaled by Stretch, centred, and cut to the control.
        var viewPort = new Rect(Bounds.Size);
        var sourceSize = bitmap.Size;
        var scale = Stretch.CalculateScaling(Bounds.Size, sourceSize, StretchDirection);
        var destination = viewPort.CenterRect(new Rect(sourceSize * scale)).Intersect(viewPort);
        var source = new Rect(sourceSize).CenterRect(new Rect(destination.Size / scale));
        context.DrawImage(bitmap, source, destination);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize) =>
        _bitmap is { } bitmap ? Stretch.CalculateSize(availableSize, bitmap.Size, StretchDirection) : default;

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize) =>
        _bitmap is { } bitmap ? Stretch.CalculateSize(finalSize, bitmap.Size) : default;

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.RequestAnimationFrame(OnFrame);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel = null;
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Copies a frame into <paramref name="bitmap"/>, made anew when it is missing or of another size, row by row (the
    /// bitmap's rows may be padded); true when a new bitmap was made.
    /// </summary>
    internal static bool Copy(RgbaFrame frame, ref WriteableBitmap? bitmap)
    {
        bool made = false;
        if (bitmap is null || bitmap.PixelSize.Width != frame.Width || bitmap.PixelSize.Height != frame.Height)
        {
            bitmap?.Dispose();
            bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormats.Rgba8888, AlphaFormat.Opaque);
            made = true;
        }

        using var buffer = bitmap.Lock();
        int row = frame.Width * 4;
        for (int y = 0; y < frame.Height; y++)
        {
            Marshal.Copy(frame.Pixels, y * row, buffer.Address + (y * buffer.RowBytes), row);
        }

        return made;
    }

    // Each rendered frame while in a window: the newest decoded frame into the bitmap, then the next request.
    private void OnFrame(TimeSpan time)
    {
        if (_topLevel is not { } topLevel)
        {
            return;
        }

        if (Player?.TakeLatest() is { } frame)
        {
            try
            {
                if (Copy(frame, ref _bitmap))
                {
                    InvalidateMeasure();
                }
            }
            finally
            {
                Player.Return(frame);
            }

            ShownFrames++;
            InvalidateVisual();
        }

        topLevel.RequestAnimationFrame(OnFrame);
    }
}
