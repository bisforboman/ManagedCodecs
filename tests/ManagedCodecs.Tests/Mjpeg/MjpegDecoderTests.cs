using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Tests.Mjpeg;

public class MjpegDecoderTests
{
    // A 16x8 grayscale JPEG of two flat blocks, no DHT (so the Annex K tables apply) and
    // every quantizer 8. Each block is coded as 0x5A: DC category 1 ('010'), magnitude '1'
    // (+1, i.e. +8 after dequantization), then EOB ('1010').
    private static byte[] TwoBlockGrayJpeg(bool restartMarkers)
    {
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
        data.Add(0x5A);
        if (restartMarkers)
        {
            data.AddRange([0xFF, JpegMarker.Rst0]);
        }

        data.Add(0x5A);
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
}
