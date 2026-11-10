using System.Buffers.Binary;

namespace ManagedCodecs;

/// <summary>
/// Decodes Microsoft ADPCM (<see cref="CodecIds.AdpcmMs"/>, the WAVE format tag 0x02 at 4 bits, in .wav and AVI files)
/// to 32-bit float samples, x / 32768 of the 16-bit samples the algorithm gives: exactly FFmpeg's adpcm_ms output.
/// </summary>
/// <remarks>
/// <para>
/// A block starts with a header of 7 bytes a channel, the channels' fields in turn: a predictor index, then the step
/// (iDelta), the last sample and the one before it, 16-bit little-endian each. The two samples are the block's first
/// two (the older first); then each byte holds two 4-bit codes, the high nibble first: two samples of a mono stream, or
/// one sample of each channel of a stereo one. A block of B bytes holds 2 + (B - 7 * channels) * 2 / channels samples a
/// channel; a block cut short gives what its bytes hold.
/// </para>
/// <para>
/// A sample is the last two weighted by the predictor's coefficient pair, over 256 (rounded toward zero), plus the code
/// (-8 to 7) times the step, clamped to 16 bits; the step then scales by the code's adaptation factor over 256, kept at
/// 16 or more. Two rules are FFmpeg's rather than the format's (docs/decisions/2026-11-10-ms-adpcm.md): the
/// coefficients are always the standard seven pairs (the file's own table, in its CodecPrivate, is not read, as
/// FFmpeg's isn't), so a predictor index past 6 is malformed; and the step is capped at int.MaxValue / 768, so that it
/// cannot overflow.
/// </para>
/// <para>
/// The stream's CodecPrivate (the WAVEFORMATEX's extra bytes) gives wSamplesPerBlock first, from which a packet of
/// several blocks is cut; without it a packet is one block.
/// </para>
/// </remarks>
public sealed class MsAdpcmDecoder : IAudioDecoder
{
    // The step's largest value, as FFmpeg caps it: 768 times it (the largest factor) still fits an int.
    private const int MaxDelta = int.MaxValue / 768;

    // The standard coefficient pairs, over 256.
    private static ReadOnlySpan<short> Coefficient1 => [256, 512, 0, 192, 240, 460, 392];

    private static ReadOnlySpan<short> Coefficient2 => [0, -256, 0, 64, 0, -208, -232];

    // The step's adaptation factor for each 4-bit code, over 256.
    private static ReadOnlySpan<short> Adaptation => [230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230];

    private readonly int _blockBytes;
    private readonly Rational _timeBase;

    // Samples still to drop at the start of the next packets, from a Packet.SkipSamples larger than its packet's output.
    private int _pendingSkip;

