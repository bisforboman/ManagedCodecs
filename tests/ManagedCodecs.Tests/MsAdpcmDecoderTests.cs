using System.Buffers.Binary;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests;

/// <summary>
/// <see cref="MsAdpcmDecoder"/>: every sample with a name.msadpcm.f32 reference (FFmpeg's adpcm_ms output) decoded
/// exactly, every sample and no more; and its own rules on built blocks, whose expected samples are FFmpeg's output for
/// the same blocks in a WAV file.
/// </summary>
public class MsAdpcmDecoderTests
{
    // FFmpeg's samples for Blocks() (ffmpeg -i blocks.wav -f s16le): 42 for each whole block, 6 for the one cut short.
    private static readonly short[] Expected =
    [
        -50, 100, 270, 474, 726, 1042, 1453, 2044, 3055, 3923, 3895, 2025, -2915, -11775, -24160, -32768, -32768, -32768,
        -32768, -32083, -30168, -26597, -21046, -12530, 1668, 29124, 20244, -32768, -32768, -32768, -32768, -32768, -32768,
        -32768, 32767, 32767, 32767, 32767, 32767, 32767, -32768, -32768,
        2000, -1000, -4243, -5478, -4504, -2068, 835, 3222, 4331, 3417, 1307, -1183, -3074, -3634, -3212, -2661, 961, 8131,
        12995, 15073, 13587, 8171, 2961, 438, 1952, 6156, 12992, 24539, 32767, 32767, 32767, -32768, -32768, 32767,
        -32768, -32768, -32768, -32768, -32768, -20480, -1664, -32768,
        0, 0, -32768, -32768, -32768, -32768, -32768, -32768, -32768, -32768, -32768, -32768, -32768, -32768, -32768,
        -32768, -32768, -32768, 32767, 32767, 32767, 32767, 32767, 32767, 32767, 32767, 32767, 32767, 32767, 32767,
        -32768, -32768, -32768, -32768, 32767, -32768, -32768, 32767, 32767, 32767, 32767, 32767,
        -7, 7, 2503, -515, -1909, 1655,
    ];

    // The standard coefficient pairs, as a file's WAVEFORMATEX lists them after wSamplesPerBlock and wNumCoef.
    private static readonly short[] StandardCoefficients = [256, 0, 512, -256, 0, 0, 192, 64, 240, 0, 460, -208, 392, -232];

