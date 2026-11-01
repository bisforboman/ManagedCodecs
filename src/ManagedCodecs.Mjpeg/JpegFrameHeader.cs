namespace ManagedCodecs.Mjpeg;

internal sealed record JpegComponent(byte Id, int HorizontalSampling, int VerticalSampling, int QuantizationTableId);

/// <summary>Parsed SOFn segment: image size and component layout.</summary>
internal sealed record JpegFrameHeader(byte Marker, int Precision, int Width, int Height, IReadOnlyList<JpegComponent> Components)
{
    public static JpegFrameHeader Parse(byte marker, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6)
        {
            throw new InvalidDataException("SOF segment too short.");
        }

        int precision = payload[0];
        int height = (payload[1] << 8) | payload[2];
        int width = (payload[3] << 8) | payload[4];
        int count = payload[5];

        if (payload.Length < 6 + (count * 3))
        {
            throw new InvalidDataException("SOF segment shorter than its component count.");
        }

        if (width == 0)
        {
            throw new InvalidDataException("SOF segment with a width of 0.");
        }

        if (height == 0)
        {
            throw new NotSupportedException("Height defined by a DNL marker is not supported.");
        }

        var components = new JpegComponent[count];
        for (int i = 0; i < count; i++)
        {
            var c = payload.Slice(6 + (i * 3), 3);
            components[i] = new JpegComponent(c[0], c[1] >> 4, c[1] & 0x0F, c[2]);
        }

        return new JpegFrameHeader(marker, precision, width, height, components);
    }

    /// <summary>Maps the sampling factors to an output layout, as FFmpeg's yuvj* formats do.</summary>
    public PixelFormat ToPixelFormat()
    {
        if (Precision != 8)
        {
            throw new NotSupportedException($"{Precision}-bit JPEG is not supported.");
        }

        if (Components.Count == 1)
        {
            return PixelFormat.Gray8;
        }

        // Only the luma/chroma ratio matters: FFmpeg itself writes 4:2:2 as 2x2, 1x2, 1x2.
        if (Components.Count == 3
            && Components[1].HorizontalSampling == Components[2].HorizontalSampling
            && Components[1].VerticalSampling == Components[2].VerticalSampling
            && Components[1].HorizontalSampling > 0
            && Components[1].VerticalSampling > 0
            && Components[0].HorizontalSampling % Components[1].HorizontalSampling == 0
            && Components[0].VerticalSampling % Components[1].VerticalSampling == 0)
        {
            return (Components[0].HorizontalSampling / Components[1].HorizontalSampling,
                    Components[0].VerticalSampling / Components[1].VerticalSampling) switch
            {
                (2, 2) => PixelFormat.Yuv420P,
                (2, 1) => PixelFormat.Yuv422P,
                (1, 1) => PixelFormat.Yuv444P,
                (4, 1) => PixelFormat.Yuv411P,
                (1, 2) => PixelFormat.Yuv440P,
                _ => throw Unsupported(),
            };
        }

        throw Unsupported();
    }

    private NotSupportedException Unsupported() =>
        new($"Unsupported component layout: {string.Join(", ", Components.Select(c => $"{c.HorizontalSampling}x{c.VerticalSampling}"))}.");
}
