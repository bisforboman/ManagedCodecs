using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests;

/// <summary>
/// <see cref="PcmDecoder"/>: every AVI, QuickTime and Matroska sample with a name.pcm.f32 reference (FFmpeg's pcm_u8 or pcm_s16le output) decoded
/// exactly, every sample and no more; and its own rules on built input.
/// </summary>
public class PcmDecoderTests
{
    public static TheoryData<string> Samples() => new(TestSamples.WithReference(".pcm.f32"));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegExactly(string sampleName)
    {
        var reference = File.ReadAllBytes(TestSamples.ReferenceOf(sampleName, ".pcm.f32"));
        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var audio = demuxer.Audio!;
        Assert.True(PcmDecoder.CanDecode(audio.Codec), audio.Codec);
        var decoder = new PcmDecoder(audio);

        int position = 0;
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == audio.StreamIndex))
        {
            var decoded = decoder.Decode(packet);
            for (int i = 0; i < decoded.SampleCount; i++)
            {
                for (int c = 0; c < decoded.Channels; c++)
                {
                    int offset = (((position + i) * decoded.Channels) + c) * 4;
                    Assert.True(offset < reference.Length, $"More samples than FFmpeg's {reference.Length / 4 / decoded.Channels}.");
                    Assert.Equal(BitConverter.ToSingle(reference, offset), decoded.Samples[c][i]);
                }
            }

            position += decoded.SampleCount;
        }

        Assert.Equal(reference.Length / 4 / audio.Channels, position);
    }

    /// <summary>The extremes of each format, channels de-interleaved: (x - 128) / 128 and x / 32768.</summary>
    [Fact]
    public void ConvertsEachFormat()
    {
        var u8 = new PcmDecoder(Stream(CodecIds.PcmU8, 2)).Decode([0, 255, 128, 129]);
        Assert.Equal([-1f, 0f], u8.Samples[0]);
        Assert.Equal([127 / 128f, 1 / 128f], u8.Samples[1]);

        var s16 = new PcmDecoder(Stream(CodecIds.PcmS16Le, 1)).Decode([0x00, 0x80, 0xFF, 0x7F, 0x01, 0x00]);
        Assert.Equal([-1f, 32767 / 32768f, 1 / 32768f], s16.Samples[0]);
    }

    /// <summary>
    /// Every format against the definition on random bytes: integers over 2^(bits - 1), floats as they are and doubles
    /// rounded to float, little- and big-endian; and each one's extremes.
    /// </summary>
    [Theory]
    [InlineData(CodecIds.PcmS16Le, 2, false, false)]
    [InlineData(CodecIds.PcmS16Be, 2, false, true)]
    [InlineData(CodecIds.PcmS24Le, 3, false, false)]
    [InlineData(CodecIds.PcmS24Be, 3, false, true)]
    [InlineData(CodecIds.PcmS32Le, 4, false, false)]
    [InlineData(CodecIds.PcmS32Be, 4, false, true)]
    [InlineData(CodecIds.PcmF32Le, 4, true, false)]
    [InlineData(CodecIds.PcmF32Be, 4, true, true)]
    [InlineData(CodecIds.PcmF64Le, 8, true, false)]
    [InlineData(CodecIds.PcmF64Be, 8, true, true)]
    public void ConvertsEveryFormatByTheDefinition(string codec, int bytes, bool isFloat, bool bigEndian)
    {
        var random = new Random((bytes * 31) + codec.Length);
        var data = new byte[bytes * 2 * 500];
        random.NextBytes(data);
        for (int i = 0; isFloat && i < data.Length; i += bytes)
        {
            // Finite values only: a random exponent is often a NaN or an infinity.
            double value = (random.NextDouble() * 4) - 2;
            byte[] encoded = bytes == 4 ? BitConverter.GetBytes((float)value) : BitConverter.GetBytes(value);
            if (bigEndian)
            {
                Array.Reverse(encoded);
            }

            encoded.CopyTo(data, i);
        }

        var frame = new PcmDecoder(Stream(codec, 2)).Decode(data);
        Assert.Equal(500, frame.SampleCount);
        for (int i = 0; i < 1000; i++)
        {
            var sample = data.AsSpan(i * bytes, bytes).ToArray();
            if (bigEndian)
            {
                Array.Reverse(sample);
            }

            float expected = (isFloat, bytes) switch
            {
                (true, 4) => BitConverter.ToSingle(sample),
                (true, _) => (float)BitConverter.ToDouble(sample),
                _ => (float)(SignedLittleEndian(sample) / Math.Pow(2, (8 * bytes) - 1)),
            };
            Assert.Equal(expected, frame.Samples[i % 2][i / 2]);
        }
    }

    /// <summary>The extremes of 24 and 32 bits: -1 and just under 1.</summary>
    [Fact]
    public void ConvertsTheWiderExtremes()
    {
        var s24 = new PcmDecoder(Stream(CodecIds.PcmS24Be, 1)).Decode([0x80, 0x00, 0x00, 0x7F, 0xFF, 0xFF]);
        Assert.Equal([-1f, 8388607 / 8388608f], s24.Samples[0]);
        var s32 = new PcmDecoder(Stream(CodecIds.PcmS32Le, 1)).Decode([0x00, 0x00, 0x00, 0x80, 0xFF, 0xFF, 0xFF, 0x7F]);
        Assert.Equal([-1f, 1f], s32.Samples[0]); // 2147483647 / 2^31 rounds to 1 as a float, as in FFmpeg
    }

    /// <summary>The PCM formats and nothing else.</summary>
    [Fact]
    public void CanDecodeThePcmFormats()
    {
        Assert.True(PcmDecoder.CanDecode(CodecIds.PcmU8));
        Assert.True(PcmDecoder.CanDecode(CodecIds.PcmF64Be));
        Assert.False(PcmDecoder.CanDecode(CodecIds.Aac));
        Assert.False(PcmDecoder.CanDecode("0x0001"));
        Assert.False(PcmDecoder.CanDecode(null));
    }

    private static long SignedLittleEndian(byte[] sample)
    {
        long value = 0;
        for (int i = sample.Length - 1; i >= 0; i--)
        {
            value = (value << 8) | sample[i];
        }

        int shift = 64 - (8 * sample.Length);
        return (value << shift) >> shift;
    }

    /// <summary>
    /// Bytes after the last whole sample frame are ignored, as FFmpeg ignores them; a packet shorter than one frame is
    /// malformed; another codec is not this decoder's.
    /// </summary>
    [Fact]
    public void IgnoresPartialFramesAndRefusesOtherCodecs()
    {
        var decoder = new PcmDecoder(Stream(CodecIds.PcmS16Le, 2));
        var frame = decoder.Decode([0x00, 0x40, 0x00, 0xC0, 0x01, 0x02, 0x03]);
        Assert.Equal([0.5f], frame.Samples[0]);
        Assert.Equal([-0.5f], frame.Samples[1]);
        Assert.Equal(0, decoder.Decode([]).SampleCount);
        Assert.Throws<InvalidDataException>(() => decoder.Decode([1, 2, 3]));
        Assert.Throws<NotSupportedException>(() => new PcmDecoder(Stream("0x0001", 1)));
    }

    /// <summary><see cref="PcmDecoder.Decode(Packet)"/> drops SkipSamples (running on into the next packets) and SkipEndSamples.</summary>
    [Fact]
    public void DropsWhatThePacketsSay()
    {
        var decoder = new PcmDecoder(Stream(CodecIds.PcmU8, 1));
        byte[] four = [128, 129, 130, 131];
        var first = decoder.Decode(new Packet(0, 0, four, true, 0, 0, 4, SkipSamples: 6));
        var second = decoder.Decode(new Packet(0, 1, four, true, 4, 4, 4) { SkipEndSamples = 1 });
        Assert.Equal(0, first.SampleCount);
        Assert.Equal([2 / 128f], second.Samples[0]);
        Assert.Equal(6, second.Pts);
    }

    private static AudioStreamInfo Stream(string codec, int channels) =>
        new(codec, 8000, channels, 0, new Rational(1, 8000), ReadOnlyMemory<byte>.Empty, 0);
}
