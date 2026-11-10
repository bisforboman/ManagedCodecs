using System.Buffers.Binary;

namespace ManagedCodecs;

/// <summary>
/// Decodes PCM to 32-bit float samples, channels interleaved, as FFmpeg's pcm_* decoders give them (exactly the same
/// values): unsigned 8-bit as (x - 128) / 128 (<see cref="CodecIds.PcmU8"/>); signed 16-, 24- and 32-bit as x / 2^15,
/// x / 2^23 and x / 2^31, little- or big-endian (<see cref="CodecIds.PcmS16Le"/> to <see cref="CodecIds.PcmS32Be"/>);
/// 32-bit float as it is and 64-bit float rounded to the nearest float (<see cref="CodecIds.PcmF32Le"/> to
/// <see cref="CodecIds.PcmF64Be"/>).
/// </summary>
public sealed class PcmDecoder : IAudioDecoder
{
    private readonly Format _format;
    private readonly int _bytesPerSample;
    private readonly Rational _timeBase;

    // Samples still to drop at the start of the next packets, from a Packet.SkipSamples larger than its packet's output.
    private int _pendingSkip;

    /// <summary>A decoder for a demuxer's audio stream: its codec, rate, channels and time base.</summary>
    /// <exception cref="NotSupportedException">The codec is not one of the PCM formats <see cref="CanDecode"/> takes.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The rate or the channel count is not positive.</exception>
    public PcmDecoder(AudioStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!TryGetFormat(stream.Codec, out _format, out _bytesPerSample))
        {
            throw new NotSupportedException($"No PCM decoder for {stream.Codec}.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stream.SampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stream.Channels);
        SampleRate = stream.SampleRate;
        Channels = stream.Channels;
        _timeBase = stream.TimeBase;
    }

    private enum Format
    {
        U8,
        S16Le,
        S16Be,
        S24Le,
        S24Be,
        S32Le,
        S32Be,
        F32Le,
        F32Be,
        F64Le,
        F64Be,
    }

    /// <summary>The sample rate, as the stream gives it.</summary>
    public int SampleRate { get; }

    /// <summary>The channel count, as the stream gives it.</summary>
    public int Channels { get; }

    /// <summary>The bytes of one sample of <paramref name="codec"/> (one channel's), or 0 for a codec this decoder doesn't take.</summary>
    internal static int BytesPerSample(string? codec) => TryGetFormat(codec, out _, out int bytes) ? bytes : 0;

    /// <summary>Whether <paramref name="codec"/> is one of the PCM formats this decoder takes (the <c>CodecIds.Pcm*</c> constants).</summary>
    public static bool CanDecode(string? codec) => TryGetFormat(codec, out _, out _);

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
                samples[i] = Sample(packet.Slice(at, _bytesPerSample));
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

    private static bool TryGetFormat(string? codec, out Format format, out int bytesPerSample)
    {
        (format, bytesPerSample) = codec switch
        {
            CodecIds.PcmU8 => (Format.U8, 1),
            CodecIds.PcmS16Le => (Format.S16Le, 2),
            CodecIds.PcmS16Be => (Format.S16Be, 2),
            CodecIds.PcmS24Le => (Format.S24Le, 3),
            CodecIds.PcmS24Be => (Format.S24Be, 3),
            CodecIds.PcmS32Le => (Format.S32Le, 4),
            CodecIds.PcmS32Be => (Format.S32Be, 4),
            CodecIds.PcmF32Le => (Format.F32Le, 4),
            CodecIds.PcmF32Be => (Format.F32Be, 4),
            CodecIds.PcmF64Le => (Format.F64Le, 8),
            CodecIds.PcmF64Be => (Format.F64Be, 8),
            _ => (Format.U8, 0),
        };
        return bytesPerSample > 0;
    }

    // One sample as a float (floats read through their bits: .NET Standard, the Unity build's, has no ReadSingle). The integer divisions are by powers of two, so each is exact before the one rounding of
    // the cast (32-bit integers and 64-bit floats have more precision than a float), as FFmpeg's conversion.
    private float Sample(ReadOnlySpan<byte> b) => _format switch
    {
        Format.U8 => (b[0] - 128) / 128f,
        Format.S16Le => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
        Format.S16Be => BinaryPrimitives.ReadInt16BigEndian(b) / 32768f,
        Format.S24Le => (((b[2] << 24) | (b[1] << 16) | (b[0] << 8)) >> 8) / 8388608f,
        Format.S24Be => (((b[0] << 24) | (b[1] << 16) | (b[2] << 8)) >> 8) / 8388608f,
        Format.S32Le => (float)(BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648.0),
        Format.S32Be => (float)(BinaryPrimitives.ReadInt32BigEndian(b) / 2147483648.0),
        Format.F32Le => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(b)),
        Format.F32Be => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(b)),
        Format.F64Le => (float)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(b)),
        _ => (float)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(b)),
    };

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
