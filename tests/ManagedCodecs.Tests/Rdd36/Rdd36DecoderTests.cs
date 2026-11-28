using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ManagedCodecs.Rdd36;

namespace ManagedCodecs.Tests.Rdd36;

/// <summary>
/// <see cref="Rdd36Decoder"/> on frames written by <see cref="Rdd36FrameWriter"/>: what FFmpeg's encoders never write
/// and FFmpeg's decoder refuses or reads otherwise than RDD 36 says (so that no sample can have its reference: a
/// slice header with more bytes than its elements, reserved bits that are set, a frame header's aspect ratio),
/// pictures of sizes the encoders don't take, the header's rules, and damage. The samples an FFmpeg reference exists
/// for are <see cref="Rdd36ConformanceTests"/>'.
/// </summary>
public class Rdd36DecoderTests
{
    public static TheoryData<int, int, int> Sizes() => new()
    {
        // width, height, log2_desired_slice_size_in_mb
        { 16, 16, 3 },
        { 2, 2, 0 }, // inside the first blocks: one luma column pair, one chroma column
        { 17, 33, 1 }, // one column and one row into the next macroblocks; an odd width (9 chroma columns)
        { 9, 7, 2 },
        { 240, 16, 3 }, // 15 macroblocks: slices of 8, 4, 2 and 1
        { 240, 16, 2 },
        { 240, 16, 1 },
        { 240, 16, 0 },
        { 330, 250, 3 },
        { 720, 22, 3 }, // RDD 36's example row: 45 macroblocks, slices of 8, 8, 8, 8, 8, 4 and 1
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void DecodesPicturesOfAnySizeAndSliceSize(int width, int height, int log2SliceSize)
    {
        var random = new Random((width * 1000) + height + log2SliceSize);
        var writer = new Rdd36FrameWriter(width, height)
        {
            Log2SliceSize = log2SliceSize,
            LumaMatrix = RandomMatrix(random),
            ChromaMatrix = RandomMatrix(random),

            // Every index there is, the reserved ones too: 0 as 1, above 224 as 224 (as FFmpeg; rdd36-rw-reserved-index).
            QuantizationIndex = slice => (slice * 37) % 256,
        };
        var picture = RandomPicture(writer, random);

        using var frame = new Rdd36Decoder().Decode(writer.Write(picture));

        AssertSamples(writer.Expected(picture), frame);
        Assert.Equal((width, height, PixelFormat.Yuv422P10, PictureType.I), (frame.Width, frame.Height, frame.Format, frame.PictureType));
        Assert.False(frame.IsInterlaced);
    }

    [Fact]
    public void UsesTheDefaultMatrixAndTheLumaMatrixForChroma()
    {
        // No matrix: 4 everywhere, both components. A luma matrix alone: the chroma's too (7.3).
        var random = new Random(3);
        var plain = new Rdd36FrameWriter(48, 32);
        var lumaOnly = new Rdd36FrameWriter(48, 32) { LumaMatrix = RandomMatrix(random) };
        var chromaOnly = new Rdd36FrameWriter(48, 32) { ChromaMatrix = RandomMatrix(random) };
        foreach (var writer in new[] { plain, lumaOnly, chromaOnly })
        {
            var picture = RandomPicture(writer, random);
            using var frame = new Rdd36Decoder().Decode(writer.Write(picture));
            AssertSamples(writer.Expected(picture), frame);
        }

        Assert.All(plain.EffectiveChromaMatrix, weight => Assert.Equal(4, weight));
        Assert.Equal(lumaOnly.LumaMatrix, lumaOnly.EffectiveChromaMatrix);
    }

    [Fact]
    public void FindsEachStructureByTheSizeItsHeaderGives()
    {
        // 6.4: "decoders shall use the specified size—rather than inference from the syntax itself—to determine the
        // start of the immediately following syntax structure". FFmpeg's decoder takes the frame and picture
        // headers' extra bytes (rdd36-rw-headers) but refuses a longer slice header.
        var random = new Random(4);
        var writer = new Rdd36FrameWriter(50, 40) { ExtraHeaderBytes = (7, 5, 3), LumaMatrix = RandomMatrix(random) };
        var picture = RandomPicture(writer, random);

        using var frame = new Rdd36Decoder().Decode(writer.Write(picture));

        AssertSamples(writer.Expected(picture), frame);
    }

    [Fact]
    public void IgnoresReservedBits()
    {
        // 6.1.1: "decoders shall ignore it (in particular shall not expect its value to be 0)". FFmpeg's reads the
        // byte before bitstream_version as part of the version and the picture header's as part of the slice size.
        var random = new Random(5);
        var writer = new Rdd36FrameWriter(50, 40) { ReservedOnes = true, Log2SliceSize = 2 };
        var picture = RandomPicture(writer, random);

        using var frame = new Rdd36Decoder().Decode(writer.Write(picture));

        AssertSamples(writer.Expected(picture), frame);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SkipsZeroBytesAfterAComponentsCoefficients(int zeroBytes)
    {
        // 5.3.2: up to three zero_byte after the alignment bits, from "older encoders" (6.3.2). With the alignment's up
        // to 7 bits, that is the 31 zero bits endOfData() allows.
        var random = new Random(zeroBytes);
        var writer = new Rdd36FrameWriter(40, 24) { ZeroBytes = zeroBytes };
        var picture = RandomPicture(writer, random);

        using var frame = new Rdd36Decoder().Decode(writer.Write(picture));

        AssertSamples(writer.Expected(picture), frame);
    }

    [Fact]
    public void IgnoresStuffingAndWhatFollowsTheFrame()
    {
        var random = new Random(6);
        var writer = new Rdd36FrameWriter(32, 16) { Stuffing = 100 };
        var picture = RandomPicture(writer, random);
        byte[] frameBytes = writer.Write(picture);
        byte[] followed = [.. frameBytes, .. Enumerable.Repeat((byte)0xFF, 50)];

        using var frame = new Rdd36Decoder().Decode(followed);

        Assert.Equal(frameBytes.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(frameBytes)); // frame_size counts the stuffing
        AssertSamples(writer.Expected(picture), frame);
    }

    [Fact]
    public void WrapsCoefficientsBeyond16Bits()
    {
        // Levels no picture gives: the weighted coefficient wraps to 16 bits (docs/conformance.md: FFmpeg's own
        // transforms disagree there, so this is the decoder's definition, which the expectation shares).
        var writer = new Rdd36FrameWriter(16, 16) { QuantizationIndex = _ => 224 };
        int[] Block(int component, int x, int y)
        {
            var levels = new int[64];
            levels[0] = 1000 * (x + 1);
            levels[9] = -70000;
            levels[63] = 123456;
            return levels;
        }

        using var frame = new Rdd36Decoder().Decode(writer.Write(Block));

        AssertSamples(writer.Expected(Block), frame);
    }

    [Fact]
    public void DecodesBitstreamVersion1WithoutAlphaAndRefusesLaterVersions()
    {
        var random = new Random(7);
        var picture = RandomPicture(new Rdd36FrameWriter(16, 16), random);

        using var frame = new Rdd36Decoder().Decode(new Rdd36FrameWriter(16, 16) { Version = 1 }.Write(picture));
        AssertSamples(new Rdd36FrameWriter(16, 16).Expected(picture), frame);

        // 6.1.1: "A decoder shall abort if it encounters a bitstream with an unsupported bitstream_version value."
        Assert.Throws<NotSupportedException>(() => new Rdd36Decoder().Decode(new Rdd36FrameWriter(16, 16) { Version = 2 }.Write(picture)));
        Assert.Throws<NotSupportedException>(() => new Rdd36Decoder().Decode(new Rdd36FrameWriter(16, 16) { Version = 255 }.Write(picture)));
    }

    [Theory]
    [InlineData(1, 2, 1, 0)] // interlaced, top field first
    [InlineData(1, 2, 2, 0)] // interlaced, bottom field first
    [InlineData(1, 3, 0, 0)] // 4:4:4
    [InlineData(1, 2, 0, 1)] // 8-bit alpha
    [InlineData(1, 3, 0, 2)] // 4:4:4 with 16-bit alpha
    public void RefusesWhatItDoesNotDecodeYet(int version, int chromaFormat, int interlaceMode, int alphaChannelType)
    {
        // Only the headers are those of such a frame: the decoder refuses before it reads a slice.
        var writer = new Rdd36FrameWriter(16, 16) { Version = version, ChromaFormat = chromaFormat, InterlaceMode = interlaceMode, AlphaChannelType = alphaChannelType };
        var frame = writer.Write((_, _, _) => new int[64]);

        Assert.Throws<NotSupportedException>(() => new Rdd36Decoder().Decode(frame));
    }

    [Theory]
    [InlineData(1, 0, 0, 0)] // chroma_format 0 and 1 are reserved
    [InlineData(1, 1, 0, 0)]
    [InlineData(1, 2, 3, 0)] // interlace_mode 3 is reserved
    [InlineData(1, 2, 0, 3)] // alpha_channel_type 3 to 15 are reserved
    [InlineData(1, 2, 0, 15)]
    [InlineData(0, 3, 0, 0)] // version 0 is 4:2:2 without alpha (6.4)
    [InlineData(0, 2, 0, 1)]
    public void RejectsReservedAndContradictoryHeaders(int version, int chromaFormat, int interlaceMode, int alphaChannelType)
    {
        var writer = new Rdd36FrameWriter(16, 16) { Version = version, ChromaFormat = chromaFormat, InterlaceMode = interlaceMode, AlphaChannelType = alphaChannelType };
        var frame = writer.Write((_, _, _) => new int[64]);

        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(frame));
    }

    [Fact]
    public void RejectsEveryTruncation()
    {
        // Cut anywhere, the frame's frame_size is beyond its data. With frame_size set to what is left, as a damaged
        // file might have it, each structure's own size catches it, or the coefficients end early (which is still a
        // frame, the missing ones zero) or decode to other values; never an exception outside the contract.
        var random = new Random(8);
        var writer = new Rdd36FrameWriter(40, 24) { LumaMatrix = RandomMatrix(random), ExtraHeaderBytes = (1, 1, 1) };
        byte[] whole = writer.Write(RandomPicture(writer, random));
        for (int length = 0; length < whole.Length; length++)
        {
            byte[] cut = whole[..length];
            Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(cut));
            if (length >= 4)
            {
                BinaryPrimitives.WriteUInt32BigEndian(cut, (uint)length);
                AssertKeepsTheContract(cut);
            }
        }
    }

