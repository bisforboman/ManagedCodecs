using ManagedCodecs.Aac;
using ManagedCodecs.Containers;
using ManagedCodecs.Mp3;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// <see cref="AudioPlayer"/>: every audio-only sample (.wav, raw MP3 and AAC) plays its decoder's own output, as
/// interleaved stereo in file order; Loop plays it again; what ends the decoding is kept in Error.
/// </summary>
public class AudioPlayerTests
{
    public static TheoryData<string> Samples()
    {
        var names = new TheoryData<string>();
        if (TestSamples.Directory is null)
        {
            return names;
        }

        foreach (var file in Directory.EnumerateFiles(TestSamples.Directory).Order())
        {
            if (Path.GetExtension(file) is ".wav" or ".mp3" or ".aac")
            {
                names.Add(Path.GetFileName(file));
            }
        }

        return names;
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void PlaysTheDecodersOutput(string name)
    {
        var expected = Expected(name);
        using var player = new AudioPlayer(TestSamples.PathOf(name));
        Assert.Null(Record.Exception(() => player.Start()));
        var played = Drain(player, expected.Count + 1);
        Assert.Null(player.Error);
        Assert.True(player.IsFinished);
        Assert.Equal(expected, played);
    }

    /// <summary>Loop plays the file again after its end, from its start: each pass the same samples, gapless MP3 included.</summary>
    [Theory]
    [InlineData("wav-pcm-s16.wav")]
    [InlineData("mp3-raw-cbr.mp3")]
    [InlineData("aac-pulse.aac")]
    public void Loop_PlaysTheFileAgain(string name)
    {
        var once = Expected(name);
        using var player = new AudioPlayer(TestSamples.PathOf(name), new PlaybackOptions { Loop = true });
        player.Start();
        var played = Drain(player, (once.Count * 3) + 1, stopAfter: once.Count * 3);
        Assert.Null(player.Error);
        Assert.False(player.IsFinished);
        Assert.Equal([.. once, .. once, .. once], played);
    }

    /// <summary>A file without audio, or with audio no decoder here takes, is refused when opened.</summary>
    [Fact]
    public void RefusesAFileWithoutAudioItPlays()
    {
        Assert.Throws<NotSupportedException>(() => new AudioPlayer(TestSamples.PathOf("testsrc-420.avi")));
    }

    /// <summary>A file cut short in a frame gives the audio before it, then ends with the demuxer's error in Error.</summary>
    [Fact]
    public void KeepsWhatEndsTheDecodingInError()
    {
        // Cut 10 bytes into the last ADTS frame (the file ends with it: no tag after).
        var bytes = File.ReadAllBytes(TestSamples.PathOf("aac-pulse.aac"));
        int last;
        using (var demuxer = new AdtsDemuxer(new MemoryStream(bytes)))
        {
            last = demuxer.ReadPacketInfo().Last().Size;
        }

        using var player = new AudioPlayer(new MemoryStream(bytes[..(bytes.Length - last + 10)]));
        player.Start();
        var played = Drain(player, int.MaxValue);
        Assert.True(player.IsFinished);
        Assert.IsType<InvalidDataException>(player.Error);
        Assert.NotEmpty(played);
    }

    /// <summary>Disposed without being started, the player is finished: no thread will end its queue.</summary>
    [Fact]
    public void DisposedUnstarted_IsFinished()
    {
        var player = new AudioPlayer(TestSamples.PathOf("wav-pcm-s16.wav"));
        player.Dispose();
        Assert.True(player.IsFinished);
        Assert.Throws<ObjectDisposedException>(player.Start);
    }

    /// <summary>
    /// An MXF file's mono track a channel (mxf-h264-mono4, four sines): the track <see cref="PlaybackOptions.AudioTrack"/>
    /// names is the left channel and the next the right, sample for sample what FFmpeg's <c>join</c> filter makes of the
    /// two (name.pair01.f32, name.pair23.f32); the last track, with none after it, plays on both; a track the file
    /// doesn't have is refused.
    /// </summary>
    [Theory]
    [InlineData(0, 1, "pair01", 0, "pair01", 1)]
    [InlineData(1, 2, "pair01", 1, "pair23", 0)]
    [InlineData(2, 3, "pair23", 0, "pair23", 1)]
    [InlineData(3, 4, "pair23", 1, "pair23", 1)]
    public void PlaysAnMxfFilesMonoTracksAsLeftAndRight(int track, int streamIndex, string left, int leftChannel, string right, int rightChannel)
    {
        const string Name = "mxf-h264-mono4.mxf";
        var expected = Interleave(Channel(Name, left, leftChannel), Channel(Name, right, rightChannel));
        using var player = new AudioPlayer(TestSamples.PathOf(Name), new PlaybackOptions { AudioTrack = track });
        Assert.Equal(streamIndex, player.Audio.StreamIndex);
        player.Start();
        var played = Drain(player, expected.Count + 1);
        Assert.Null(player.Error);
        Assert.Equal(expected.Count, played.Count);
        Assert.Equal(expected, played);
    }

    /// <summary>Loop plays a pair of mono tracks again as it played them, nothing of one pass waiting into the next.</summary>
    [Fact]
    public void Loop_PlaysAPairOfMonoTracksAgain()
    {
        const string Name = "mxf-h264-mono4.mxf";
        var once = Interleave(Channel(Name, "pair01", 0), Channel(Name, "pair01", 1));
        using var player = new AudioPlayer(TestSamples.PathOf(Name), new PlaybackOptions { Loop = true });
        player.Start();
        var played = Drain(player, (once.Count * 2) + 1, stopAfter: once.Count * 2);
        Assert.Null(player.Error);
        Assert.Equal([.. once, .. once], played);
    }

    /// <summary>
    /// <see cref="PlaybackOptions.AudioTrack"/> on tracks that are not mono (mxf-prores-pcm16's two stereo ones): the
    /// track named plays alone, as its decoder gives it. A track the file doesn't have, or a negative one, is refused,
    /// in a file with one track too.
    /// </summary>
    [Fact]
    public void PlaysTheTrackTheOptionsName()
    {
        const string Name = "mxf-prores-pcm16.mxf";
        using (var first = new AudioPlayer(TestSamples.PathOf(Name)))
        using (var second = new AudioPlayer(TestSamples.PathOf(Name), new PlaybackOptions { AudioTrack = 1 }))
        {
            Assert.Equal((1, 2), (first.Audio.StreamIndex, second.Audio.StreamIndex));
            first.Start();
            second.Start();
            var (expectedFirst, expectedSecond) = (Expected(Name, 0), Expected(Name, 1));
            Assert.NotEqual(expectedFirst, expectedSecond); // 440 Hz and 660 Hz
            Assert.Equal(expectedFirst, Drain(first, expectedFirst.Count + 1));
            Assert.Equal(expectedSecond, Drain(second, expectedSecond.Count + 1));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioPlayer(TestSamples.PathOf(Name), new PlaybackOptions { AudioTrack = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioPlayer(TestSamples.PathOf(Name), new PlaybackOptions { AudioTrack = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioPlayer(TestSamples.PathOf("wav-pcm-s16.wav"), new PlaybackOptions { AudioTrack = 1 }));
    }

    /// <summary>
    /// The pairing itself, on hand-made 16-bit packets: a track's samples wait for the other's, whatever the packets'
    /// sizes and order; what one track has past the other's end plays beside silence; packets of other streams and
    /// discarded ones give nothing. And only an MXF file's mono tracks are a pair: elsewhere two mono tracks are two
    /// languages as likely as two channels.
    /// </summary>
    [Fact]
    public void PairsMonoTracksSampleBySample()
    {
        static AudioStreamInfo Mono(int index) => new(CodecIds.PcmS16Le, 48000, 1, 0, new Rational(1, 48000), default, index);
        static Packet Pcm(int stream, params short[] samples) =>
            new(stream, 0, samples.SelectMany(BitConverter.GetBytes).ToArray(), true, 0, 0, samples.Length);

        using var mxf = MxfDemuxer.Open(TestSamples.PathOf("mxf-h264-mono4.mxf"));
        var played = PlayedAudio.Choose(mxf, new PlaybackOptions())!;
        Assert.Equal((1, 2), (played.Track.StreamIndex, played.Right!.StreamIndex));
        played.Select(mxf);
        Assert.Equal([0, 1, 2], mxf.ReadPackets().Select(p => p.StreamIndex).Distinct().Order());

        // Not an MXF demuxer: no pair.
        Assert.Null(PlayedAudio.Choose(new Tracks(true, Mono(1), Mono(2)), new PlaybackOptions())!.Right);

        var decoding = new PlayedAudio(Mono(1), Mono(2)).Decode();
        Assert.Null(decoding.Decode(Pcm(1, 16384, 8192, 4096)));
        Assert.Null(decoding.Decode(Pcm(0, 1, 2)));
        Assert.Null(decoding.Decode(Pcm(2, -16384) with { Discard = true }));
        Assert.Equal([0.5f, -0.5f, 0.25f, -0.25f], decoding.Decode(Pcm(2, -16384, -8192))!.Samples);
        Assert.Null(decoding.Decode(Pcm(1, 2048)));
        Assert.Equal([0.125f, -0.125f, 0.0625f, -0.0625f], decoding.Decode(Pcm(2, -4096, -2048, -1024))!.Samples);
        var rest = decoding.Flush()!;
        Assert.Equal(48000, rest.SampleRate);
        Assert.Equal([0f, -0.03125f], rest.Samples);
        Assert.Null(decoding.Flush());

        // One track alone: each packet a chunk, nothing to flush.
        var alone = PlayedAudio.Choose(new Tracks(false, Mono(1)), new PlaybackOptions())!.Decode();
        Assert.Equal([0.5f, 0.5f], alone.Decode(Pcm(1, 16384))!.Samples);
        Assert.Null(alone.Flush());
    }

    /// <summary>
    /// Unset, <see cref="PlaybackOptions.AudioTrack"/> plays the track the demuxer gives unasked, wherever it is in the
    /// list; a number counts from 0 in the file's order, whichever that is.
    /// </summary>
    [Fact]
    public void PlaysTheDemuxersAudioUnasked()
    {
        static AudioStreamInfo Stereo(int index) => new(CodecIds.PcmS16Le, 48000, 2, 0, new Rational(1, 48000), default, index);
        var demuxer = new Tracks(true, Stereo(1), Stereo(2)) { Unasked = 1 };
        Assert.Equal(2, PlayedAudio.Choose(demuxer, new PlaybackOptions())!.Track.StreamIndex);
        Assert.Equal(1, PlayedAudio.Choose(demuxer, new PlaybackOptions { AudioTrack = 0 })!.Track.StreamIndex);
        Assert.Equal(1, PlayedAudio.Choose(new Tracks(true, Stereo(1), Stereo(2)), new PlaybackOptions())!.Track.StreamIndex);
    }

    private sealed class Tracks(bool several, params AudioStreamInfo[] tracks) : IDemuxer
    {
        /// <summary>Which track <see cref="Audio"/> is.</summary>
        public int Unasked { get; init; }

        public VideoStreamInfo? Video => null;

        public AudioStreamInfo? Audio => tracks[Unasked];

        public IReadOnlyList<AudioStreamInfo> AudioTracks => several ? tracks : [tracks[0]];

        public IEnumerable<Packet> ReadPackets() => [];

        public void Dispose()
        {
        }
    }

    // One channel of a stereo reference (name.pairNM.f32: FFmpeg's join of two mono tracks).
    private static float[] Channel(string name, string reference, int channel)
    {
        var bytes = File.ReadAllBytes(TestSamples.ReferenceOf(name, $".{reference}.f32"));
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
        var samples = new float[floats.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = floats[(2 * i) + channel];
        }

        return samples;
    }

    private static List<float> Interleave(float[] left, float[] right)
    {
        var samples = new List<float>(left.Length * 2);
        for (int i = 0; i < left.Length; i++)
        {
            samples.Add(left[i]);
            samples.Add(right[i]);
        }

        return samples;
    }

    // The samples the player should give: each audio packet not discarded, decoded and interleaved as stereo; of
    // several tracks, the one at `track` in the demuxer's list.
    private static List<float> Expected(string name, int track = 0)
    {
        using var demuxer = Demuxer.Open(TestSamples.PathOf(name));
        if (track > 0)
        {
            demuxer.SelectAudio(demuxer.AudioTracks[track].StreamIndex);
        }

        var audio = demuxer.Audio!;
        IAudioDecoder decoder = audio.Codec switch
        {
            CodecIds.Aac => new AacDecoder(audio),
            CodecIds.Mp3 => new Mp3Decoder(audio),
            CodecIds.AdpcmImaWav => new ImaAdpcmDecoder(audio),
            CodecIds.AdpcmMs => new MsAdpcmDecoder(audio),
            _ => new PcmDecoder(audio),
        };
        var samples = new List<float>();
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == audio.StreamIndex && !p.Discard))
        {
            var frame = decoder.Decode(packet);
            for (int i = 0; i < frame.SampleCount; i++)
            {
                samples.Add(frame.Samples[0][i]);
                samples.Add(frame.Samples[frame.Channels > 1 ? 1 : 0][i]);
            }
        }

        return samples;
    }

    // The player's samples until it finishes or gives stopAfter of them, within 30 s.
    private static List<float> Drain(AudioPlayer player, int limit, int stopAfter = int.MaxValue)
    {
        var samples = new List<float>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (samples.Count < stopAfter && DateTime.UtcNow < deadline)
        {
            if (player.TakeAudio() is { } chunk)
            {
                samples.AddRange(chunk.Samples);
                Assert.True(samples.Count <= limit, $"More than {limit} samples.");
            }
            else if (player.IsFinished)
            {
                break;
            }
            else
            {
                Thread.Sleep(1);
            }
        }

        return samples.Count > stopAfter ? samples[..stopAfter] : samples;
    }
}
