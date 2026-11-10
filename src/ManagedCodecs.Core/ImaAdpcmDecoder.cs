using System.Buffers.Binary;

namespace ManagedCodecs;

/// <summary>
/// Decodes 4-bit IMA ADPCM as Microsoft's WAVE format stores it (<see cref="CodecIds.AdpcmImaWav"/>, AVI's format tag
/// 0x11) to 32-bit float samples, x / 32768 of the 16-bit samples the IMA algorithm gives: exactly FFmpeg's
/// adpcm_ima_wav output.
/// </summary>
/// <remarks>
/// A block starts with a 4-byte header a channel (the first sample, 16-bit little-endian, then the step index and a
/// reserved byte); then the channels take turns, 4 bytes (8 samples, low nibble first) each. A block of B bytes holds
/// 1 + 8 * floor((B - 4 * channels) / (4 * channels)) samples a channel. The stream's CodecPrivate (the WAVEFORMATEX's
/// extra bytes) gives wSamplesPerBlock, from which a packet of several blocks is cut; without it a packet is one block.
/// </remarks>
public sealed class ImaAdpcmDecoder : IAudioDecoder
{
    // IMA's step sizes, and the step index's change for each nibble's magnitude (the sign bit, 8, doesn't count).
    private static ReadOnlySpan<short> Steps =>
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107,
        118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
        1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894,
        6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
        32767,
    ];

    private static ReadOnlySpan<sbyte> IndexChanges => [-1, -1, -1, -1, 2, 4, 6, 8];

    private readonly int _blockBytes;
    private readonly Rational _timeBase;

    // Samples still to drop at the start of the next packets, from a Packet.SkipSamples larger than its packet's output.
    private int _pendingSkip;

    /// <summary>A decoder for a demuxer's audio stream: its codec, rate, channels, time base and wSamplesPerBlock.</summary>
    /// <exception cref="NotSupportedException">The codec is not <see cref="CodecIds.AdpcmImaWav"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The rate or the channel count is not positive.</exception>
    public ImaAdpcmDecoder(AudioStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Codec != CodecIds.AdpcmImaWav)
        {
            throw new NotSupportedException($"No IMA ADPCM decoder for {stream.Codec}.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stream.SampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stream.Channels);
        SampleRate = stream.SampleRate;
        Channels = stream.Channels;
        _timeBase = stream.TimeBase;

        // wSamplesPerBlock = 1 + 8 * groups, a group being 4 bytes a channel: the block is the headers and the groups.
        if (stream.CodecPrivate.Length >= 2)
        {
            int samplesPerBlock = BinaryPrimitives.ReadUInt16LittleEndian(stream.CodecPrivate.Span);
            if (samplesPerBlock > 1 && (samplesPerBlock - 1) % 8 == 0)
            {
                _blockBytes = 4 * Channels * (1 + ((samplesPerBlock - 1) / 8));
            }
        }
    }

    /// <summary>The sample rate, as the stream gives it.</summary>
    public int SampleRate { get; }

    /// <summary>The channel count, as the stream gives it.</summary>
    public int Channels { get; }

    /// <summary>
    /// Decodes one packet's blocks into planar floats. A block cut short (the packet's last) gives its whole 4-byte
    /// groups; bytes after the last whole group are ignored, as FFmpeg ignores them.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// A block is shorter than its headers (4 bytes a channel), or a header's step index is past the last step (88).
    /// </exception>
    public AudioFrame Decode(ReadOnlySpan<byte> packet)
    {
        int headers = 4 * Channels;
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
            for (int c = 0; c < Channels; c++)
            {
                DecodeChannel(bytes, c, samples, frame.Samples[c].AsSpan(position, samples));
            }

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

    // A block's samples a channel: the header's, and 8 for each whole group of 4 bytes a channel.
    private static int SamplesIn(int bytes, int headers)
    {
        if (bytes < headers)
        {
            throw new InvalidDataException($"An IMA ADPCM block of {bytes} bytes is shorter than its {headers} bytes of headers.");
        }

        return 1 + ((bytes - headers) / headers * 8);
    }

    // One channel of one block: its header's sample, then its nibbles, 4 bytes a group, the channels' groups in turn.
    private void DecodeChannel(ReadOnlySpan<byte> block, int channel, int samples, Span<float> output)
    {
        int predictor = BinaryPrimitives.ReadInt16LittleEndian(block[(4 * channel)..]);
        int index = block[(4 * channel) + 2];
        if (index >= Steps.Length)
        {
            throw new InvalidDataException($"An IMA ADPCM block's step index {index} is past the last step ({Steps.Length - 1}).");
        }

        output[0] = predictor / 32768f;
        int stride = 4 * Channels;
        for (int i = 1, at = stride + (4 * channel); i < samples; at += stride)
        {
            for (int b = 0; b < 4; b++, i += 2)
            {
                int value = block[at + b];
                output[i] = Expand(value & 0xF, ref predictor, ref index) / 32768f;
                output[i + 1] = Expand(value >> 4, ref predictor, ref index) / 32768f;
            }
        }
    }

    // IMA's step: the difference (magnitude + 1/2) * step / 4, rounded down once, added or subtracted by the sign bit,
    // the sum clamped to 16 bits; the step index moves by the magnitude, kept in 0..88. IMA's recommended practice adds
    // step, step / 2, step / 4 and step / 8 by shifts instead, each rounded down on its own, and lands up to 2 lower;
    // FFmpeg rounds once (docs/decisions/2026-11-10-ima-adpcm.md).
    private static int Expand(int nibble, ref int predictor, ref int index)
    {
        int step = Steps[index];
        int difference = (((2 * (nibble & 7)) + 1) * step) >> 3;
        predictor = Math.Clamp((nibble & 8) != 0 ? predictor - difference : predictor + difference, short.MinValue, short.MaxValue);
        index = Math.Clamp(index + IndexChanges[nibble & 7], 0, Steps.Length - 1);
        return predictor;
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
