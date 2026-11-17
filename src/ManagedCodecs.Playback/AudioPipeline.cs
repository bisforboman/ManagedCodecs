using ManagedCodecs.Aac;
using ManagedCodecs.Containers;
using ManagedCodecs.Mp3;

namespace ManagedCodecs.Playback;

/// <summary>
/// The audio side of <see cref="MediaPlayer"/> and <see cref="AudioPlayer"/>: which codecs play, their decoders, and
/// their samples as float stereo.
/// </summary>
internal static class AudioPipeline
{
    /// <summary>Whether a stream of <paramref name="codec"/> plays: AAC, MP3, PCM, IMA and Microsoft ADPCM, G.726.</summary>
    public static bool CanPlay(string? codec) =>
        codec is CodecIds.Aac or CodecIds.Mp3 or CodecIds.AdpcmImaWav or CodecIds.AdpcmMs or CodecIds.AdpcmG726 or CodecIds.AdpcmG726Le || PcmDecoder.CanDecode(codec);

    /// <summary>A decoder for <paramref name="audio"/>, whose codec <see cref="CanPlay"/>.</summary>
    public static IAudioDecoder CreateDecoder(AudioStreamInfo audio) => audio switch
    {
        { Codec: CodecIds.Mp3 } => new Mp3Decoder(audio),
        { Codec: var codec } when PcmDecoder.CanDecode(codec) => new PcmDecoder(audio),
        { Codec: CodecIds.AdpcmImaWav } => new ImaAdpcmDecoder(audio),
        { Codec: CodecIds.AdpcmMs } => new MsAdpcmDecoder(audio),
        { Codec: CodecIds.AdpcmG726 or CodecIds.AdpcmG726Le } => new G726Decoder(audio),
        _ => new AacDecoder(audio),
    };

    /// <summary>
    /// A frame's samples as interleaved stereo: mono on both, stereo as it is, and more channels downmixed as FFmpeg's
    /// <c>-ac 2</c> (<see cref="Downmix"/>; more than eight with no layout named, their first two).
    /// </summary>
    public static AudioChunk ToStereo(AudioFrame frame, DateTime? captureTime = null)
    {
        if (Downmix.Weights(frame.ChannelMask, frame.Channels) is { } weights)
        {
            return new AudioChunk(frame.SampleRate, Downmix.Apply(frame, weights)) { CaptureTime = captureTime };
        }

        int count = frame.SampleCount;
        var left = frame.Samples[0];
        var right = frame.Channels > 1 ? frame.Samples[1] : left;
        var samples = new float[count * 2];
        for (int i = 0; i < count; i++)
        {
            samples[2 * i] = left[i];
            samples[(2 * i) + 1] = right[i];
        }

        return new AudioChunk(frame.SampleRate, samples) { CaptureTime = captureTime };
    }
}

/// <summary>
/// The audio a <see cref="MediaPlayer"/> or <see cref="AudioPlayer"/> plays of a file: the track
/// <see cref="PlaybackOptions.AudioTrack"/> names and, where that one is mono and an MXF file's next sound track is mono
/// of the same codec and rate (broadcast MXF's mono track a channel), that next one as the right channel
/// (docs/decisions/2026-11-15-sound-tracks.md).
/// </summary>
internal sealed class PlayedAudio
{
    internal PlayedAudio(AudioStreamInfo track, AudioStreamInfo? right)
    {
        Track = track;
        Right = right;
    }

    /// <summary>The track chosen: alone, or the left channel of a pair.</summary>
    public AudioStreamInfo Track { get; }

    /// <summary>The mono track played as the right channel beside a mono <see cref="Track"/>, or null.</summary>
    public AudioStreamInfo? Right { get; }

    /// <summary>
    /// The track the options name among the demuxer's, or null where the file has no audio at all. Does not select it:
    /// <see cref="Select"/> does, once the caller knows the track plays.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="PlaybackOptions.AudioTrack"/> is negative, or the file has audio tracks but not that many.</exception>
    public static PlayedAudio? Choose(IDemuxer demuxer, PlaybackOptions options)
    {
        if (options.AudioTrack < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.AudioTrack, "PlaybackOptions.AudioTrack is negative.");
        }

