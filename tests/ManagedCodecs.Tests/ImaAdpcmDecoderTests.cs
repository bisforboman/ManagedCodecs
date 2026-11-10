using System.Buffers.Binary;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests;

/// <summary>
/// <see cref="ImaAdpcmDecoder"/>: every AVI sample with a name.ima.f32 reference (FFmpeg's adpcm_ima_wav output) decoded
/// exactly, every sample and no more; and its own rules on built blocks, whose expected samples are FFmpeg's output for
/// the same blocks in a WAV file.
/// </summary>
public class ImaAdpcmDecoderTests
{
    // Mono blocks of 12 bytes (wSamplesPerBlock 17): a header (first sample, step index, reserved) and 8 bytes of
    // nibbles, low nibble first. The first starts at the smallest step, where rounding the difference once and IMA's
    // shifts part most (13 against 11 for the first nibble); the second in the middle of the table; the third, cut short
    // after one byte of nibbles, at the last step.
    private static readonly byte[] Blocks =
    [
        0x00, 0x00, 0, 0, 0x77, 0x77, 0x0F, 0xF0, 0x31, 0x9A, 0xC4, 0x58,
        0x18, 0xFC, 40, 0, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0,
        0xF4, 0x01, 88, 0, 0x7F, 0x11, 0x22,
    ];

    // FFmpeg's samples for Blocks (ffmpeg -i blocks.wav -f s16le): 17 each for the whole blocks, the header's alone for
    // the one cut short (its 3 bytes are no whole group of 4).
    private static readonly short[] Expected =
    [
        0, 13, 43, 106, 242, -52, -10, 28, -495, -271, 205, -103, -271, 188, -367, -441, 307,
        -1000, -790, -675, -362, -68, 430, 1178, 1079, 2436, 1466, 937, -505, -1863, -4155, -7591, -7134, -13372,
        500,
    ];

    public static TheoryData<string> Samples() => new(TestSamples.WithReference(".ima.f32"));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegExactly(string sampleName)
    {
        var reference = File.ReadAllBytes(TestSamples.ReferenceOf(sampleName, ".ima.f32"));
        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var audio = demuxer.Audio!;
        Assert.Equal(CodecIds.AdpcmImaWav, audio.Codec);
        var decoder = new ImaAdpcmDecoder(audio);

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
    /// A packet of several blocks is cut by wSamplesPerBlock, each block decoded from its own header; the last, cut short,
    /// gives its whole groups only. The values are FFmpeg's, which round the difference once.
    /// </summary>
    [Fact]
    public void DecodesBlocksAsFfmpeg()
    {
        var frame = new ImaAdpcmDecoder(Stream(1, 17)).Decode(Blocks);
        Assert.Equal(Expected.Select(x => x / 32768f), frame.Samples[0]);

        // Block by block, the same.
        var decoder = new ImaAdpcmDecoder(Stream(1, 17));
        var pieces = new[] { Blocks[..12], Blocks[12..24], Blocks[24..] }.SelectMany(b => decoder.Decode(b).Samples[0]);
        Assert.Equal(Expected.Select(x => x / 32768f), pieces);
    }

    /// <summary>Without wSamplesPerBlock a packet is one block; the channels take turns, 4 bytes each, after a header each.</summary>
    [Fact]
    public void InterleavesTheChannelsByGroups()
    {
        // Two channels, one group each: the first block's nibbles on the left, the second block's on the right.
        byte[] stereo = [.. Blocks[..4], .. Blocks[12..16], .. Blocks[4..8], .. Blocks[16..20]];
        var frame = new ImaAdpcmDecoder(Stream(2, 0)).Decode(stereo);
        Assert.Equal(Expected[..9].Select(x => x / 32768f), frame.Samples[0]);

        // The right channel's first 8 nibbles as a block of its own.
        var right = new ImaAdpcmDecoder(Stream(1, 0)).Decode([.. Blocks[12..20]]);
        Assert.Equal(right.Samples[0], frame.Samples[1]);
    }

    /// <summary>
    /// A block shorter than its headers, or a step index past 88, is malformed; another codec is not this decoder's; an
    /// empty packet gives nothing.
    /// </summary>
    [Fact]
    public void RefusesWhatIsNotIma()
    {
        var decoder = new ImaAdpcmDecoder(Stream(2, 0));
        Assert.Equal(0, decoder.Decode([]).SampleCount);
        Assert.Throws<InvalidDataException>(() => decoder.Decode([0, 0, 0, 0, 0, 0, 0]));
        Assert.Throws<InvalidDataException>(() => decoder.Decode([0, 0, 89, 0, 0, 0, 0, 0]));
        Assert.Equal(2, decoder.Decode([0, 0, 88, 0, 0, 0, 88, 0]).Channels);
        Assert.Throws<NotSupportedException>(() => new ImaAdpcmDecoder(Stream(1, 0) with { Codec = "0x0011" }));
    }

    /// <summary><see cref="ImaAdpcmDecoder.Decode(Packet)"/> drops SkipSamples (running on into the next packets) and SkipEndSamples.</summary>
    [Fact]
    public void DropsWhatThePacketsSay()
    {
        var decoder = new ImaAdpcmDecoder(Stream(1, 17));
        var block = Blocks[..12];
        var first = decoder.Decode(new Packet(0, 0, block, true, 0, 0, 17, SkipSamples: 20));
        var second = decoder.Decode(new Packet(0, 1, block, true, 17, 17, 17) { SkipEndSamples = 10 });
        Assert.Equal(0, first.SampleCount);
        Assert.Equal(Expected[3..7].Select(x => x / 32768f), second.Samples[0]);
        Assert.Equal(20, second.Pts);
    }

    /// <summary>
    /// AVI names IMA ADPCM <see cref="CodecIds.AdpcmImaWav"/> at 4 bits only, as FFmpeg's encoder writes it; at 3 bits
    /// (FFmpeg decodes 2 to 5) it stays the format tag, skipped.
    /// </summary>
    [Fact]
    public void AviNamesOnlyFourBitStreams()
    {
        var file = File.ReadAllBytes(TestSamples.PathOf("avi-mjpeg-ima.avi"));
        using (var demuxer = new AviDemuxer(new MemoryStream(file)))
        {
            Assert.Equal(CodecIds.AdpcmImaWav, demuxer.Audio!.Codec);
            Assert.Equal(505, BinaryPrimitives.ReadUInt16LittleEndian(demuxer.Audio.CodecPrivate.Span));
        }

        // The audio 'strf': wFormatTag 0x11, 2 channels, 22050 Hz; wBitsPerSample is 14 bytes in.
        int format = file.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x11, 0x00, 0x02, 0x00, 0x22, 0x56, 0x00, 0x00]);
        Assert.True(format > 0);
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(format + 14)));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(format + 14), 3);
        using var threeBits = new AviDemuxer(new MemoryStream(file));
        Assert.Equal("0x0011", threeBits.Audio!.Codec);
    }

    private static AudioStreamInfo Stream(int channels, int samplesPerBlock)
    {
        byte[] codecPrivate = samplesPerBlock > 0 ? [(byte)samplesPerBlock, (byte)(samplesPerBlock >> 8)] : [];
        return new(CodecIds.AdpcmImaWav, 8000, channels, 0, new Rational(1, 8000), codecPrivate, 0);
    }
}
