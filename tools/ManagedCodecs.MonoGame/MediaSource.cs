using System.Collections.Concurrent;
using ManagedCodecs.Aac;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.MonoGame;

/// <summary>A decoded video frame as RGBA and when to show it, in seconds from the start.</summary>
/// <param name="AspectRatio">The width of a sample over its height (1 when unknown): the frame is shown this much wider.</param>
internal sealed record RgbaFrame(double Time, int Width, int Height, byte[] Pixels, double AspectRatio);

/// <summary>
/// Demuxes and decodes a file on a thread of its own, ahead of playback: video frames converted to RGBA, audio as
/// interleaved 16-bit stereo (MonoGame's dynamic sound takes mono or stereo; more channels are downmixed to the first
/// two). Both queues are bounded, so the decoder waits when playback falls behind. A frame's pixels come back through
/// <see cref="Return"/> once shown or dropped, and the next frame of the size is converted into them: a 720p frame's
/// 3.6 MB would otherwise be a new array on the large object heap each frame, a gen 2 collection every few.
/// </summary>
internal sealed class MediaSource : IDisposable
{
    private readonly IDemuxer _demuxer;
    private readonly IVideoDecoder _video;
    private readonly IAudioDecoder? _audio;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();

    // Pixel arrays given back, for the frames to come. Frames holds at most 8 and a player keeps one or two, so a few
    // spare cover the steady state; more are left to the collector.
    private readonly ConcurrentBag<byte[]> _spare = [];
    private const int SpareLimit = 4;
    private long _decoded;
    private long _decodeTicks;
    private long _converted;
    private long _convertTicks;

    public MediaSource(string path)
    {
        _demuxer = Demuxer.Open(path);
        var video = _demuxer.Video ?? throw new NotSupportedException("The file has no video stream.");
        int threads = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
        _video = video.Codec switch
        {
            CodecIds.H264 => new H264Decoder(video) { Threads = threads },
            CodecIds.Mjpeg => new MjpegDecoder(video),
            _ => throw new NotSupportedException($"No decoder for {video.Codec}."),
        };
        var audio = _demuxer.Audio;
        _audio = audio?.Codec == CodecIds.Aac ? new AacDecoder(audio) : null;
        VideoStream = video;
        AudioStream = _audio is null ? null : audio;
        _thread = new Thread(Run) { IsBackground = true, Name = "mcplay decode" };
    }

    public VideoStreamInfo VideoStream { get; }

    public AudioStreamInfo? AudioStream { get; }

    public BlockingCollection<RgbaFrame> Frames { get; } = new(boundedCapacity: 8);

    /// <summary>
    /// Gives a frame's pixels back for a later frame to be converted into, once they are no longer read (copied into a
    /// texture, or the frame dropped). Optional: a frame not returned is collected as usual. Return each frame once.
    /// </summary>
    public void Return(RgbaFrame frame)
    {
        if (_spare.Count < SpareLimit)
        {
            _spare.Add(frame.Pixels);
        }
    }

    public BlockingCollection<(int SampleRate, short[] Samples)> Audio { get; } = new(boundedCapacity: 32);

    /// <summary>
    /// Video frames the decoder has returned so far, and its time per frame (milliseconds, mean): the wall time of
    /// every <c>Decode</c> and <c>Flush</c> call over the whole run, divided by the frames they returned. A single call's
    /// time doesn't belong to its frames: with H.264 frame threading a call queues a packet and returns frames that
    /// other threads finished earlier, or waits for one. Over many calls the mean is the decoder's throughput as this
    /// thread sees it, the figure that says whether playback keeps up.
    /// </summary>
    public (long Count, double Milliseconds) DecodeStatistics => (_decoded, _decoded == 0 ? 0 : _decodeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _decoded);

    /// <summary>
    /// Video frames converted to RGBA so far, and the time each took (milliseconds, mean): the conversion and its
    /// buffer, not the decoding, which runs before (and with H.264 frame threading, across frames).
    /// </summary>
    public (long Count, double Milliseconds) ConvertStatistics => (_converted, _converted == 0 ? 0 : _convertTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _converted);

    public void Start() => _thread.Start();

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _demuxer.Dispose();
        _stop.Dispose();
    }

    private void Run()
    {
        var token = _stop.Token;
        try
        {
            foreach (var packet in _demuxer.ReadPackets())
            {
                if (packet.StreamIndex == VideoStream.StreamIndex)
                {
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    var frames = _video.Decode(packet);
                    Count(start, frames);
                    Show(frames, token);
                }
                else if (_audio is not null && packet.StreamIndex == AudioStream!.StreamIndex && !packet.Discard)
                {
                    var frame = _audio.Decode(packet);
                    Audio.Add((frame.SampleRate, Interleave(frame)), token);
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }
            }

            long flushStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var flushed = _video.Flush();
            Count(flushStart, flushed);
            Show(flushed, token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Frames.CompleteAdding();
            Audio.CompleteAdding();
        }
    }

    // Adds a Decode or Flush call, timed from start, and the frames it returned to DecodeStatistics.
    private void Count(long start, IReadOnlyList<VideoFrame> frames)
    {
        _decodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        _decoded += frames.Count;
    }

    private void Show(IReadOnlyList<VideoFrame> frames, CancellationToken token)
    {
        var timeBase = VideoStream.TimeBase;
        foreach (var frame in frames)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var (width, height) = (frame.Width, frame.Height);
            var pixels = Rent(width * height * 4);
            YuvToRgba.For(frame).Convert(frame, pixels);
            double time = frame.Pts * (double)timeBase.Numerator / timeBase.Denominator;
            var sar = frame.SampleAspectRatio;
            double aspect = sar.Numerator > 0 ? sar.Numerator / (double)sar.Denominator : 1;
            frame.Dispose();
            _convertTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            _converted++;
            Frames.Add(new RgbaFrame(time, width, height, pixels, aspect), token);
        }
    }

    // A returned array of the length if there is one (a spare of another size, from before the stream changed size, is
    // dropped), else a new one.
    private byte[] Rent(int length)
    {
        while (_spare.TryTake(out var pixels))
        {
            if (pixels.Length == length)
            {
                return pixels;
            }
        }

        return new byte[length];
    }

    private static short[] Interleave(AudioFrame frame)
    {
        int count = frame.SampleCount;
        var left = frame.Samples[0];
        var right = frame.Channels > 1 ? frame.Samples[1] : left;
        var samples = new short[count * 2];
        for (int i = 0; i < count; i++)
        {
            samples[2 * i] = ToShort(left[i]);
            samples[(2 * i) + 1] = ToShort(right[i]);
        }

        return samples;
    }

    private static short ToShort(float value) => (short)Math.Clamp(MathF.Round(value * 32767), -32768, 32767);
}