    [Fact]
    public void KeepsTheContractForEveryByteChanged()
    {
        var random = new Random(9);
        var writer = new Rdd36FrameWriter(40, 24) { ChromaMatrix = RandomMatrix(random) };
        byte[] whole = writer.Write(RandomPicture(writer, random));
        for (int at = 0; at < whole.Length; at++)
        {
            foreach (byte value in new byte[] { 0x00, 0xFF, (byte)(whole[at] ^ 0x80), (byte)(whole[at] + 1) })
            {
                byte[] changed = (byte[])whole.Clone();
                changed[at] = value;
                AssertKeepsTheContract(changed);
            }
        }
    }

    [Fact]
    public void RejectsWhatIsNotAFrame()
    {
        byte[] frame = new Rdd36FrameWriter(16, 16).Write((_, _, _) => new int[64]);

        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode([]));
        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(new byte[28]));
        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(Changed(frame, 4, (byte)'j'))); // 'jcpf'
        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(Changed(frame, 3, (byte)(frame[3] + 1)))); // frame_size past the data
        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(Changed(frame, 9, 19))); // frame_header_size below the header's
        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(Changed(frame, 8, 0xFF))); // and beyond the frame
        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(Changed(frame, 27, 2))); // a luma matrix announced, no room for it
        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(Changed(Changed(frame, 16, 0), 17, 0))); // width 0
        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(Changed(Changed(frame, 18, 0), 19, 0))); // height 0
        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(Changed(frame, 28, 7 << 3))); // picture_header_size below the header's
        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(Changed(frame, 29, 1))); // picture_size past the frame
        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(Changed(frame, 37, (byte)(frame[37] + 1)))); // a slice longer than the picture has
        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(Changed(frame, 38, 5 << 3))); // slice_header_size below the header's
        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(Changed(frame, 40, 1))); // luma data longer than the slice
    }

    [Fact]
    public void RejectsAPictureItsDataCannotHold()
    {
        // The largest picture there is, 65535x65535, in a frame of a few bytes: refused by the slice table's size,
        // before a frame of 8 GB is asked for.
        byte[] frame = new Rdd36FrameWriter(16, 16).Write((_, _, _) => new int[64]);
        frame[16] = frame[17] = frame[18] = frame[19] = 0xFF;

        var e = Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(frame));
        Assert.Contains("slice table", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsCoefficientsPastTheSlicesLast()
    {
        // A run that ends beyond the last coefficient of the last block (5.3.2 has no such array element).
        var scanned = new int[4 * 64];
        scanned[^1] = 1;
        var bits = new Rdd36FrameWriter.BitWriter();
        bits.ExpGolomb(0, 5); // first_dc_coeff
        bits.ExpGolomb(0, 3); // dc_coeff_difference 0 (the first by the order-3 code)
        bits.Bits(0b11, 2); // and two more (after a 0, by the order-0 code)
        bits.ExpGolomb((4 * 63) + 5, 0); // a run past the end (previousRun starts at 4: exponential-Golomb, order 0)
        bits.Bits(0b10, 2); // abs_level_minus_1 0 (codebook 1, 0, 1), sign 0
        byte[] luma = bits.ToArray();

        var e = Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(OneMacroblock(luma)));
        Assert.Contains("run", e.Message, StringComparison.Ordinal);

        // The same run ending on the last coefficient is that coefficient.
        bits = new Rdd36FrameWriter.BitWriter();
        bits.ExpGolomb(0, 5);
        bits.ExpGolomb(0, 3);
        bits.Bits(0b11, 2);
        bits.ExpGolomb((4 * 63) - 1, 0);
        bits.Bits(0b10, 2);
        Assert.Equal(Rdd36FrameWriter.ScannedCoefficients(scanned, 4), bits.ToArray());
        new Rdd36Decoder().Decode(OneMacroblock(bits.ToArray())).Dispose();
    }

    [Fact]
    public void RejectsAComponentWithoutData()
    {
        // scanned_coefficients() starts with first_dc_coeff: a component of no bytes has none.
        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(OneMacroblock([])));

        // 32 zero bits and more are no end of data (endOfData() needs 31 or fewer left) and no codeword either.
        Assert.Throws<InvalidDataException>(() => new Rdd36Decoder().Decode(OneMacroblock(new byte[12])));
    }

    [Theory]
    [InlineData(1, 1, 1, ColorPrimaries.Bt709, ColorTransfer.Bt709, ColorMatrix.Bt709)]
    [InlineData(9, 16, 9, ColorPrimaries.Bt2020, ColorTransfer.Smpte2084, ColorMatrix.Bt2020Ncl)]
    [InlineData(12, 18, 6, ColorPrimaries.Smpte432, ColorTransfer.AribStdB67, ColorMatrix.Smpte170M)]
    [InlineData(2, 2, 2, ColorPrimaries.Bt470BG, ColorTransfer.Smpte170M, ColorMatrix.Bt470BG)] // unspecified: the container's
    [InlineData(0, 0, 0, ColorPrimaries.Bt470BG, ColorTransfer.Smpte170M, ColorMatrix.Bt470BG)] // 0 is unknown too in RDD 36 (Tables 5 and 6)
    [InlineData(200, 200, 200, ColorPrimaries.Unspecified, ColorTransfer.Unspecified, ColorMatrix.Unspecified)] // a reserved value is the frame's, unknown
    public void TakesColourFromTheFrameHeaderThenTheContainer(int primaries, int transfer, int matrix, ColorPrimaries expectedPrimaries, ColorTransfer expectedTransfer, ColorMatrix expectedMatrix)
    {
        var stream = new VideoStreamInfo(CodecIds.Rdd36, 16, 16, 25, 1, new Rational(1, 25), default, 0)
        {
            ColorPrimaries = ColorPrimaries.Bt470BG,
            ColorTransfer = ColorTransfer.Smpte170M,
            ColorMatrix = ColorMatrix.Bt470BG,
            ColorRange = ColorRange.Full,
        };
        var writer = new Rdd36FrameWriter(16, 16) { ColorPrimaries = primaries, TransferCharacteristic = transfer, MatrixCoefficients = matrix };
        byte[] bytes = writer.Write((_, _, _) => new int[64]);

        using var frame = new Rdd36Decoder(stream).Decode(bytes);
        using var alone = new Rdd36Decoder().Decode(bytes);

        Assert.Equal((expectedPrimaries, expectedTransfer, expectedMatrix), (frame.ColorPrimaries, frame.ColorTransfer, frame.ColorMatrix));

        // 7.5.1: black at 16 and nominal peak at 235 of 8 bits, whatever the container says of the range.
        Assert.Equal(ColorRange.Limited, frame.ColorRange);
        bool specified = primaries is not (0 or 2);
        Assert.Equal(specified ? expectedPrimaries : ColorPrimaries.Unspecified, alone.ColorPrimaries);
        Assert.Equal(specified ? expectedTransfer : ColorTransfer.Unspecified, alone.ColorTransfer);
        Assert.Equal(specified ? expectedMatrix : ColorMatrix.Unspecified, alone.ColorMatrix);
        Assert.Equal(ColorRange.Limited, alone.ColorRange);
    }

    [Theory]
    [InlineData(0, 720, 486, 0, 1)] // unknown
    [InlineData(1, 720, 486, 1, 1)] // square pixels
    [InlineData(2, 720, 486, 9, 10)] // a 4:3 picture of 720x486: pixels 4 * 486 : 3 * 720
    [InlineData(3, 720, 486, 6, 5)] // a 16:9 picture: 16 * 486 : 9 * 720
    [InlineData(3, 1920, 1080, 1, 1)]
    [InlineData(4, 720, 486, 0, 1)] // reserved
    public void TakesTheAspectRatioFromTheFrameHeaderWithoutAContainers(int information, int width, int height, int numerator, int denominator)
    {
        // Table 3. FFmpeg's decoder leaves the element alone (rdd36-rw-header-colour: its frames keep the 'pasp').
        var writer = new Rdd36FrameWriter(width, height) { AspectRatioInformation = information };
        byte[] bytes = writer.Write((_, _, _) => new int[64]);
        var stream = new VideoStreamInfo(CodecIds.Rdd36, width, height, 25, 1, new Rational(1, 25), default, 0) { SampleAspectRatio = new Rational(16, 11) };

        using var alone = new Rdd36Decoder().Decode(bytes);
        using var silent = new Rdd36Decoder(stream with { SampleAspectRatio = new Rational(0, 1) }).Decode(bytes);
        using var contained = new Rdd36Decoder(stream).Decode(bytes);

        Assert.Equal(new Rational(numerator, denominator), alone.SampleAspectRatio);
        Assert.Equal(new Rational(numerator, denominator), silent.SampleAspectRatio);
        Assert.Equal(new Rational(16, 11), contained.SampleAspectRatio);
    }

    [Fact]
    public async Task GivesOneFrameAPacketAndNoneForADiscardedOne()
    {
        var random = new Random(10);
        var writer = new Rdd36FrameWriter(32, 16);
        var picture = RandomPicture(writer, random);
        byte[] bytes = writer.Write(picture);
        var decoder = new Rdd36Decoder();
        IVideoDecoder asInterface = decoder;

        var frames = decoder.Decode(new Packet(0, 0, bytes, true, 40, 80, 40));
        var later = await asInterface.DecodeAsync(new Packet(0, 1, bytes, true, 80, 120, 40));

        Assert.Equal(80, Assert.Single(frames).Pts);
        Assert.Equal(120, Assert.Single(later).Pts);
        AssertSamples(writer.Expected(picture), later[0]);
        Assert.Empty(decoder.Decode(new Packet(0, 2, new byte[3], true, 120, 160, 40, Discard: true))); // not decoded at all
        Assert.Empty(decoder.Flush());
        Assert.Empty(await asInterface.FlushAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await asInterface.DecodeAsync(new Packet(0, 3, bytes, true, 0, 0, 40), new CancellationToken(true)));
    }

    [Fact]
    public void ReusesTheSamplesOfDisposedFrames()
    {
        // A frame's planes go back to the decoder when it is disposed, and the next frame (of other content) is
        // written over them whole: nothing of the old frame shows, the samples past a slice's data included.
        var random = new Random(11);
        var writer = new Rdd36FrameWriter(50, 26);
        var decoder = new Rdd36Decoder();
        for (int i = 0; i < 4; i++)
        {
            var picture = RandomPicture(writer, random);
            var frame = decoder.Decode(writer.Write(picture));
            AssertSamples(writer.Expected(picture), frame);
            frame.Dispose();
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(127, 127)]
    [InlineData(128, 128)]
    [InlineData(129, 132)]
    [InlineData(130, 136)]
    [InlineData(223, 508)]
    [InlineData(224, 512)]
    [InlineData(225, 512)]
    [InlineData(255, 512)]
    public void ScalesByTheQuantizationIndex(int index, int scale) =>
        Assert.Equal(scale, Rdd36Decoder.QuantizationScale(index)); // Table 15; 0 and above 224 as FFmpeg (measured)

    [Theory]
    [InlineData(45, 3, new[] { 8, 8, 8, 8, 8, 4, 1 })] // RDD 36's example (4, Figure 1)
    [InlineData(15, 3, new[] { 8, 4, 2, 1 })]
    [InlineData(15, 2, new[] { 4, 4, 4, 2, 1 })]
    [InlineData(5, 1, new[] { 2, 2, 1 })]
    [InlineData(3, 0, new[] { 1, 1, 1 })]
    [InlineData(1, 3, new[] { 1 })]
    [InlineData(8, 3, new[] { 8 })]
    public void LaysOutSlicesAsTheDocumentDoes(int widthInMacroblocks, int log2SliceSize, int[] expected)
    {
        var sizes = new int[16];

        int count = Rdd36Decoder.SliceSizes(widthInMacroblocks, log2SliceSize, sizes);

        Assert.Equal(expected, sizes[..count]);
        Assert.Equal(count, Rdd36Decoder.SliceSizes(widthInMacroblocks, log2SliceSize, [])); // counting alone
    }

    /// <summary>A frame of one macroblock whose luma data is <paramref name="luma"/>, its chroma blocks flat.</summary>
    private static byte[] OneMacroblock(byte[] luma)
    {
        byte[] chroma = Rdd36FrameWriter.ScannedCoefficients(new int[2 * 64], 2);
        byte[] slice = [6 << 3, 4, (byte)(luma.Length >> 8), (byte)luma.Length, (byte)(chroma.Length >> 8), (byte)chroma.Length, .. luma, .. chroma, .. chroma];
        byte[] frame = new Rdd36FrameWriter(16, 16).Write((_, _, _) => new int[64]);
        byte[] head = frame[..28];
        int pictureSize = 8 + 2 + slice.Length;
        byte[] picture = [8 << 3, 0, 0, (byte)(pictureSize >> 8), (byte)pictureSize, 0, 1, 3 << 4, (byte)(slice.Length >> 8), (byte)slice.Length, .. slice];
        byte[] whole = [.. head, .. picture];
        BinaryPrimitives.WriteUInt32BigEndian(whole, (uint)whole.Length);
        return whole;
    }

    private static byte[] Changed(byte[] data, int at, byte value)
    {
        byte[] changed = (byte[])data.Clone();
        changed[at] = value;
        return changed;
    }

    private static void AssertKeepsTheContract(byte[] data)
    {
        try
        {
            new Rdd36Decoder().Decode(data).Dispose();
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
        }
    }

    private static byte[] RandomMatrix(Random random)
    {
        // "Each entry of the matrix will be in the range 2, 3, ..., 63" (6.1.1).
        var matrix = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            matrix[i] = (byte)random.Next(2, 64);
        }

        return matrix;
    }

    /// <summary>
    /// Blocks as pictures give them: a DC anywhere in the range that keeps samples off the clamp, a few small AC
    /// coefficients, now and then a block with many or with none; every block of the padded picture, the ones past
    /// its edges too.
    /// </summary>
    private static Func<int, int, int, int[]> RandomPicture(Rdd36FrameWriter writer, Random random)
    {
        var blocks = new Dictionary<(int Component, int X, int Y), int[]>();
        for (int component = 0; component < 3; component++)
        {
            for (int y = 0; y < writer.HeightInMacroblocks * 2; y++)
            {
                for (int x = 0; x < writer.WidthInMacroblocks * (component == 0 ? 2 : 1); x++)
                {
                    var levels = new int[64];
                    int kind = random.Next(10);
                    levels[0] = random.Next(-40, 41);
                    int coefficients = kind == 0 ? 0 : kind == 1 ? 40 : random.Next(1, 6);
                    for (int i = 0; i < coefficients; i++)
                    {
                        levels[random.Next(1, 64)] = random.Next(-6, 7);
                    }

                    blocks[(component, x, y)] = levels;
                }
            }
        }

        return (component, x, y) => blocks[(component, x, y)];
    }

    private static void AssertSamples(ushort[][] expected, VideoFrame frame)
    {
        for (int plane = 0; plane < 3; plane++)
        {
            var (width, height) = frame.Format.PlaneSize(plane, frame.Width, frame.Height);
            Assert.Equal(expected[plane].Length, width * height);
            for (int y = 0; y < height; y++)
            {
                var row = MemoryMarshal.Cast<byte, ushort>(frame.GetRow(plane, y));
                Assert.True(
                    expected[plane].AsSpan(y * width, width).SequenceEqual(row[..width]),
                    $"Plane {plane}, row {y} differs: [{string.Join(' ', row[..width].ToArray())}] is not [{string.Join(' ', expected[plane].AsSpan(y * width, width).ToArray())}].");
            }
        }
    }
}
