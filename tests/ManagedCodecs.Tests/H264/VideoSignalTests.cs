using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// The VUI's colour properties, chroma location and sample aspect ratio (E.1.1, E.2.1) where no encoder at hand writes
/// them: reserved and undefined code points, an Extended_SAR of zero, and FFmpeg's checks on what it reads. The samples
/// (h264-color-*, h264-sar-extended, name.color) check the rest against ffprobe frame by frame.
/// </summary>
public class VideoSignalTests
{
    /// <summary>An SPS without a VUI says nothing, not even the chroma location (JM's streams, ffprobe's "unspecified").</summary>
    [Fact]
    public void WithoutVui_NothingIsSaid()
    {
        Assert.Equal(VideoSignal.None, Parse(vui: null).Signal);
    }

    /// <summary>A VUI that says nothing of the chroma location means left (FFmpeg's default for any VUI), of the rest nothing.</summary>
    [Fact]
    public void EmptyVui_ChromaLeft()
    {
        var signal = Parse(new Vui()).Signal;
        Assert.Equal(VideoSignal.None with { Location = ChromaLocation.Left }, signal);
    }

    [Fact]
    public void EverythingSaid_IsRead()
    {
        var signal = Parse(new Vui(AspectIdc: 255, SarWidth: 64, SarHeight: 45, FullRange: true, Primaries: 9, Transfer: 16, Matrix: 9, ChromaLocType: 2)).Signal;
        Assert.Equal(
            new VideoSignal(ColorRange.Full, ColorPrimaries.Bt2020, ColorTransfer.Smpte2084, ColorMatrix.Bt2020Ncl, ChromaLocation.TopLeft, new(64, 45), RangeSent: true, ColourSent: true),
            signal);
    }

    /// <summary>A video signal type without a colour description gives the range alone.</summary>
    [Fact]
    public void RangeWithoutColourDescription()
    {
        var signal = Parse(new Vui(FullRange: false, Primaries: null)).Signal;
        Assert.Equal(VideoSignal.None with { Range = ColorRange.Limited, Location = ChromaLocation.Left, RangeSent = true }, signal);
    }

    /// <summary>Table E-1 by aspect_ratio_idc; 0, the reserved 17 to 254, and an Extended_SAR with a zero unspecified; an Extended_SAR in lowest terms.</summary>
    [Theory]
    [InlineData(0, 0, 0, 0, 1)]
    [InlineData(1, 0, 0, 1, 1)]
    [InlineData(4, 0, 0, 16, 11)]
    [InlineData(13, 0, 0, 160, 99)]
    [InlineData(16, 0, 0, 2, 1)]
    [InlineData(17, 0, 0, 0, 1)]
    [InlineData(254, 0, 0, 0, 1)]
    [InlineData(255, 64, 45, 64, 45)]
    [InlineData(255, 128, 90, 64, 45)]
    [InlineData(255, 0, 45, 0, 1)]
    [InlineData(255, 64, 0, 0, 1)]
    public void AspectRatio_ByTableE1(int idc, int sarWidth, int sarHeight, int numerator, int denominator)
    {
        Assert.Equal(new Rational(numerator, denominator), Parse(new Vui(AspectIdc: idc, SarWidth: sarWidth, SarHeight: sarHeight)).Signal.AspectRatio);
    }

    /// <summary>
    /// FFmpeg's <c>av_image_check_sar</c>: a ratio that makes the shown width (or, above 1, height) round down to nothing
    /// is unknown; any other stays as it is.
    /// </summary>
    [Theory]
    [InlineData(1, 320, 320, 240, 1, 320)]
    [InlineData(1, 321, 320, 240, 0, 1)]
    [InlineData(240, 1, 320, 240, 240, 1)]
    [InlineData(241, 1, 320, 240, 0, 1)]
    [InlineData(65535, 65535, 16, 16, 65535, 65535)]
    [InlineData(0, 5, 320, 240, 0, 1)]
    public void AspectRatio_ThatShowsNothing_IsUnknown(int numerator, int denominator, int width, int height, int expectedNumerator, int expectedDenominator)
    {
        Assert.Equal(new Rational(expectedNumerator, expectedDenominator), VideoSignal.AspectRatioFor(new(numerator, denominator), width, height));
    }

    /// <summary>
    /// Code points as sent where H.273 defines them (FFmpeg names them, the reserved 0 and 3 too), else unspecified;
    /// the chroma location's types 0 to 5 plus one, beyond that unspecified.
    /// </summary>
    [Fact]
    public void UndefinedCodePoints_AreUnspecified()
    {
        Assert.Equal(ColorPrimaries.Reserved0, VideoSignal.PrimariesOf(0));
        Assert.Equal(ColorPrimaries.Reserved, VideoSignal.PrimariesOf(3));
        Assert.Equal(ColorPrimaries.Smpte432, VideoSignal.PrimariesOf(12));
        Assert.Equal(ColorPrimaries.Unspecified, VideoSignal.PrimariesOf(13));
        Assert.Equal(ColorPrimaries.Unspecified, VideoSignal.PrimariesOf(21));
        Assert.Equal(ColorPrimaries.Ebu3213, VideoSignal.PrimariesOf(22));
        Assert.Equal(ColorPrimaries.Unspecified, VideoSignal.PrimariesOf(255));
        Assert.Equal(ColorTransfer.AribStdB67, VideoSignal.TransferOf(18));
        Assert.Equal(ColorTransfer.Unspecified, VideoSignal.TransferOf(19));
        Assert.Equal(ColorMatrix.Identity, VideoSignal.MatrixOf(0));
        Assert.Equal(ColorMatrix.ICtCp, VideoSignal.MatrixOf(14));
        Assert.Equal(ColorMatrix.Unspecified, VideoSignal.MatrixOf(15));
        Assert.Equal(ChromaLocation.Left, VideoSignal.LocationOf(0));
        Assert.Equal(ChromaLocation.Bottom, VideoSignal.LocationOf(5));
        Assert.Equal(ChromaLocation.Unspecified, VideoSignal.LocationOf(6));
        Assert.Equal(ChromaLocation.Unspecified, VideoSignal.LocationOf(uint.MaxValue));
    }

