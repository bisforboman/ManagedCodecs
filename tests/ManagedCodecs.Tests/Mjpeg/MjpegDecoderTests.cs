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

    // An 8x8 4:4:4 JPEG of three scans, one for each component (non-interleaved), each a flat block coded as in
    // TwoBlockGrayJpeg (mean 129); with scans: 1, the first component's alone (an image cut after it).
    private static byte[] ThreeScanJpeg(int scans)
    {
        var data = new List<byte> { 0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x43, 0x00 }; // SOI, DQT table 0, 8-bit
        data.AddRange(Enumerable.Repeat((byte)8, 64));
        data.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x08, 0x00, 0x08, 0x03]); // SOF0: 8x8, 3 components
        data.AddRange([0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00]);      //   ids 1, 2, 3: 1x1, table 0
        for (byte id = 1; id <= scans; id++)
        {
            data.AddRange([0xFF, 0xDA, 0x00, 0x08, 0x01, id, 0x00, 0x00, 0x3F, 0x00, 0x5A]); // SOS of one component, its block
        }

        data.AddRange([0xFF, 0xD9]);
        return [.. data];
    }

    /// <summary>
    /// A component no scan reached (a non-interleaved image cut before its scan) is 0 and the frame damaged, not the last
    /// frame's samples left in the decoder's buffers.
    /// </summary>
    [Fact]
    public void Decode_AComponentWithoutItsScanIsZero()
    {
        var decoder = new MjpegDecoder();
        var whole = decoder.Decode(ThreeScanJpeg(scans: 3));
        Assert.False(whole.IsDamaged);
        Assert.Equal([129, 129, 129], new[] { whole.Planes[0][0], whole.Planes[1][0], whole.Planes[2][0] }.Select(b => (int)b));
        whole.Dispose();

        var cut = decoder.Decode(ThreeScanJpeg(scans: 1));

        Assert.True(cut.IsDamaged);
        Assert.Equal(129, cut.Planes[0][0]);
        Assert.All(cut.Planes[1].Concat(cut.Planes[2]), b => Assert.Equal(0, b));
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
    /// An interlaced packet whose first field is cut short (its scan running to the packet's end, so no second field
    /// follows) is damaged data, as FFmpeg's decoder gives no frame for it, not a layout this library leaves out.
    /// </summary>
    [Fact]
    public void Decode_AnInterlacedPacketWithItsFirstFieldCut_IsInvalidData()
    {
        var field = TwoBlockGrayJpeg(restartMarkers: false);
        byte[] cut = field[..^3]; // the scan's last byte and the EOI marker gone

        Assert.Throws<InvalidDataException>(() => new MjpegDecoder(Stream(16)).Decode(cut));
    }

    /// <summary>
    /// FFmpeg's decoder refuses a packet of less than a quarter byte for each 8x8 block of the image's size (rounded up to
    /// whole blocks), what comes before the SOI marker counted too: measured at four sizes and layouts (src/ManagedCodecs.Mjpeg/CLAUDE.md).
    /// The tiny image's SOF says 160x160 here, 400 blocks, so 100 bytes are the least; its scan runs out after its two
    /// blocks, the rest left at 0.
    /// </summary>
    [Fact]
    public void Decode_RefusesAPacketTooSmallForItsImage()
    {
        var jpeg = TwoBlockGrayJpeg(restartMarkers: false);
        int sof = jpeg.AsSpan().IndexOf([(byte)0xFF, JpegMarker.Sof0]);
        jpeg[sof + 5] = 0;
        jpeg[sof + 6] = 160; // height
        jpeg[sof + 7] = 0;
        jpeg[sof + 8] = 160; // width
        Assert.True(jpeg.Length < 100);

        byte[] Padded(int length) => [.. new byte[length - jpeg.Length], .. jpeg];

        Assert.Throws<InvalidDataException>(() => new MjpegDecoder().Decode(Padded(99)));
        var frame = new MjpegDecoder().Decode(Padded(100));
        Assert.Equal((160, 160), (frame.Width, frame.Height));
        Assert.True(frame.IsDamaged);
        Assert.Equal(0, frame.Planes[0][(80 * frame.Strides[0]) + 80]);
    }

    /// <summary>
    /// A scan damaged mid-way with a code that is not valid (32 one bits, as stuffed 0xFF bytes: T.81 gives no code of all
    /// ones): the frame comes out (as FFmpeg's decoder gives it, partly decoded), damaged, the part before the damage as
    /// the undamaged frame's. Not compared with FFmpeg past the damage: it decodes on through some codes that are not
    /// valid, as its own tables read them (so make-samples.sh has no sample of it).
    /// </summary>
    [Fact]
    public void Decode_ADamagedScanGivesADamagedFrame()
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf("testsrc-420.avi")))
        {
            return;
        }

        using var demuxer = ManagedCodecs.Containers.Demuxer.Open(TestSamples.PathOf("testsrc-420.avi"));
        var packet = demuxer.ReadPackets().First(p => p.StreamIndex == demuxer.Video!.StreamIndex).Data.ToArray();
        var clean = new MjpegDecoder().Decode(packet);
        Assert.False(clean.IsDamaged);

        // Half-way through the scan (not inside a stuffed pair), four stuffed 0xFF bytes in place of four bytes.
        int sos = packet.AsSpan().IndexOf([(byte)0xFF, JpegMarker.Sos]);
        int scan = sos + 2 + ((packet[sos + 2] << 8) | packet[sos + 3]);
        int at = scan + ((packet.Length - scan) / 2);
        while (packet[at - 1] == 0xFF || packet.AsSpan(at, 4).Contains((byte)0xFF))
        {
            at++;
        }

        byte[] ones = [0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00];
        var damaged = new MjpegDecoder().Decode([.. packet.AsSpan(0, at), .. ones, .. packet.AsSpan(at + 4)]);

        Assert.True(damaged.IsDamaged);
        int rows = 16; // the first macroblock row is decoded before the damage half-way through
        Assert.True(damaged.Planes[0].AsSpan(0, rows * damaged.Strides[0]).SequenceEqual(clean.Planes[0].AsSpan(0, rows * clean.Strides[0])));
    }

    /// <summary>
    /// The frames of the cut samples (src/ManagedCodecs.Mjpeg/CLAUDE.md): the cut one damaged, those around it not, and
    /// frames that decode whole (bytes before SOI, no EOI) not damaged either. MjpegConformanceTests compares their
    /// samples with FFmpeg's.
    /// </summary>
    [Theory]
    [InlineData("mjpeg-cut.avi", "010")]
    [InlineData("mjpeg-cut-eoi.avi", "010")]
    [InlineData("mjpeg-cut-stuffed.avi", "010")]
    [InlineData("mjpeg-cut-ff-eoi.avi", "010")]
    [InlineData("mjpeg-cut-stuffed-eoi.avi", "010")]
    [InlineData("mjpeg-junk.avi", "0000")]
    public void Decode_MarksTheCutFramesDamaged(string sample, string damaged)
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf(sample)))
        {
            return;
        }

        using var demuxer = ManagedCodecs.Containers.Demuxer.Open(TestSamples.PathOf(sample));
        var decoder = new MjpegDecoder(demuxer.Video!);
        var flags = string.Concat(demuxer.ReadPackets().Select(p => decoder.Decode(p.Data.Span).IsDamaged ? '1' : '0'));

        Assert.Equal(damaged, flags);
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
