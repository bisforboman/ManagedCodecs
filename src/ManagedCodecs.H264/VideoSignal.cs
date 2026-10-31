namespace ManagedCodecs.H264;

/// <summary>
/// What the SPS's VUI says of the samples' meaning (E.2.1): video_full_range_flag, the colour description, the chroma
/// sample location and the sample aspect ratio, as FFmpeg reads them into a decoded frame's <c>color_range</c>,
/// <c>color_primaries</c>, <c>color_transfer</c>, <c>color_space</c>, <c>chroma_location</c> and
/// <c>sample_aspect_ratio</c>. <see cref="RangeSent"/> and <see cref="ColourSent"/>: whether the VUI had a video signal
/// type and a colour description, without which a frame takes the container's values.
/// </summary>
internal readonly record struct VideoSignal(
    ColorRange Range,
    ColorPrimaries Primaries,
    ColorTransfer Transfer,
    ColorMatrix Matrix,
    ChromaLocation Location,
    Rational AspectRatio,
    bool RangeSent = false,
    bool ColourSent = false)
{
    /// <summary>An SPS without a VUI: nothing said, not even the chroma location.</summary>
    public static readonly VideoSignal None = new(
        ColorRange.Unspecified, ColorPrimaries.Unspecified, ColorTransfer.Unspecified, ColorMatrix.Unspecified, ChromaLocation.Unspecified, new(0, 1));

    /// <summary>Table E-1's sample aspect ratios by aspect_ratio_idc, 0 (unspecified) to 16; 255 sends its own.</summary>
    private static readonly Rational[] AspectRatios =
    [
        new(0, 1), new(1, 1), new(12, 11), new(10, 11), new(16, 11), new(40, 33), new(24, 11), new(20, 11), new(32, 11),
        new(80, 33), new(18, 11), new(15, 11), new(64, 33), new(160, 99), new(4, 3), new(3, 2), new(2, 1),
    ];

    /// <summary>
    /// aspect_ratio_idc's ratio, or sar_width:sar_height for Extended_SAR (255) in lowest terms (as ffprobe prints it):
    /// unspecified (0:1) for idc 0, for the reserved 17 to 254 and for a width or height of 0 (E.2.1).
    /// </summary>
    public static Rational AspectRatioOf(int idc, int sarWidth, int sarHeight)
    {
        if (idc == 255)
        {
            return ColorCodes.AspectRatio(sarWidth, sarHeight);
        }

        return idc < 17 ? AspectRatios[idc] : new(0, 1);
    }

    /// <summary>colour_primaries as FFmpeg reads it (<see cref="ColorCodes.PrimariesOf"/>).</summary>
    public static ColorPrimaries PrimariesOf(int value) => ColorCodes.PrimariesOf(value);

    /// <summary>transfer_characteristics as FFmpeg reads it (<see cref="ColorCodes.TransferOf"/>).</summary>
    public static ColorTransfer TransferOf(int value) => ColorCodes.TransferOf(value);

    /// <summary>matrix_coefficients as FFmpeg reads it (<see cref="ColorCodes.MatrixOf"/>).</summary>
    public static ColorMatrix MatrixOf(int value) => ColorCodes.MatrixOf(value);

    /// <summary>chroma_sample_loc_type_top_field plus one (0 to 5, Figure E-1), or unspecified beyond, as FFmpeg reads it.</summary>
    public static ChromaLocation LocationOf(uint topFieldType) => topFieldType <= 5 ? (ChromaLocation)(topFieldType + 1) : ChromaLocation.Unspecified;

    /// <summary>
    /// The aspect ratio a frame of this size takes, as FFmpeg's <c>ff_set_sar</c> checks it: 0:1 where the ratio would
    /// make the shown width (or, above 1, height) round to nothing (<c>av_image_check_sar</c>), as an extreme
    /// Extended_SAR can.
    /// </summary>
    public static Rational AspectRatioFor(Rational ratio, int width, int height)
    {
        if (ratio.Numerator == 0 || ratio.Numerator == ratio.Denominator)
        {
            return ratio.Numerator == 0 ? new(0, 1) : ratio;
        }

        long shown = ratio.Numerator < ratio.Denominator
            ? (long)width * ratio.Numerator / ratio.Denominator
            : (long)height * ratio.Denominator / ratio.Numerator;
        return shown > 0 ? ratio : new(0, 1);
    }

    /// <summary>
    /// This SPS's signal as a frame gets it after <paramref name="previous"/>, the decoder's until then: FFmpeg's
    /// <c>h264_init_ps</c> writes the range into its codec context only under video_signal_type_present_flag and the
    /// colour description only under colour_description_present_flag, so an SPS without them keeps what an earlier one
    /// sent (with its <see cref="RangeSent"/> and <see cref="ColourSent"/>, so the container's values stand in only
    /// while no SPS has sent any); the chroma location and aspect ratio are always the SPS's own.
    /// </summary>
    public VideoSignal After(VideoSignal previous)
    {
        var signal = RangeSent ? this : this with { Range = previous.Range, RangeSent = previous.RangeSent };
        if (ColourSent)
        {
            return signal;
        }

        return signal with { Primaries = previous.Primaries, Transfer = previous.Transfer, Matrix = previous.Matrix, ColourSent = previous.ColourSent };
    }

    /// <summary>
    /// The frame's colour properties and sample aspect ratio from this signal, as FFmpeg gives them: where the VUI sent
    /// no range (or no colour description), the container's (<paramref name="stream"/>, FFmpeg's decoder starting from
    /// the container's values); the container's aspect ratio where it has one (ffprobe's
    /// <c>av_guess_sample_aspect_ratio</c>), else the VUI's, checked against the frame.
    /// </summary>
    public void Apply(VideoFrame frame, VideoStreamInfo? stream = null)
    {
        frame.ColorRange = RangeSent || stream is null ? Range : stream.ColorRange;
        (frame.ColorPrimaries, frame.ColorTransfer, frame.ColorMatrix) = ColourSent || stream is null
            ? (Primaries, Transfer, Matrix)
            : (stream.ColorPrimaries, stream.ColorTransfer, stream.ColorMatrix);
        frame.ChromaLocation = Location;
        frame.SampleAspectRatio = stream is { SampleAspectRatio.Numerator: > 0 } ? stream.SampleAspectRatio : AspectRatioFor(AspectRatio, frame.Width, frame.Height);
    }
}