    /// <summary>A decoder for a demuxer's audio stream: its codec, rate, channels, time base and wSamplesPerBlock.</summary>
    /// <exception cref="NotSupportedException">The codec is not <see cref="CodecIds.AdpcmMs"/>, or the stream has more than 2 channels.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The rate or the channel count is not positive.</exception>
    public MsAdpcmDecoder(AudioStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Codec != CodecIds.AdpcmMs)
        {
            throw new NotSupportedException($"No Microsoft ADPCM decoder for {stream.Codec}.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stream.SampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stream.Channels);
        if (stream.Channels > 2)
        {
            throw new NotSupportedException($"Microsoft ADPCM of {stream.Channels} channels: only mono and stereo are defined.");
        }

        SampleRate = stream.SampleRate;
        Channels = stream.Channels;
        _timeBase = stream.TimeBase;

        // wSamplesPerBlock = 2 + 2 * (B - 7 * channels) / channels: the block is the headers and the codes.
        if (stream.CodecPrivate.Length >= 2)
        {
            int samplesPerBlock = BinaryPrimitives.ReadUInt16LittleEndian(stream.CodecPrivate.Span);
            if (samplesPerBlock > 2 && ((samplesPerBlock - 2) * Channels) % 2 == 0)
            {
                _blockBytes = (7 * Channels) + ((samplesPerBlock - 2) * Channels / 2);
            }
        }
    }

    /// <summary>The sample rate, as the stream gives it.</summary>
    public int SampleRate { get; }

    /// <summary>The channel count, as the stream gives it.</summary>
    public int Channels { get; }

    /// <summary>Decodes one packet's blocks into planar floats; the last block may be cut short.</summary>
    /// <exception cref="InvalidDataException">
    /// A block is shorter than its headers (7 bytes a channel), or a header's predictor index is past the last of the
    /// standard seven.
    /// </exception>
    public AudioFrame Decode(ReadOnlySpan<byte> packet)
    {
        int headers = 7 * Channels;
        int block = _blockBytes > 0 ? _blockBytes : Math.Max(packet.Length, 1);
        int count = 0;
        for (int at = 0; at < packet.Length; at += block)
        {
            count += SamplesIn(Math.Min(block, packet.Length - at), headers);
        }

        var frame = new AudioFrame(SampleRate, Channels, count);
        int position = 0;
        for (int at = 0; at < packet.Length; at += block)
        {
            var bytes = packet.Slice(at, Math.Min(block, packet.Length - at));
            int samples = SamplesIn(bytes.Length, headers);
            DecodeBlock(bytes, samples, frame, position);
            position += samples;
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

    // A block's samples a channel: the header's two, and two codes a byte for mono, one for stereo.
    private int SamplesIn(int bytes, int headers)
    {
        if (bytes < headers)
        {
            throw new InvalidDataException($"A Microsoft ADPCM block of {bytes} bytes is shorter than its {headers} bytes of headers.");
        }

        return 2 + ((bytes - headers) * 2 / Channels);
    }

    private void DecodeBlock(ReadOnlySpan<byte> block, int samples, AudioFrame frame, int position)
    {
        Span<int> coefficient1 = stackalloc int[2];
        Span<int> coefficient2 = stackalloc int[2];
        Span<int> delta = stackalloc int[2];
        Span<int> sample1 = stackalloc int[2];
        Span<int> sample2 = stackalloc int[2];
        int channels = Channels;
        for (int c = 0; c < channels; c++)
        {
            int predictor = block[c];
            if (predictor >= Coefficient1.Length)
            {
                throw new InvalidDataException($"A Microsoft ADPCM block's predictor index {predictor} is past the standard seven.");
            }

            coefficient1[c] = Coefficient1[predictor];
            coefficient2[c] = Coefficient2[predictor];
            delta[c] = BinaryPrimitives.ReadInt16LittleEndian(block[(channels + (2 * c))..]);
            sample1[c] = BinaryPrimitives.ReadInt16LittleEndian(block[((3 * channels) + (2 * c))..]);
            sample2[c] = BinaryPrimitives.ReadInt16LittleEndian(block[((5 * channels) + (2 * c))..]);
            var output = frame.Samples[c];
            output[position] = sample2[c] / 32768f;
            output[position + 1] = sample1[c] / 32768f;
        }

        // The codes: a byte's high nibble first, both a mono stream's, or the left then the right channel's.
        int at = 7 * channels;
        for (int i = 2; i < samples; at++)
        {
            int value = block[at];
            for (int half = 0; half < 2; half++)
            {
                int c = channels == 2 ? half : 0;
                int nibble = half == 0 ? value >> 4 : value & 0xF;
                int predicted = ((sample1[c] * coefficient1[c]) + (sample2[c] * coefficient2[c])) / 256;
                predicted += (nibble >= 8 ? nibble - 16 : nibble) * delta[c];
                sample2[c] = sample1[c];
                sample1[c] = Math.Clamp(predicted, short.MinValue, short.MaxValue);
                delta[c] = Math.Clamp((Adaptation[nibble] * delta[c]) >> 8, 16, MaxDelta);
                frame.Samples[c][position + i] = sample1[c] / 32768f;
                if (channels == 1)
                {
                    i++;
                    if (i == samples)
                    {
                        break;
                    }
                }
            }

            if (channels == 2)
            {
                i++;
            }
        }
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
