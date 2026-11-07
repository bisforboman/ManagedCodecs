using System.Diagnostics;
using ManagedCodecs.Aac;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// MediaPlayer against the decoders it runs: its frames are the decoder's, in order, converted as YuvToRgba converts
/// them and timed from the first; its audio is the AAC decoder's, interleaved; frames already late are dropped, a loop
/// plays the file again after its last frame, and the options pick the matrix and range. The decoders themselves are
/// checked against FFmpeg elsewhere.
/// </summary>
public class MediaPlayerTests
{
    // Long enough for a loaded CI runner; a frame of these samples decodes in well under a millisecond.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public static TheoryData<string> Samples() => ["h264-audio-edits.mp4", "testsrc-420.avi"];

    [Theory]
    [MemberData(nameof(Samples))]
    public void PlaysTheDecodersFramesAndAudio(string name)
    {
        var (frames, audio) = Expected(name, null);
        Assert.True(frames.Count >= 20, "Too few frames to tell.");

        using var player = new MediaPlayer(TestSamples.PathOf(name));
        player.Start();
        var samples = new List<float>();
        foreach (var expected in frames)
        {
            var frame = Wait(player, () => player.TakeFrame(expected.Time), samples);
            Assert.Equal(expected.Time, frame.Time);
            Assert.Equal((320, 240), (frame.Width, frame.Height));
            Assert.Equal(expected.Pixels, frame.Pixels);
            player.Return(frame);
        }

        Wait(player, () => player.IsFinished ? player : null, samples);
        while (player.TakeAudio() is { } chunk)
        {
            samples.AddRange(chunk.Samples);
        }

        Assert.Null(player.Error);
        Assert.Equal(audio, samples);
        var statistics = player.Statistics;
        Assert.Equal((frames.Count, frames.Count, 0L), (statistics.DecodedFrames, statistics.ConvertedFrames, statistics.DroppedFrames));
    }

    // A game ticking at the video's own rate (Godot's Movie Maker mode, 60 fps video at 60 Hz) sums its frame times in
    // doubles, which falls a rounding error short of some frames' times. Each tick still shows its frame: none is shown
    // late and then dropped as it was when a frame was due only at its exact time (1 in 5 of a 24 fps film's).
    [Theory]
    [MemberData(nameof(Samples))]
    public void ClockAtTheFrameRate_ShowsEveryFrame(string name)
    {
        var (frames, _) = Expected(name, null);
        double interval = (frames[1].Time - frames[0].Time).TotalSeconds;
        using var player = new MediaPlayer(TestSamples.PathOf(name));
        player.Start();
        double clock = 0;
        int short_ = 0;
        for (int k = 0; k < frames.Count; k++, clock += interval)
        {
            var position = TimeSpan.FromSeconds(clock);
            short_ += position < frames[k].Time ? 1 : 0;

            // Decoded ahead (or the file finished), as a player keeping up would be, then one tick.
            Wait(player, () => player.IsFinished || player.Statistics.ConvertedFrames > Math.Min(k + 1, frames.Count - 1) ? player : null, []);
            var frame = player.TakeFrame(position);
            Assert.True(frame is not null, $"Tick {k} at {position} showed nothing.");
            Assert.Equal(frames[k].Time, frame.Time);
            player.Return(frame);
        }

        Assert.True(short_ > 0, "The clock never fell short of a frame's time: the test shows nothing.");
        Assert.Equal(0, player.Statistics.DroppedFrames);
    }

    [Fact]
    public void FramesAlreadyLate_AreDropped()
    {
        var (frames, _) = Expected("h264-audio-edits.mp4", null);
        using var player = new MediaPlayer(TestSamples.PathOf("h264-audio-edits.mp4"));
        player.Start();
        RgbaFrame? last = null;
        int shown = 0;
        var watch = Stopwatch.StartNew();
        while (!player.IsFinished)
        {
            Assert.True(watch.Elapsed < Patience, "The player did not finish.");
            if (player.TakeFrame(TimeSpan.MaxValue) is { } frame)
            {
                last = frame;
                shown++;
            }

            while (player.TakeAudio() is not null)
            {
            }

            Thread.Yield();
        }

        Assert.NotNull(last);
        Assert.Equal(frames[^1].Time, last.Time);
        Assert.Equal(frames[^1].Pixels, last.Pixels);
        Assert.Equal(frames.Count, shown + player.Statistics.DroppedFrames);
        Assert.Null(player.TakeFrame(TimeSpan.MaxValue));
    }

