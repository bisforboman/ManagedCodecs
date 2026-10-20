using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Tests.Mjpeg;

public class JpegMarkerReaderTests
{
    // A hand-made 32x16 grayscale "JPEG": just enough structure for the marker reader.
    private static byte[] TinyGrayJpeg(byte sofMarker = JpegMarker.Sof0) =>
    [
        0xFF, 0xD8,                                     // SOI
        0xFF, 0xE0, 0x00, 0x04, 0xAA, 0xBB,             // APP0, 2-byte payload
        0xFF, sofMarker, 0x00, 0x0B,                    // SOFn, length 11
        0x08, 0x00, 0x10, 0x00, 0x20, 0x01,             //   8-bit, height 16, width 32, 1 component
        0x01, 0x11, 0x00,                               //   id 1, sampling 1x1, quant table 0
        0xFF, 0xDA, 0x00, 0x08,                         // SOS, length 8
        0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,             //   1 component, Ss 0, Se 63, Ah/Al 0
        0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56,       // entropy data with stuffing and RST0
        0xFF, 0xD9,                                     // EOI
    ];

    [Fact]
    public void ReadSegments_FindsMarkersAndScanData()
    {
        var segments = JpegMarkerReader.ReadSegments(TinyGrayJpeg());

        Assert.Equal(
            new byte[] { JpegMarker.Soi, 0xE0, JpegMarker.Sof0, JpegMarker.Sos, JpegMarker.Eoi },
            segments.Select(s => s.Marker).ToArray());

        Assert.Equal(new JpegSegment(0xE0, 6, 2), segments[1]);
        Assert.Equal(new JpegSegment(JpegMarker.Sof0, 12, 9), segments[2]);

        var sos = segments[3];
        Assert.Equal(25, sos.PayloadOffset);
        Assert.Equal(6, sos.PayloadLength);
        Assert.Equal(31, sos.EntropyDataOffset);
        Assert.Equal(7, sos.EntropyDataLength); // byte stuffing and RST0 stay inside the scan
    }

    [Fact]
    public void ReadSegments_SkipsFillBytesBeforeMarker()
    {
        byte[] data = [0xFF, 0xD8, 0xFF, 0xFF, 0xFF, 0xD9];

        var segments = JpegMarkerReader.ReadSegments(data);

        Assert.Equal(JpegMarker.Eoi, segments[^1].Marker);
    }

    [Fact]
    public void ReadSegments_RejectsMissingSoi()
    {
        Assert.Throws<InvalidDataException>(() => JpegMarkerReader.ReadSegments(new byte[] { 0x00, 0x01 }));
    }

    [Fact]
    public void ReadSegments_RejectsMissingEoi()
    {
        var data = TinyGrayJpeg()[..^2];

        Assert.Throws<InvalidDataException>(() => JpegMarkerReader.ReadSegments(data));
    }

    [Fact]
    public void FrameHeader_ParsesSizeAndLayout()
    {
        var data = TinyGrayJpeg();
        var sof = JpegMarkerReader.ReadSegments(data)[2];

        var header = JpegFrameHeader.Parse(sof.Marker, data.AsSpan(sof.PayloadOffset, sof.PayloadLength));

        Assert.Equal(32, header.Width);
        Assert.Equal(16, header.Height);
        Assert.Equal(PixelFormat.Gray8, header.ToPixelFormat());
    }

    [Fact]
    public void Decoder_RejectsProgressiveJpeg()
    {
        var decoder = new MjpegDecoder();

        Assert.Throws<NotSupportedException>(() => decoder.Decode(TinyGrayJpeg(JpegMarker.Sof2)));
    }
}
