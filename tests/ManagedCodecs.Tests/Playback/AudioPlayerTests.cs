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

    // The samples the player should give: each audio packet not discarded, decoded and interleaved as stereo.
    private static List<float> Expected(string name)
    {
        using var demuxer = Demuxer.Open(TestSamples.PathOf(name));
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
