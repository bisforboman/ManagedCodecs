using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Thumbnails;

/// <summary>
/// A thumbnail of a video file: one frame, decoded from the keyframe before it (<see cref="IDemuxer.Seek"/>), converted
/// to RGB by its own matrix and range, scaled down by area and turned upright. For uploads nobody vetted:
/// <see cref="ThumbnailOptions.MaxPixels"/> refuses large frames before they are decoded, and damaged streams are
/// decoded on with concealment (<see cref="H264Decoder.ConcealErrors"/>), an undamaged frame taken where there is one.
/// </summary>
public static class VideoThumbnail
{
    /// <summary>A thumbnail of the file at <paramref name="path"/>, by the default options.</summary>
    /// <inheritdoc cref="Create(Stream, ThumbnailOptions?, CancellationToken)"/>
    public static Thumbnail Create(string path) => Create(path, null);

    /// <summary>A thumbnail of the file at <paramref name="path"/>.</summary>
    /// <inheritdoc cref="Create(Stream, ThumbnailOptions?, CancellationToken)"/>
    public static Thumbnail Create(string path, ThumbnailOptions? options)
    {
        using var stream = File.OpenRead(path);
        return Create(stream, options);
    }

    /// <summary>
    /// A thumbnail of the video file in <paramref name="stream"/>, which must be seekable (as for
    /// <see cref="VideoProbe.Read(Stream)"/>: buffer a request body first). The stream is left open.
    /// </summary>
    /// <exception cref="ArgumentException">The stream is not seekable, or an option is out of range.</exception>
    /// <exception cref="InvalidDataException">The file is not AVI or MP4, is malformed, has no video, or no frame decodes.</exception>
    /// <exception cref="NotSupportedException">
    /// The video is in a codec this library does not decode, or its frames are larger than
    /// <see cref="ThumbnailOptions.MaxPixels"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Thumbnail Create(Stream stream, ThumbnailOptions? options = null, CancellationToken cancellationToken = default)
    {
        options = Validate(options);
        using var demuxer = Open(stream);
        using var picker = new FramePicker(demuxer, options);
        foreach (var packet in demuxer.ReadPackets())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (picker.Add(packet))
            {
                break;
            }
        }

