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
/// two). Both queues are bounded, so the decoder waits when playback falls behind.
/// </summary>
internal sealed class MediaSource : IDisposable
{
    private readonly IDemuxer _demuxer;
    private readonly IVideoDecoder _video;
    private readonly IAudioDecoder? _audio;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private long _decoded;
    private long _decodeTicks;

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

    public BlockingCollection<(int SampleRate, short[] Samples)> Audio { get; } = new(boundedCapacity: 32);

    /// <summary>Video frames decoded and converted so far, and the time spent on each (milliseconds, mean).</summary>
    public (long Count, double Milliseconds) DecodeStatistics => (_decoded, _decoded == 0 ? 0 : _decodeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _decoded);

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
                    Show(_video.Decode(packet), token);
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

            Show(_video.Flush(), token);
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

    private void Show(IReadOnlyList<VideoFrame> frames, CancellationToken token)
    {
        var timeBase = VideoStream.TimeBase;
        foreach (var frame in frames)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var (width, height) = (frame.Width, frame.Height);
            var pixels = new byte[width * height * 4];
            YuvToRgba.For(frame).Convert(frame, pixels);
            double time = frame.Pts * (double)timeBase.Numerator / timeBase.Denominator;
            var sar = frame.SampleAspectRatio;
            double aspect = sar.Numerator > 0 ? sar.Numerator / (double)sar.Denominator : 1;
            frame.Dispose();
            _decodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            _decoded++;
            Frames.Add(new RgbaFrame(time, width, height, pixels, aspect), token);
        }
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
