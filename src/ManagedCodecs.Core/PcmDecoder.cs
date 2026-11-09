namespace ManagedCodecs;

/// <summary>
/// Decodes PCM to 32-bit float samples: unsigned 8-bit (<see cref="CodecIds.PcmU8"/>, (x - 128) / 128) and signed
/// 16-bit little-endian (<see cref="CodecIds.PcmS16Le"/>, x / 32768), channels interleaved, as FFmpeg's pcm_u8 and
/// pcm_s16le decoders give them: exactly the same values.
/// </summary>
public sealed class PcmDecoder : IAudioDecoder
{
    private readonly int _bytesPerSample;
    private readonly Rational _timeBase;

    // Samples still to drop at the start of the next packets, from a Packet.SkipSamples larger than its packet's output.
    private int _pendingSkip;

    /// <summary>A decoder for a demuxer's audio stream: its codec, rate, channels and time base.</summary>
    /// <exception cref="NotSupportedException">The codec is not <see cref="CodecIds.PcmU8"/> or <see cref="CodecIds.PcmS16Le"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The rate or the channel count is not positive.</exception>
    public PcmDecoder(AudioStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _bytesPerSample = stream.Codec switch
        {
            CodecIds.PcmU8 => 1,
            CodecIds.PcmS16Le => 2,
            _ => throw new NotSupportedException($"No PCM decoder for {stream.Codec}."),
        };
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stream.SampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stream.Channels);
        SampleRate = stream.SampleRate;
        Channels = stream.Channels;
        _timeBase = stream.TimeBase;
    }

    /// <summary>The sample rate, as the stream gives it.</summary>
    public int SampleRate { get; }

    /// <summary>The channel count, as the stream gives it.</summary>
    public int Channels { get; }

    /// <summary>
    /// Decodes one packet's interleaved samples into planar floats. Bytes after the last whole sample frame (a sample
    /// for every channel) are ignored, as FFmpeg ignores them.
    /// </summary>
    /// <exception cref="InvalidDataException">The packet is not empty but shorter than one sample frame.</exception>
    public AudioFrame Decode(ReadOnlySpan<byte> packet)
    {
        int frameBytes = _bytesPerSample * Channels;
        if (packet.Length > 0 && packet.Length < frameBytes)
        {
            throw new InvalidDataException($"A PCM packet of {packet.Length} bytes is shorter than one {frameBytes}-byte sample frame.");
        }

        int count = packet.Length / frameBytes;
        var frame = new AudioFrame(SampleRate, Channels, count);
        for (int c = 0; c < Channels; c++)
        {
            var samples = frame.Samples[c];
            for (int i = 0, at = c * _bytesPerSample; i < count; i++, at += frameBytes)
            {
                samples[i] = _bytesPerSample == 1
                    ? (packet[at] - 128) / 128f
                    : (short)(packet[at] | (packet[at + 1] << 8)) / 32768f;
            }
        }

        return frame;
    }

    /// <inheritdoc/>
    public AudioFrame Decode(Packet packet)
    {
        var frame = Decode(packet.Data.Span);
        int dropped;
        if (packet.Discard)
        {
            dropped = frame.SampleCount;
        }
        else
        {
            int skip = _pendingSkip + Math.Max(0, packet.SkipSamples);
            dropped = Math.Min(skip, frame.SampleCount);
            _pendingSkip = skip - dropped;
        }

        var output = frame.Drop(dropped, packet.Discard ? 0 : packet.SkipEndSamples);
        output.Pts = packet.Pts + SamplesToTime(dropped);
        return output;
    }

    // Output samples as a duration in the packets' time base, rounded to nearest (halves away from zero).
    private long SamplesToTime(int samples)
    {
        if (samples == 0)
        {
            return 0;
        }

        long numerator = (long)samples * _timeBase.Denominator;
        long denominator = (long)SampleRate * _timeBase.Numerator;
        return (numerator + (denominator / 2)) / denominator;
    }
}