    /// <summary>Through the parser: undefined code points and a chroma location type beyond 5 in a VUI.</summary>
    [Fact]
    public void UndefinedCodePoints_InTheVui()
    {
        var signal = Parse(new Vui(FullRange: true, Primaries: 200, Transfer: 100, Matrix: 15, ChromaLocType: 6)).Signal;
        Assert.Equal(VideoSignal.None with { Range = ColorRange.Full, RangeSent = true, ColourSent = true }, signal);
    }

    /// <summary>The frame takes the signal, the aspect ratio checked against its size.</summary>
    [Fact]
    public void Apply_SetsTheFrame()
    {
        var frame = new VideoFrame(320, 240, PixelFormat.Yuv420P);
        Assert.Equal((ColorRange.Unspecified, ColorPrimaries.Unspecified, ColorTransfer.Unspecified, ColorMatrix.Unspecified, ChromaLocation.Unspecified, new Rational(0, 1)),
            (frame.ColorRange, frame.ColorPrimaries, frame.ColorTransfer, frame.ColorMatrix, frame.ChromaLocation, frame.SampleAspectRatio));
        new VideoSignal(ColorRange.Limited, ColorPrimaries.Bt709, ColorTransfer.Bt709, ColorMatrix.Bt709, ChromaLocation.Left, new(1, 321)).Apply(frame);
        Assert.Equal((ColorRange.Limited, ColorPrimaries.Bt709, ColorTransfer.Bt709, ColorMatrix.Bt709, ChromaLocation.Left, new Rational(0, 1)),
            (frame.ColorRange, frame.ColorPrimaries, frame.ColorTransfer, frame.ColorMatrix, frame.ChromaLocation, frame.SampleAspectRatio));
    }

    /// <summary>The VUI fields this test sets; null leaves a part out (aspect ratio, video signal type, colour description, chroma location).</summary>
    private sealed record Vui(int? AspectIdc = null, int SarWidth = 0, int SarHeight = 0, bool? FullRange = null, int? Primaries = 1, int Transfer = 1, int Matrix = 1, int? ChromaLocType = null);

    /// <summary>A Constrained Baseline SPS of 320x240 with the VUI given (or none), parsed.</summary>
    private static SequenceParameterSet Parse(Vui? vui)
    {
        var w = new BitWriter();
        w.Bits(66, 8); // profile_idc
        w.Bits(0xC0, 8); // constraint flags
        w.Bits(30, 8); // level_idc
        w.Ue(0); // seq_parameter_set_id
        w.Ue(0); // log2_max_frame_num_minus4
        w.Ue(2); // pic_order_cnt_type
        w.Ue(1); // max_num_ref_frames
        w.Bit(0); // gaps_in_frame_num_value_allowed_flag
        w.Ue(19); // pic_width_in_mbs_minus1
        w.Ue(14); // pic_height_in_map_units_minus1
        w.Bit(1); // frame_mbs_only_flag
        w.Bit(1); // direct_8x8_inference_flag
        w.Bit(0); // frame_cropping_flag
        w.Bit(vui is null ? 0 : 1); // vui_parameters_present_flag
        if (vui is not null)
        {
            w.Bit(vui.AspectIdc is null ? 0 : 1);
            if (vui.AspectIdc is { } idc)
            {
                w.Bits(idc, 8);
                if (idc == 255)
                {
                    w.Bits(vui.SarWidth, 16);
                    w.Bits(vui.SarHeight, 16);
                }
            }

            w.Bit(0); // overscan_info_present_flag
            w.Bit(vui.FullRange is null ? 0 : 1); // video_signal_type_present_flag
            if (vui.FullRange is { } full)
            {
                w.Bits(5, 3); // video_format: unspecified
                w.Bit(full ? 1 : 0);
                w.Bit(vui.Primaries is null ? 0 : 1); // colour_description_present_flag
                if (vui.Primaries is { } primaries)
                {
                    w.Bits(primaries, 8);
                    w.Bits(vui.Transfer, 8);
                    w.Bits(vui.Matrix, 8);
                }
            }

            w.Bit(vui.ChromaLocType is null ? 0 : 1); // chroma_loc_info_present_flag
            if (vui.ChromaLocType is { } type)
            {
                w.Ue((uint)type);
                w.Ue((uint)type);
            }

            w.Bit(0); // timing_info_present_flag
            w.Bit(0); // nal_hrd_parameters_present_flag
            w.Bit(0); // vcl_hrd_parameters_present_flag
            w.Bit(0); // pic_struct_present_flag
            w.Bit(0); // bitstream_restriction_flag
        }

        return SequenceParameterSet.Parse(NalUnit.Parse(w.ToNal(3, (int)NalUnitType.Sps)).CreateReader());
    }
}
