using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Tests.Mjpeg;

public class MjpegDecoderTests
{
    // A 16x8 grayscale JPEG of two flat blocks, no DHT (so the Annex K tables apply) and
    // every quantizer 8. Each block is coded as 0x5A: DC category 1 ('010'), magnitude '1'
    // (+1, i.e. +8 after dequantization), then EOB ('1010'); with negative, as 0x4A (magnitude
    // '0', -1).
    private static byte[] TwoBlockGrayJpeg(bool restartMarkers, bool negative = false)
    {
        byte block = negative ? (byte)0x4A : (byte)0x5A;
        var data = new List<byte>
        {
            0xFF, 0xD8,                                         // SOI
            0xFF, 0xDB, 0x00, 0x43, 0x00,                       // DQT, table 0, 8-bit
        };
        data.AddRange(Enumerable.Repeat((byte)8, 64));
        data.AddRange(
        [
            0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x08, 0x00, 0x10, // SOF0: 8-bit, 8 high, 16 wide
            0x01, 0x01, 0x11, 0x00,                             //   1 component, 1x1, table 0
        ]);

        if (restartMarkers)
        {
            data.AddRange([0xFF, 0xDD, 0x00, 0x04, 0x00, 0x01]); // DRI: restart every MCU
        }

        data.AddRange([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]); // SOS
        data.Add(block);
        if (restartMarkers)
        {
            data.AddRange([0xFF, JpegMarker.Rst0]);
        }

        data.Add(block);
        data.AddRange([0xFF, 0xD9]); // EOI
        return [.. data];
    }

    [Fact]
    public void Decode_UsesStandardHuffmanTablesWhenDhtIsMissing()
    {
        var frame = new MjpegDecoder().Decode(TwoBlockGrayJpeg(restartMarkers: false));

        Assert.Equal((16, 8, PixelFormat.Gray8), (frame.Width, frame.Height, frame.Format));

        // DC 1024 + 8 is mean 129. The second block's DC is predicted from the first: 1040, mean 130.
        for (int y = 0; y < 8; y++)
        {
            Assert.All(frame.GetRow(0, y)[..8].ToArray(), p => Assert.Equal(129, p));
            Assert.All(frame.GetRow(0, y)[8..].ToArray(), p => Assert.Equal(130, p));
        }
    }

    [Fact]
    public void Decode_ResetsDcPredictionAtRestartMarkers()
    {
        var frame = new MjpegDecoder().Decode(TwoBlockGrayJpeg(restartMarkers: true));

        for (int y = 0; y < 8; y++)
        {
            Assert.All(frame.GetRow(0, y).ToArray(), p => Assert.Equal(129, p));
        }
    }

    [Fact]
    public void Decode_RejectsMissingQuantizationTable()
    {
        var data = TwoBlockGrayJpeg(restartMarkers: false);
        data[3] = 0xFE; // turn DQT into a COM segment, same length

        Assert.Throws<InvalidDataException>(() => new MjpegDecoder().Decode(data));
    }

    // Two 16x8 fields in one packet: the first with mean values 129 and 130, the second 127 and 126.
    private static byte[] TwoFieldPacket() => [.. TwoBlockGrayJpeg(restartMarkers: false), .. TwoBlockGrayJpeg(restartMarkers: false, negative: true)];

    private static VideoStreamInfo Stream(int height) => new(CodecIds.Mjpeg, 16, height, 25, 1, new Rational(1, 25), default, 0);

    [Fact]
    public void Decode_WithoutTheStreamHeight_DecodesTheFirstImageOfAPacket()
    {
        var frame = new MjpegDecoder().Decode(TwoFieldPacket());

        Assert.Equal((16, 8), (frame.Width, frame.Height));
        Assert.Equal(129, frame.GetRow(0, 0)[0]);
    }

    [Fact]
    public void Decode_WeavesTheFieldsOfAnInterlacedPacket_FirstFieldOnTheBottomRows()
    {
        // Images 8 high in a 16-high stream are fields (8 < 16 * 3 / 4).
        var frame = new MjpegDecoder(Stream(16)).Decode(TwoFieldPacket());

        Assert.Equal((16, 16, PixelFormat.Gray8), (frame.Width, frame.Height, frame.Format));
        for (int y = 0; y < 16; y++)
        {
            var (left, right) = y % 2 == 1 ? (129, 130) : (127, 126);
            Assert.All(frame.GetRow(0, y)[..8].ToArray(), p => Assert.Equal(left, p));
            Assert.All(frame.GetRow(0, y)[8..].ToArray(), p => Assert.Equal(right, p));
        }
    }

    [Fact]
    public void Decode_FirstFieldOnTop_PutsTheFirstFieldOnTheEvenRows()
    {
        var frame = new MjpegDecoder(Stream(16)) { FirstFieldOnTop = true }.Decode(TwoFieldPacket());

        Assert.Equal(129, frame.GetRow(0, 0)[0]);
        Assert.Equal(127, frame.GetRow(0, 1)[0]);
    }

    [Fact]
    public void Decode_ImagesOfAtLeastThreeQuartersOfTheStreamHeight_AreFrames()
    {
        // 8 is not less than 11 * 3 / 4 = 8 (integer division, as FFmpeg): a frame, the second image ignored.
        var frame = new MjpegDecoder(Stream(11)).Decode(TwoFieldPacket());

        Assert.Equal((16, 8), (frame.Width, frame.Height));
    }

    [Fact]
    public void Decode_AFieldWithoutItsPair_IsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => new MjpegDecoder(Stream(16)).Decode(TwoBlockGrayJpeg(restartMarkers: false)));
    }

    /// <summary>
    /// <see cref="MjpegDecoder.ZeroCopy"/> frames are the planes the image was decoded into, whole MCUs wide and high (an odd
    /// size shows it), and a disposed frame's planes come back for the next one.
    /// </summary>
    [Fact]
    public void ZeroCopy_DecodesIntoTheFramesPlanesAndReusesThem()
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf("testsrc-odd.avi")))
        {
            return;
        }

        using var demuxer = ManagedCodecs.Containers.Demuxer.Open(TestSamples.PathOf("testsrc-odd.avi"));
        var video = demuxer.Video!;
        var decoder = new MjpegDecoder(video) { ZeroCopy = true };
        var packets = demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex).Take(2).ToList();

        var first = decoder.Decode(packets[0].Data.Span);
        Assert.True(first.Planes[0].Length > first.Strides[0] * first.Height, $"Luma plane of {first.Planes[0].Length} bytes for {first.Width} x {first.Height}: not padded to whole MCUs.");
        var planes = first.Planes;
        first.Dispose();

        var second = decoder.Decode(packets[1].Data.Span);
        Assert.Same(planes[0], second.Planes[0]);
    }
}
