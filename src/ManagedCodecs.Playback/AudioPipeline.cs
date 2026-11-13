using ManagedCodecs.Aac;
using ManagedCodecs.Mp3;

namespace ManagedCodecs.Playback;

/// <summary>
/// The audio side of <see cref="MediaPlayer"/> and <see cref="AudioPlayer"/>: which codecs play, their decoders, and
/// their samples as float stereo.
/// </summary>
internal static class AudioPipeline
{
    /// <summary>Whether a stream of <paramref name="codec"/> plays: AAC, MP3, PCM, IMA and Microsoft ADPCM.</summary>
    public static bool CanPlay(string? codec) =>
        codec is CodecIds.Aac or CodecIds.Mp3 or CodecIds.AdpcmImaWav or CodecIds.AdpcmMs || PcmDecoder.CanDecode(codec);

    /// <summary>A decoder for <paramref name="audio"/>, whose codec <see cref="CanPlay"/>.</summary>
    public static IAudioDecoder CreateDecoder(AudioStreamInfo audio) => audio switch
    {
        { Codec: CodecIds.Mp3 } => new Mp3Decoder(audio),
        { Codec: var codec } when PcmDecoder.CanDecode(codec) => new PcmDecoder(audio),
        { Codec: CodecIds.AdpcmImaWav } => new ImaAdpcmDecoder(audio),
        { Codec: CodecIds.AdpcmMs } => new MsAdpcmDecoder(audio),
        _ => new AacDecoder(audio),
    };

    /// <summary>A frame's samples as interleaved stereo: its first two channels, or mono on both.</summary>
    public static AudioChunk ToStereo(AudioFrame frame)
    {
        int count = frame.SampleCount;
        var left = frame.Samples[0];
        var right = frame.Channels > 1 ? frame.Samples[1] : left;
        var samples = new float[count * 2];
        for (int i = 0; i < count; i++)
        {
            samples[2 * i] = left[i];
            samples[(2 * i) + 1] = right[i];
        }

        return new AudioChunk(frame.SampleRate, samples);
    }
}