        var tracks = demuxer.AudioTracks;
        if (tracks.Count == 0)
        {
            return null;
        }

        // Unasked, the track the demuxer gives by default.
        int index = options.AudioTrack ?? Math.Max(0, IndexOf(tracks, demuxer.Audio));

        if (index >= tracks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(options), index, $"PlaybackOptions.AudioTrack is {index}, and the file has {tracks.Count} audio track{(tracks.Count == 1 ? "" : "s")}.");
        }

        var track = tracks[index];
        var right = options.PairMonoTracks && demuxer is MxfDemuxer && track.Channels == 1 && index + 1 < tracks.Count
            && tracks[index + 1] is { Channels: 1 } next && next.Codec == track.Codec && next.SampleRate == track.SampleRate
            ? next
            : null;
        return new PlayedAudio(track, right);
    }

    private static int IndexOf(IReadOnlyList<AudioStreamInfo> tracks, AudioStreamInfo? audio)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].StreamIndex == audio?.StreamIndex)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Has the demuxer's next read give the track's packets (and its right channel's).</summary>
    public void Select(IDemuxer demuxer)
    {
        if (Right is null)
        {
            demuxer.SelectAudio(Track.StreamIndex);
        }
        else
        {
            demuxer.SelectAudio(Track.StreamIndex, Right.StreamIndex);
        }
    }

    /// <summary>Decoders that start afresh, for one pass over the file.</summary>
    public Decoding Decode() => new(this);

    /// <summary>One pass's decoders and, for a pair of mono tracks, the samples of one still waiting for the other's.</summary>
    internal sealed class Decoding(PlayedAudio audio)
    {
        private readonly IAudioDecoder _decoder = AudioPipeline.CreateDecoder(audio.Track);
        private readonly IAudioDecoder? _rightDecoder = audio.Right is null ? null : AudioPipeline.CreateDecoder(audio.Right);
        private readonly List<float> _left = [];
        private readonly List<float> _right = [];
        private int _sampleRate = audio.Track.SampleRate;

        /// <summary>
        /// The audio the packet completes as stereo, or null: a packet of another stream, one to discard or that decodes
        /// to nothing, or a mono track's samples whose other channel has not come yet.
        /// </summary>
        public AudioChunk? Decode(Packet packet)
        {
            bool left = packet.StreamIndex == audio.Track.StreamIndex;
            if ((!left && packet.StreamIndex != audio.Right?.StreamIndex) || packet.Discard)
            {
                return null;
            }

            var frame = (left ? _decoder : _rightDecoder!).Decode(packet);
            if (frame.SampleCount == 0)
            {
                return null;
            }

            if (_rightDecoder is null)
            {
                return AudioPipeline.ToStereo(frame);
            }

            _sampleRate = frame.SampleRate;
            (left ? _left : _right).AddRange(new ArraySegment<float>(frame.Samples[0], 0, frame.SampleCount));
            return Pair(Math.Min(_left.Count, _right.Count));
        }

        /// <summary>At the file's end: what one mono track has past the other's end, beside silence; null for none.</summary>
        public AudioChunk? Flush()
        {
            int count = Math.Max(_left.Count, _right.Count);
            _left.AddRange(new float[count - _left.Count]);
            _right.AddRange(new float[count - _right.Count]);
            return Pair(count);
        }

        // The first `count` waiting samples of each track, as left and right.
        private AudioChunk? Pair(int count)
        {
            if (count == 0)
            {
                return null;
            }

            var samples = new float[count * 2];
            for (int i = 0; i < count; i++)
            {
                samples[2 * i] = _left[i];
                samples[(2 * i) + 1] = _right[i];
            }

            _left.RemoveRange(0, count);
            _right.RemoveRange(0, count);
            return new AudioChunk(_sampleRate, samples);
        }
    }
}
