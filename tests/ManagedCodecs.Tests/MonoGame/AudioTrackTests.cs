using ManagedCodecs.MonoGame;

namespace ManagedCodecs.Tests.MonoGame;

/// <summary>
/// AudioTrack's bookkeeping, which needs no audio device: how long a chunk plays and the time of the buffers the sound
/// has finished (its Position). The sound itself needs an audio device, which CI doesn't have: mcplay
/// (tools/ManagedCodecs.MonoGame.Sample) plays sound-only files through it.
/// </summary>
public class AudioTrackTests
{
    [Theory]
    [InlineData(2048, 44100, 10_000_000L * 1024 / 44100)] // a frame of AAC, stereo
    [InlineData(2304, 48000, 240_000L)] // a frame of MP3: 1152 samples, 24 ms
    [InlineData(0, 8000, 0L)]
    public void Duration_CountsStereoPairs(int samples, int rate, long ticks) =>
        Assert.Equal(ticks, AudioTrack.Duration(samples, rate).Ticks);

    [Fact]
    public void Finished_TakesAllButThePendingBuffers()
    {
        var queued = new Queue<TimeSpan>([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(30)]);
        Assert.Equal(TimeSpan.Zero, AudioTrack.Finished(queued, 3));
        Assert.Equal(TimeSpan.FromMilliseconds(10), AudioTrack.Finished(queued, 2));
        Assert.Equal(TimeSpan.FromMilliseconds(50), AudioTrack.Finished(queued, 0));
        Assert.Empty(queued);

        // More pending than queued (a sound counting a buffer this didn't queue) finishes nothing.
        queued.Enqueue(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, AudioTrack.Finished(queued, 5));
        Assert.Single(queued);
    }

    [Fact]
    public void OpeningAFileWithoutAudioThrows()
    {
        Assert.Throws<NotSupportedException>(() => new AudioTrack(TestSamples.PathOf("testsrc-420.avi")));
    }

    [Fact]
    public void StartsWithNothingPlayed()
    {
        using var track = new AudioTrack(TestSamples.PathOf("wav-pcm-s16.wav"));
        Assert.Equal(TimeSpan.Zero, track.Position);
        Assert.False(track.IsPlaying);
        Assert.False(track.IsFinished);
        Assert.Equal(1f, track.Volume);
        Assert.Throws<ArgumentOutOfRangeException>(() => track.Volume = 1.5f);
    }
}