    [Fact]
    public void Loop_PlaysTheFileAgainAfterItsLastFrame()
    {
        var (frames, _) = Expected("h264-audio-edits.mp4", null);
        var interval = frames[1].Time - frames[0].Time;
        var length = frames[^1].Time + interval;
        using var player = new MediaPlayer(TestSamples.PathOf("h264-audio-edits.mp4"), new PlaybackOptions { Loop = true });
        player.Start();
        var samples = new List<float>();
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (var expected in frames)
            {
                var time = expected.Time + (length * pass);
                var frame = Wait(player, () => player.TakeFrame(time), samples);
                Assert.Equal(time, frame.Time);
                Assert.Equal(expected.Pixels, frame.Pixels);
            }
        }

        Assert.False(player.IsFinished);
        Assert.Equal(0, player.Statistics.DroppedFrames);
    }

    [Fact]
    public void Options_ChooseTheMatrixAndRange()
    {
        var options = new PlaybackOptions { Matrix = ColorMatrix.Bt2020Ncl, Range = ColorRange.Full, DecoderThreads = 1 };
        var (frames, _) = Expected("h264-audio-edits.mp4", options);
        var (plain, _) = Expected("h264-audio-edits.mp4", null);
        Assert.NotEqual(plain[0].Pixels, frames[0].Pixels);

        using var player = new MediaPlayer(TestSamples.PathOf("h264-audio-edits.mp4"), options);
        player.Start();
        var frame = Wait(player, () => player.TakeFrame(TimeSpan.Zero), []);
        Assert.Equal(frames[0].Pixels, frame.Pixels);
    }

    [Fact]
    public void Stream_PlaysAsTheFileDoes()
    {
        var (frames, _) = Expected("testsrc-420.avi", null);
        using var player = new MediaPlayer(new MemoryStream(File.ReadAllBytes(TestSamples.PathOf("testsrc-420.avi")), writable: false));
        player.Start();

        // The newest frame decoded by then: frame 3 once it is, the frames before it dropped or shown on the way.
        int shown = 0;
        RgbaFrame frame;
        do
        {
            frame = Wait(player, () => player.TakeFrame(frames[3].Time), []);
            shown++;
        }
        while (frame.Time < frames[3].Time);

        Assert.Equal(frames[3].Time, frame.Time);
        Assert.Equal(frames[3].Pixels, frame.Pixels);
        Assert.Equal(4, shown + player.Statistics.DroppedFrames);
    }

    [Fact]
    public void AFileWithoutVideo_IsRefused()
    {
        Assert.Throws<NotSupportedException>(() => new MediaPlayer(TestSamples.PathOf("aac-stereo-48k.mp4")));
    }

    [Fact]
    public void Dispose_StopsTheDecodingThreadAndStartAfterThrows()
    {
        var player = new MediaPlayer(TestSamples.PathOf("h264-audio-edits.mp4"), new PlaybackOptions { Loop = true });
        player.Start();
        player.Start();
        Wait(player, () => player.TakeFrame(TimeSpan.Zero), []);

        // Not taken from here on: the decoding thread waits on the full queues until Dispose stops it.
        Thread.Sleep(50);
        player.Dispose();
        player.Dispose();
        Assert.Throws<ObjectDisposedException>(player.Start);

        var watch = Stopwatch.StartNew();
        while (player.TakeFrame(TimeSpan.MaxValue) is not null || !player.IsFinished)
        {
            Assert.True(watch.Elapsed < Patience, "The decoding thread did not stop.");
        }

        Assert.Null(player.Error);
    }

    [Fact]
    public void Dispose_BeforeStart_Finishes()
    {
        var player = new MediaPlayer(TestSamples.PathOf("testsrc-420.avi"));
        player.Dispose();
        Assert.True(player.IsFinished);
        Assert.Null(player.TakeFrame(TimeSpan.MaxValue));
        Assert.Throws<ObjectDisposedException>(player.Start);
    }

    [Fact]
    public void ADamagedFile_PlaysUpToTheDamageAndFinishes()
    {
        // The second half of the file overwritten: chunk headers and JPEG markers alike.
        byte[] file = File.ReadAllBytes(TestSamples.PathOf("testsrc-420.avi"));
        new Random(1).NextBytes(file.AsSpan(file.Length / 2, file.Length / 4));
        using var player = new MediaPlayer(new MemoryStream(file, writable: false));
        player.Start();
        int shown = 0;
        var watch = Stopwatch.StartNew();
        while (!player.IsFinished)
        {
            Assert.True(watch.Elapsed < Patience, "The player did not finish.");
            if (player.TakeFrame(TimeSpan.MaxValue) is not null)
            {
                shown++;
            }

            while (player.TakeAudio() is not null)
            {
            }
        }

        // The frames before the damage play; the decoder's error ends the file rather than reaching the game loop.
        Assert.IsType<InvalidDataException>(player.Error);
        Assert.InRange(shown + player.Statistics.DroppedFrames, 1, 49);
    }

    // Waits for the player to give something, taking (and keeping) its audio meanwhile so that the decoding goes on.
    private static T Wait<T>(MediaPlayer player, Func<T?> take, List<float> samples)
        where T : class
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            // Read before taking: once finished, every frame and every chunk is queued, so one more pass that finds
            // nothing means there is nothing left (the decoding thread can finish between a take and this check).
            bool finished = player.IsFinished;
            while (player.TakeAudio() is { } chunk)
            {
                samples.AddRange(chunk.Samples);
            }

            if (take() is { } value)
            {
                return value;
            }

            Assert.True(watch.Elapsed < Patience, "The player gave nothing in time.");
            Assert.False(finished, "The player finished early.");
            Thread.Yield();
        }
    }

    // The file decoded here, as the player should: each frame's RGBA by YuvToRgba and its time from the first frame's,
    // and the AAC decoder's output of the packets not discarded, interleaved as stereo.
    private static (List<(TimeSpan Time, byte[] Pixels)> Frames, List<float> Audio) Expected(string name, PlaybackOptions? options)
    {
        using var demuxer = Demuxer.Open(TestSamples.PathOf(name));
        var video = demuxer.Video!;
        IVideoDecoder decoder = video.Codec == CodecIds.H264 ? new H264Decoder(video) : new MjpegDecoder(video);
        var aac = demuxer.Audio is { Codec: CodecIds.Aac } audio ? new AacDecoder(audio) : null;
        var frames = new List<(TimeSpan, byte[])>();
        var samples = new List<float>();
        long? first = null;
        void Add(IReadOnlyList<VideoFrame> decoded)
        {
            foreach (var frame in decoded)
            {
                first ??= frame.Pts;
                var pixels = new byte[frame.Width * frame.Height * 4];
                YuvToRgba.For(frame, options?.Matrix, options?.Range).Convert(frame, pixels);
                frames.Add((TimeSpan.FromSeconds((frame.Pts - first.Value) * (double)video.TimeBase.Numerator / video.TimeBase.Denominator), pixels));
                frame.Dispose();
            }
        }

        foreach (var packet in demuxer.ReadPackets())
        {
            if (packet.StreamIndex == video.StreamIndex)
            {
                Add(decoder.Decode(packet));
            }
            else if (aac is not null && packet.StreamIndex == demuxer.Audio!.StreamIndex && !packet.Discard)
            {
                var frame = aac.Decode(packet);
                var right = frame.Channels > 1 ? frame.Samples[1] : frame.Samples[0];
                for (int i = 0; i < frame.SampleCount; i++)
                {
                    samples.Add(frame.Samples[0][i]);
                    samples.Add(right[i]);
                }
            }
        }

        Add(decoder.Flush());
        return (frames, samples);
    }
}
