namespace ManagedCodecs.Thumbnails;

/// <summary>
/// A video frame as a small 8-bit RGBA image, upright (<see cref="VideoThumbnail.Create(Stream, ThumbnailOptions?, CancellationToken)"/>):
/// <see cref="Pixels"/> for an image library of the caller's (JPEG or WebP through SkiaSharp or ImageSharp), or
/// <see cref="SavePng(Stream)"/> without one.
/// </summary>
public sealed class Thumbnail
{
    internal Thumbnail(int width, int height, byte[] pixels, TimeSpan time)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        Time = time;
    }

    /// <summary>The width in pixels.</summary>
    public int Width { get; }

    /// <summary>The height in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// The pixels, row by row from the top, 4 bytes each: R, G, B and A (always 255). The array is the thumbnail's own,
    /// not a copy: changing it changes what <see cref="SavePng(Stream)"/> writes.
    /// </summary>
    public byte[] Pixels { get; }

    /// <summary>When the frame is shown, from the start of the file (ffprobe's <c>start_time</c>).</summary>
    public TimeSpan Time { get; }

    /// <summary>Writes the thumbnail to <paramref name="destination"/> as a PNG file (RGBA, 8 bits a channel).</summary>
    public void SavePng(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Png.Write(destination, Pixels, Width, Height);
    }

    /// <summary>The thumbnail as the bytes of a PNG file.</summary>
    public byte[] ToPng()
    {
        using var memory = new MemoryStream();
        SavePng(memory);
        return memory.ToArray();
    }
}

/// <summary>How <see cref="VideoThumbnail"/> picks and sizes the frame.</summary>
public sealed record ThumbnailOptions
{
    /// <summary>
    /// When the frame is taken, from the start of the file: the first frame shown at or after it. Null (the default):
    /// 10% into the video, at most 10 s, past a fade-in or a title. A time past the end takes the last frame.
    /// </summary>
    public TimeSpan? At { get; init; }

    /// <summary>
    /// The largest width (default 320): the frame as shown (its sample aspect ratio and rotation applied) is scaled
    /// down, aspect kept, to fit <see cref="MaxWidth"/> x <see cref="MaxHeight"/>, and never above its decoded size.
    /// </summary>
    public int MaxWidth { get; init; } = 320;

    /// <summary>The largest height (default 320).</summary>
    public int MaxHeight { get; init; } = 320;

    /// <summary>
    /// Whether to step past a dark frame (default false): while the thumbnail's mean luma is below 10% of white, take
    /// the first frame of the next keyframe instead, up to 10 of them, and the brightest seen if all are dark.
    /// </summary>
    public bool SkipDark { get; init; }

    /// <summary>
    /// The largest frame, in pixels, decoded at all (default 16,777,216: 4K and 5K pass, 8K doesn't): a file declaring
    /// larger frames, in its container, its parameter sets or a JPEG's header, is refused with
    /// <see cref="NotSupportedException"/> before they are decoded. For uploads nobody vetted, below the decoders' own
    /// limits (H.264's 139,264 macroblocks, about 35 million pixels; JPEG's 64 million samples a plane).
    /// </summary>
    public long MaxPixels { get; init; } = 16_777_216;

    /// <summary>The matrix to convert by, in place of the frame's (none said: BT.601, as FFmpeg).</summary>
    public ColorMatrix? Matrix { get; init; }

    /// <summary>The range to convert by, in place of the frame's (none said: limited, MJPEG full).</summary>
    public ColorRange? Range { get; init; }
}
