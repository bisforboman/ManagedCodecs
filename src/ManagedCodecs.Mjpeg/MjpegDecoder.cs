namespace ManagedCodecs.Mjpeg;

/// <summary>Baseline (sequential, Huffman) JPEG decoder for MJPEG frames.</summary>
/// <remarks>
/// Implement from the JPEG spec (ITU-T T.81) rather than by copying FFmpeg's mjpegdec.c,
/// so this package is free to use any license. Remaining steps:
/// <list type="number">
/// <item>Parse DQT into 8x8 quantization tables (zig-zag order, 8- and 16-bit entries).</item>
/// <item>Parse DHT into Huffman lookup tables. Many MJPEG streams omit DHT, so fall back to
/// the standard tables from T.81 Annex K.3.</item>
/// <item>Parse DRI (restart interval) and SOS (component order, table selectors).</item>
/// <item>Bit reader over the entropy data: remove 0xFF00 stuffing, reset DC predictors at RSTn.</item>
/// <item>Decode DC/AC coefficients per block, MCU by MCU, following the sampling factors.</item>
/// <item>Dequantize, inverse DCT (start with a scalar float IDCT, then match FFmpeg's integer
/// IDCT exactly so output is bit-exact), level shift +128, clamp, write into the planes.</item>
/// <item>Crop edge MCUs to the real image size.</item>
/// </list>
/// </remarks>
public sealed class MjpegDecoder : IVideoDecoder
{
    public VideoFrame Decode(ReadOnlySpan<byte> packet)
    {
        var segments = JpegMarkerReader.ReadSegments(packet);
        JpegFrameHeader? header = null;

        foreach (var segment in segments)
        {
            if (segment.Marker is JpegMarker.Sof0 or JpegMarker.Sof1)
            {
                header = JpegFrameHeader.Parse(segment.Marker, packet.Slice(segment.PayloadOffset, segment.PayloadLength));
            }
            else if (JpegMarker.IsUnsupportedFrame(segment.Marker))
            {
                throw new NotSupportedException($"JPEG frame type 0xFF{segment.Marker:X2} is not supported (only baseline/extended Huffman).");
            }
        }

        if (header is null)
        {
            throw new InvalidDataException("No SOF segment found.");
        }

        // Validate early so unsupported layouts fail with a clear message.
        _ = header.ToPixelFormat();

        throw new NotImplementedException("Entropy decoding and IDCT are not implemented yet. See the remarks on MjpegDecoder.");
    }
}
