using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests;

/// <summary>
/// <see cref="G726Decoder"/> against the Recommendation, not FFmpeg (docs/decisions/2026-11-15-g726.md): every sample
/// with a name.g726.f32 reference, the output of tools/g726-reference.py, which its <c>verify</c> proves on ITU-T
/// G.726's Appendix II test sequences, decoded exactly, every sample and no more. FFmpeg's adpcm_g726 gives other samples
/// (its own files, name.g726.ffmpeg.f32, are only kept to show by how much). Also the packing of RTP's G726-* (from each
/// byte's least significant bit), packets cut anywhere between codes, and what the decoder refuses.
/// </summary>
public class G726DecoderTests
{
    public static TheoryData<string> Samples() => new(TestSamples.WithReference(".g726.f32"));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesTheRecommendationExactly(string sampleName)
    {
        var reference = Floats(File.ReadAllBytes(TestSamples.ReferenceOf(sampleName, ".g726.f32")));
        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var audio = demuxer.Audio!;
        Assert.Equal((CodecIds.AdpcmG726, 1, 8000), (audio.Codec, audio.Channels, audio.SampleRate));
        Assert.Equal([(byte)Bits(sampleName)], audio.CodecPrivate.ToArray());

        var decoder = new G726Decoder(audio);
        var decoded = new List<float>();
        foreach (var packet in demuxer.ReadPackets())
        {
            var frame = decoder.Decode(packet);
            decoded.AddRange(frame.Samples[0].AsSpan(0, frame.SampleCount).ToArray());
        }

        Assert.Equal(reference.Length, decoded.Count);
        for (int i = 0; i < reference.Length; i++)
        {
            Assert.True(reference[i] == decoded[i], $"Sample {i}: {decoded[i] * 8192} where the Recommendation gives {reference[i] * 8192}.");
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DecodesTheCodesPackedFromTheLeastSignificantBitAlike(int bits)
    {
        // RTP's G726-* (RFC 3551, 4.5.4): the same codes, each byte filled from its least significant bit.
        var data = Data($"wav-g726-itu-{8 * bits}.wav");
        var codes = Unpack(data, bits, leastSignificantFirst: false);
        var expected = new G726Decoder(Stream(CodecIds.AdpcmG726, bits)).Decode(data).Samples[0];
        var decoded = new G726Decoder(Stream(CodecIds.AdpcmG726Le, bits)).Decode(Pack(codes, bits, leastSignificantFirst: true)).Samples[0];

        Assert.Equal(expected, decoded);
        Assert.Equal(codes, Unpack(Pack(codes, bits, leastSignificantFirst: true), bits, leastSignificantFirst: true));
    }

    [Theory]
    [InlineData(CodecIds.AdpcmG726, 2)]
    [InlineData(CodecIds.AdpcmG726, 3)]
    [InlineData(CodecIds.AdpcmG726Le, 4)]
    [InlineData(CodecIds.AdpcmG726Le, 5)]
    public void CarriesItsStateFromPacketToPacket(string codec, int bits)
    {
        // Packets of whole codes (a multiple of `bits` bytes: 8 codes), cut at random: the same samples as one packet.
        bool leastSignificantFirst = codec == CodecIds.AdpcmG726Le;
        var codes = Unpack(Data($"wav-g726-itu-{8 * bits}.wav"), bits, leastSignificantFirst: false);
        var data = Pack(codes, bits, leastSignificantFirst);
        var whole = new G726Decoder(Stream(codec, bits)).Decode(data).Samples[0];

        var random = new Random(bits);
        var decoder = new G726Decoder(Stream(codec, bits));
        var pieces = new List<float>();
        for (int at = 0; at < data.Length;)
        {
            int length = Math.Min(data.Length - at, bits * random.Next(1, 40));
            var frame = decoder.Decode(data.AsSpan(at, length));
            Assert.Equal(length * 8 / bits, frame.SampleCount);
            pieces.AddRange(frame.Samples[0]);
            at += length;
        }

        Assert.Equal(whole, pieces);
    }

    [Theory]
    [InlineData(3, 1, 2)] // 8 bits of 3-bit codes: two, and 2 bits left over
    [InlineData(5, 3, 4)] // 24 bits of 5-bit codes: four, and 4 bits left over
    [InlineData(2, 1, 4)]
    public void DecodesAPacketsWholeCodesAndIgnoresTheBitsAfter(int bits, int bytes, int samples)
    {
        var data = Data($"wav-g726-itu-{8 * bits}.wav")[..bytes];
        Assert.Equal(samples, new G726Decoder(Stream(CodecIds.AdpcmG726, bits)).Decode(data).SampleCount);
        Assert.Equal(samples, new G726Decoder(Stream(CodecIds.AdpcmG726Le, bits)).Decode(data).SampleCount);
    }

    [Fact]
    public void TakesFourBitsWhenTheStreamSaysNone()
    {
        // G.721's 32 kbit/s, when nothing gives the rate: a stream without CodecPrivate.
        var data = Data("wav-g726-itu-32.wav");
        var stream = new AudioStreamInfo(CodecIds.AdpcmG726, 8000, 1, 0, new Rational(1, 8000), default, 0);
        Assert.Equal(new G726Decoder(Stream(CodecIds.AdpcmG726, 4)).Decode(data).Samples[0], new G726Decoder(stream).Decode(data).Samples[0]);
    }

    [Fact]
    public void RefusesWhatIsNotG726AsItDecodesIt()
    {
        Assert.Throws<NotSupportedException>(() => new G726Decoder(Stream(CodecIds.PcmAlaw, 4)));
        Assert.Throws<NotSupportedException>(() => new G726Decoder(Stream(CodecIds.AdpcmG726, 1)));
        Assert.Throws<NotSupportedException>(() => new G726Decoder(Stream(CodecIds.AdpcmG726, 6)));
        Assert.Throws<NotSupportedException>(() => new G726Decoder(new AudioStreamInfo(CodecIds.AdpcmG726, 8000, 2, 0, new Rational(1, 8000), new byte[] { 4 }, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new G726Decoder(new AudioStreamInfo(CodecIds.AdpcmG726, 0, 1, 0, new Rational(1, 8000), new byte[] { 4 }, 0)));
    }

    /// <summary>The WAVE format tag 0x45 is G.726 in mono at 2 to 5 bits a code, wBitsPerSample its CodecPrivate; else its tag.</summary>
    [Theory]
    [InlineData(1, 4, CodecIds.AdpcmG726)]
    [InlineData(1, 2, CodecIds.AdpcmG726)]
    [InlineData(1, 5, CodecIds.AdpcmG726)]
    [InlineData(1, 6, "0x0045")]
    [InlineData(2, 4, "0x0045")]
    public void ReadsTheWaveFormatTag(int channels, int bits, string codec)
    {
        var file = File.ReadAllBytes(TestSamples.PathOf("wav-g726-itu-32.wav"));
        int fmt = file.AsSpan().IndexOf("fmt "u8);
        file[fmt + 10] = (byte)channels;
        file[fmt + 22] = (byte)bits;
        using var demuxer = new WavDemuxer(new MemoryStream(file));

        Assert.Equal(codec, demuxer.Audio!.Codec);
        if (codec == CodecIds.AdpcmG726)
        {
            Assert.Equal([(byte)bits], demuxer.Audio.CodecPrivate.ToArray());
        }
    }

    internal static AudioStreamInfo Stream(string codec, int bits) =>
        new(codec, 8000, 1, 0, new Rational(1, 8000), new byte[] { (byte)bits }, 0);

    // The codes of a .wav file's data chunk, as FFmpeg's muxer packs them (from each byte's most significant bit).
    internal static byte[] Data(string name)
    {
        var file = File.ReadAllBytes(TestSamples.PathOf(name));
        int data = file.AsSpan().IndexOf("data"u8);
        int length = BitConverter.ToInt32(file, data + 4);
        return file.AsSpan(data + 8, Math.Min(length, file.Length - data - 8)).ToArray();
    }

    internal static int[] Unpack(byte[] data, int bits, bool leastSignificantFirst)
    {
        var codes = new int[data.Length * 8 / bits];
        for (int i = 0; i < codes.Length; i++)
        {
            for (int b = 0; b < bits; b++)
            {
                int bit = (i * bits) + b;
                int value = leastSignificantFirst ? (data[bit / 8] >> (bit % 8)) & 1 : (data[bit / 8] >> (7 - (bit % 8))) & 1;
                codes[i] |= value << (leastSignificantFirst ? b : bits - 1 - b);
            }
        }

        return codes;
    }

    internal static byte[] Pack(int[] codes, int bits, bool leastSignificantFirst)
    {
        var data = new byte[((codes.Length * bits) + 7) / 8];
        for (int i = 0; i < codes.Length; i++)
        {
            for (int b = 0; b < bits; b++)
            {
                int bit = (i * bits) + b;
                int value = leastSignificantFirst ? (codes[i] >> b) & 1 : (codes[i] >> (bits - 1 - b)) & 1;
                data[bit / 8] |= (byte)(leastSignificantFirst ? value << (bit % 8) : value << (7 - (bit % 8)));
            }
        }

        return data;
    }

    private static int Bits(string name) => int.Parse(name[(name.LastIndexOf('-') + 1)..name.IndexOf('.')], System.Globalization.CultureInfo.InvariantCulture) / 8;

    private static float[] Floats(byte[] bytes)
    {
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