        return picker.Finish();
    }

    /// <summary>
    /// A thumbnail of the video file in <paramref name="stream"/>, its packets read asynchronously
    /// (<see cref="IDemuxer.ReadPacketsAsync"/>); the container's headers and index are read as by
    /// <see cref="Create(Stream, ThumbnailOptions?, CancellationToken)"/>, synchronously, and the decoding runs on the
    /// calling thread.
    /// </summary>
    /// <inheritdoc cref="Create(Stream, ThumbnailOptions?, CancellationToken)"/>
    public static async Task<Thumbnail> CreateAsync(Stream stream, ThumbnailOptions? options = null, CancellationToken cancellationToken = default)
    {
        options = Validate(options);
        using var demuxer = Open(stream);
        using var picker = new FramePicker(demuxer, options);
        await foreach (var packet in demuxer.ReadPacketsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (picker.Add(packet))
            {
                break;
            }
        }

        return picker.Finish();
    }

    /// <summary>
    /// The size a <paramref name="width"/> x <paramref name="height"/> frame's thumbnail takes: its size as shown
    /// (<paramref name="sampleAspectRatio"/> and <paramref name="rotation"/> applied) scaled down, aspect kept, into
    /// <paramref name="maxWidth"/> x <paramref name="maxHeight"/>, and no larger than the frame along either side (the
    /// area scaling only scales down), at least one pixel.
    /// </summary>
    internal static (int Width, int Height) Fit(int width, int height, Rational sampleAspectRatio, int rotation, int maxWidth, int maxHeight)
    {
        var (shownWidth, shownHeight) = VideoProbe.DisplaySize(width, height, sampleAspectRatio, rotation);
        var (frameWidth, frameHeight) = rotation is 90 or 270 ? (height, width) : (width, height);
        double scale = Math.Min(1, Math.Min((double)maxWidth / shownWidth, (double)maxHeight / shownHeight));
        scale = Math.Min(scale, Math.Min((double)frameWidth / shownWidth, (double)frameHeight / shownHeight));
        int fitWidth = Math.Clamp((int)Math.Round(shownWidth * scale), 1, Math.Min(maxWidth, frameWidth));
        int fitHeight = Math.Clamp((int)Math.Round(shownHeight * scale), 1, Math.Min(maxHeight, frameHeight));
        return (fitWidth, fitHeight);
    }

    // The options checked before the file is read, so a wrong one is reported as such whatever the file holds.
    private static ThumbnailOptions Validate(ThumbnailOptions? options)
    {
        options ??= new ThumbnailOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxWidth, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxHeight, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPixels, 1, nameof(options));
        if (options.At < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.At, "The time is before the start.");
        }

        return options;
    }

    private static IDemuxer Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Demuxer.Open(stream, leaveOpen: true);
    }

    /// <summary>
    /// Takes the video's packets one at a time, decodes them and keeps the frame to show: the first undamaged one at or
    /// after the time (with <see cref="ThumbnailOptions.SkipDark"/>, the first bright one from there, keyframe by
    /// keyframe), else at the end the first damaged one at or after it, else the last frame decoded.
    /// </summary>
    private sealed class FramePicker : IDisposable
    {
        private const int DarkSteps = 10;

        private readonly ThumbnailOptions _options;
        private readonly VideoStreamInfo _video;
        private readonly IVideoDecoder _decoder;
        private readonly double _start;
        private long _target;
        private bool _waitingForKeyframe;
        private int _darkFrames;
        private VideoFrame? _last;
        private VideoFrame? _damaged;
        private Thumbnail? _result;
        private Thumbnail? _brightest;
        private long _brightestLuma = -1;

        public FramePicker(IDemuxer demuxer, ThumbnailOptions options)
        {
            if (demuxer.Video is not { } video)
            {
                throw new InvalidDataException("The file has no video stream.");
            }

            if (video.Codec is not (CodecIds.H264 or CodecIds.Mjpeg))
            {
                throw new NotSupportedException($"The video is in {video.Codec}, which this library does not decode (H.264 and MJPEG only).");
            }

            var (width, height) = video.Codec == CodecIds.H264 && H264Decoder.FrameSize(video) is { } size ? size : (video.Width, video.Height);
            CheckSize(width, height, options);
            _options = options;
            _video = video;
            _decoder = video.Codec == CodecIds.H264 ? new H264Decoder(video) { ConcealErrors = true } : new MjpegDecoder(video);

            // The time from the file's start, as ffmpeg's -ss: 10% in, at most 10 s, unless said; in the stream's time
            // base rounded up, so the first frame at or after it is the one shown.
            var (duration, start) = VideoProbe.Timing(demuxer);
            _start = start;
            var at = options.At ?? TimeSpan.FromTicks(Math.Min(duration.Ticks / 10, TimeSpan.TicksPerSecond * 10));
            double seconds = start + at.TotalSeconds;
            _target = (long)Math.Ceiling(seconds * video.TimeBase.Denominator / video.TimeBase.Numerator);
            try
            {
                demuxer.Seek(_target, video.StreamIndex);
            }
            catch (NotSupportedException)
            {
                // No index to seek by (AVI's H.264): decode from the start.
            }
        }

        /// <summary>Decodes <paramref name="packet"/> if it is the video's; true once the frame is chosen.</summary>
        public bool Add(Packet packet)
        {
            if (packet.StreamIndex != _video.StreamIndex || (packet.Discard && _video.Codec == CodecIds.Mjpeg))
            {
                return _result is not null;
            }

            // A JPEG's size is read before it is decoded: its header can declare far more than the container's.
            if (_video.Codec == CodecIds.Mjpeg && MjpegDecoder.ImageSize(packet.Data.Span) is { } image)
            {
                CheckSize(image.Width, image.Height, _options, "A JPEG image in the video declares");
            }

            if (_waitingForKeyframe && packet.IsKeyframe)
            {
                // Past a dark frame: the next frame to weigh is the first this keyframe shows.
                (_target, _waitingForKeyframe) = (packet.Pts, false);
            }

            foreach (var frame in _decoder.Decode(packet))
            {
                Take(frame);
            }

            return _result is not null;
        }

        /// <summary>The thumbnail: the frame chosen, or at the end of the packets the best one there is.</summary>
        public Thumbnail Finish()
        {
            if (_result is null)
            {
                foreach (var frame in _decoder.Flush())
                {
                    Take(frame);
                }
            }

            _result ??= _brightest ?? ((_damaged ?? _last) is { } best ? Render(best) : null);
            return _result ?? throw new InvalidDataException("No video frame decoded.");
        }

        public void Dispose()
        {
            if (_damaged is not null && _damaged != _last)
            {
                _damaged.Dispose();
            }

            _last?.Dispose();
            (_decoder as IDisposable)?.Dispose();
        }

        private static void CheckSize(int width, int height, ThumbnailOptions options, string what = "The video's frames are")
        {
            if ((long)width * height > options.MaxPixels)
            {
                throw new NotSupportedException($"{what} {width}x{height}, more than the {options.MaxPixels} pixels allowed (ThumbnailOptions.MaxPixels).");
            }
        }

        private void Take(VideoFrame frame)
        {
            if (_result is not null)
            {
                frame.Dispose();
                return;
            }

            CheckSize(frame.Width, frame.Height, _options);
            if (_last is not null && _last != _damaged)
            {
                _last.Dispose();
            }

            _last = frame;
            if (_waitingForKeyframe || frame.Pts < _target)
            {
                return;
            }

            if (frame.IsDamaged)
            {
                _damaged ??= frame;
                return;
            }

            var thumbnail = Render(frame);
            if (!_options.SkipDark)
            {
                _result = thumbnail;
                return;
            }

            long luma = MeanLuma(thumbnail);
            if (luma > _brightestLuma)
            {
                (_brightest, _brightestLuma) = (thumbnail, luma);
            }

            // Bright: a mean luma of 10% of white or more. Else on to the next keyframe, up to a limit.
            if (luma * 10 >= 255 || ++_darkFrames >= DarkSteps)
            {
                _result = luma * 10 >= 255 ? thumbnail : _brightest;
            }
            else
            {
                _waitingForKeyframe = true;
            }
        }

        private Thumbnail Render(VideoFrame frame)
        {
            var aspect = frame.SampleAspectRatio is { Numerator: > 0, Denominator: > 0 } ratio ? ratio : _video.SampleAspectRatio;
            var (width, height) = Fit(frame.Width, frame.Height, aspect, _video.Rotation, _options.MaxWidth, _options.MaxHeight);
            var converter = RgbaConverter.For(frame, _video.Codec, _options.Matrix, _options.Range);
            byte[] pixels = RgbaImage.Render(frame, width, height, _video.Rotation, converter);
            var time = TimeSpan.FromSeconds(Math.Max(0, (frame.Pts * _video.TimeBase.ToDouble()) - _start));
            return new Thumbnail(width, height, pixels, time);
        }

        // The mean of BT.601's luma over the thumbnail, 0 to 255.
        private static long MeanLuma(Thumbnail thumbnail)
        {
            long sum = 0;
            var pixels = thumbnail.Pixels;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                sum += ((77 * pixels[i]) + (150 * pixels[i + 1]) + (29 * pixels[i + 2])) >> 8;
            }

            return sum / (pixels.Length / 4);
        }
    }
}