    public static TheoryData<string> Samples() => new(TestSamples.WithReference(".msadpcm.f32"));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegExactly(string sampleName)
    {
        var reference = File.ReadAllBytes(TestSamples.ReferenceOf(sampleName, ".msadpcm.f32"));
        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var audio = demuxer.Audio!;
        Assert.Equal(CodecIds.AdpcmMs, audio.Codec);
        var decoder = new MsAdpcmDecoder(audio);

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

    /// <summary>
    /// A packet of several mono blocks is cut by wSamplesPerBlock, each decoded from its own header (predictors 0, 1, 3
    /// and 6, a negative step, a step driven up to its cap); the last, cut short, gives its header's two samples and two
    /// a byte. Block by block, the same.
    /// </summary>
    [Fact]
    public void DecodesBlocksAsFfmpeg()
    {
        var blocks = Blocks();
        Assert.Equal(Expected.Select(x => x / 32768f), new MsAdpcmDecoder(Stream(1, 42)).Decode(blocks).Samples[0]);

        var decoder = new MsAdpcmDecoder(Stream(1, 42));
        var pieces = new[] { blocks[..27], blocks[27..54], blocks[54..81], blocks[81..] }.SelectMany(b => decoder.Decode(b).Samples[0]);
        Assert.Equal(Expected.Select(x => x / 32768f), pieces);
    }

    /// <summary>
    /// The step is capped at int.MaxValue / 768, as FFmpeg caps it: predictor 2 (coefficients 0, 0) makes each sample the
    /// code times the step; six codes of -8 drive the step past the cap, 46 of 0 shrink it (by 230 / 256 each), and the
    /// codes of 1 then show it: FFmpeg's 20273 from the capped step, where an uncapped one would still clamp to 32767.
    /// </summary>
    [Fact]
    public void CapsTheStepAsFfmpeg()
    {
        byte[] codes = [0x88, 0x88, 0x88, .. new byte[23], 0x11, 0x11];
        var frame = new MsAdpcmDecoder(Stream(1, 0)).Decode(Block(2, 32767, 0, 0, codes));
        Assert.Equal(58, frame.SampleCount);
        Assert.Equal(new[] { 20273, 18214, 16364, 14702 }.Select(x => x / 32768f), frame.Samples[0][^4..]);
    }

    /// <summary>
    /// The coefficients are always the standard pairs: a file's own table (here pair 1 changed) is not read, as FFmpeg
    /// doesn't read it (its output on such a file is the same).
    /// </summary>
    [Fact]
    public void TakesTheStandardCoefficientsWhateverTheFileSays()
    {
        var table = StandardCoefficients.ToArray();
        (table[2], table[3]) = (300, -100);
        var frame = new MsAdpcmDecoder(Stream(1, 42, table)).Decode(Blocks());
        Assert.Equal(Expected.Select(x => x / 32768f), frame.Samples[0]);
    }

    /// <summary>
    /// Stereo: the header's fields a channel each in turn, then a code of each channel a byte, the left one in the high
    /// nibble. The first two mono blocks' codes, paired, decode to their samples on each channel.
    /// </summary>
    [Fact]
    public void PairsTheChannelsInEachByte()
    {
        var blocks = Blocks();
        var (left, right) = (blocks[..27], blocks[27..54]);
        var stereo = new byte[14 + 40];
        stereo[0] = left[0];
        stereo[1] = right[0];
        for (int field = 0; field < 3; field++)
        {
            left.AsSpan(1 + (2 * field), 2).CopyTo(stereo.AsSpan(2 + (4 * field)));
            right.AsSpan(1 + (2 * field), 2).CopyTo(stereo.AsSpan(4 + (4 * field)));
        }

        for (int i = 0; i < 40; i++)
        {
            stereo[14 + i] = (byte)((Nibble(left, i) << 4) | Nibble(right, i));
        }

        var frame = new MsAdpcmDecoder(Stream(2, 42)).Decode(stereo);
        Assert.Equal(Expected[..42].Select(x => x / 32768f), frame.Samples[0]);
        Assert.Equal(Expected[42..84].Select(x => x / 32768f), frame.Samples[1]);

        // A mono block's i-th code: the high nibble of each byte first.
        static int Nibble(byte[] block, int i) => i % 2 == 0 ? block[7 + (i / 2)] >> 4 : block[7 + (i / 2)] & 0xF;
    }

    /// <summary>
    /// A predictor index past the standard seven (FFmpeg: "block_predictor = 7"), or a block shorter than its headers,
    /// is malformed; more than two channels, or another codec, is not this decoder's; an empty packet gives nothing.
    /// </summary>
    [Fact]
    public void RefusesWhatIsNotMsAdpcm()
    {
        var decoder = new MsAdpcmDecoder(Stream(1, 0));
        Assert.Equal(0, decoder.Decode([]).SampleCount);
        Assert.Throws<InvalidDataException>(() => decoder.Decode([7, 20, 0, 0, 0, 0, 0, 0x11]));
        Assert.Throws<InvalidDataException>(() => decoder.Decode([0, 20, 0, 0, 0, 0]));
        Assert.Equal(4, decoder.Decode([6, 20, 0, 0, 0, 0, 0, 0x11]).SampleCount);
        Assert.Throws<NotSupportedException>(() => new MsAdpcmDecoder(Stream(3, 0)));
        Assert.Throws<NotSupportedException>(() => new MsAdpcmDecoder(Stream(1, 0) with { Codec = "0x0002" }));
    }

    /// <summary><see cref="MsAdpcmDecoder.Decode(Packet)"/> drops SkipSamples (running on into the next packets) and SkipEndSamples.</summary>
    [Fact]
    public void DropsWhatThePacketsSay()
    {
        var decoder = new MsAdpcmDecoder(Stream(1, 42));
        var block = Blocks()[..27];
        var first = decoder.Decode(new Packet(0, 0, block, true, 0, 0, 42, SkipSamples: 45));
        var second = decoder.Decode(new Packet(0, 1, block, true, 42, 42, 42) { SkipEndSamples = 30 });
        Assert.Equal(0, first.SampleCount);
        Assert.Equal(Expected[3..12].Select(x => x / 32768f), second.Samples[0]);
        Assert.Equal(45, second.Pts);
    }

    /// <summary>The WAVE format tag 0x02 at 4 bits is <see cref="CodecIds.AdpcmMs"/>, its extra bytes the codec private data (wSamplesPerBlock first).</summary>
    [Fact]
    public void WaveFilesNameIt()
    {
        using var demuxer = Demuxer.Open(TestSamples.PathOf("wav-ms-adpcm.wav"));
        Assert.Equal(CodecIds.AdpcmMs, demuxer.Audio!.Codec);
        Assert.Equal(2036, BinaryPrimitives.ReadUInt16LittleEndian(demuxer.Audio.CodecPrivate.Span));
        Assert.Equal(StandardCoefficients, Enumerable.Range(0, 14).Select(i => BinaryPrimitives.ReadInt16LittleEndian(demuxer.Audio.CodecPrivate.Span[(4 + (2 * i))..])));
    }

    // Mono blocks of 27 bytes (wSamplesPerBlock 42): a header (predictor, step, the last sample and the one before it)
    // and 20 bytes of codes; then one cut short after 2 bytes of codes. As built for FFmpeg's reference.
    private static byte[] Blocks() =>
    [
        .. Block(1, 20, 100, -50, [0x12, 0x34, 0x56, 0x7F, 0x9A, 0xBC, 0xDE, 0xF0, 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0x70, 0x07, 0x77, 0x88]),
        .. Block(6, -300, -1000, 2000, [0x37, 0x1C, 0xE2, 0x5A, 0x0F, 0xF0, 0x99, 0x66, 0x12, 0x21, 0x34, 0x43, 0x56, 0x65, 0x78, 0x87, 0x9F, 0xF9, 0xA0, 0x0A]),
        .. Block(0, 32767, 0, 0, [0x88, 0x88, 0x88, 0x88, 0x88, 0x88, 0x88, 0x88, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x80, 0x08, 0x7F, 0xF7, 0x31, 0x13]),
        .. Block(3, 500, 7, -7, [0x5D, 0xD5]),
    ];

    private static byte[] Block(byte predictor, short delta, short sample1, short sample2, byte[] codes)
    {
        var block = new byte[7 + codes.Length];
        block[0] = predictor;
        BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(1), delta);
        BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(3), sample1);
        BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(5), sample2);
        codes.CopyTo(block, 7);
        return block;
    }

    // A stream with wSamplesPerBlock (none if 0) and a coefficient table (the standard one if none given).
    private static AudioStreamInfo Stream(int channels, int samplesPerBlock, short[]? coefficients = null)
    {
        byte[] codecPrivate = [];
        if (samplesPerBlock > 0)
        {
            var table = coefficients ?? StandardCoefficients;
            codecPrivate = new byte[4 + (2 * table.Length)];
            BinaryPrimitives.WriteUInt16LittleEndian(codecPrivate, (ushort)samplesPerBlock);
            BinaryPrimitives.WriteUInt16LittleEndian(codecPrivate.AsSpan(2), (ushort)(table.Length / 2));
            for (int i = 0; i < table.Length; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(codecPrivate.AsSpan(4 + (2 * i)), table[i]);
            }
        }

        return new(CodecIds.AdpcmMs, 8000, channels, 0, new Rational(1, 8000), codecPrivate, 0);
    }
}
