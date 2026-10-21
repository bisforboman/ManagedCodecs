using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// Gaps in frame_num (8.2.5.2), on hand-built 16x16 streams: an I_PCM IDR picture with flat samples,
/// then P pictures after missing frame_num values. The frames inferred for the gap take places in
/// the default reference list and in the sliding window, so a P macroblock past the gap must name
/// the IDR picture by an index that counts them; every output frame is then the flat IDR picture.
/// No encoder writes such references (OpenH264's temporal-layer sub-streams, which do have gaps,
/// name every reference explicitly), so these streams are built here.
/// </summary>
public class H264FrameNumGapTests
{
    private const byte Luma = 90;
    private const byte Cb = 60;
    private const byte Cr = 170;

    /// <summary>frame_num 4 after 0: frames 1 to 3 are inferred, so reference index 3 is the IDR picture.</summary>
    [Fact]
    public void ReferenceIndex_CountsTheInferredFrames()
    {
        var frames = Decode(maxNumRefFrames: 4, [Idr(), PFrame(frameNum: 4, refIdx: 3)]);
        Assert.Equal(2, frames.Count);
        Assert.All(frames, AssertFlat);
    }

    /// <summary>Reference index 0 names the inferred frame_num 3, which has no samples: an error, not a guess.</summary>
    [Fact]
    public void InferredFrame_CannotBePredictedFrom()
    {
        var error = Assert.Throws<InvalidDataException>(() => Decode(maxNumRefFrames: 4, [Idr(), PFrame(frameNum: 4, refIdx: 0)]));
        Assert.Contains("gap in frame_num", error.Message);
    }

    /// <summary>
    /// Skipped P pictures up to frame_num 12, an I_PCM P picture with other samples at 13, then
    /// frame_num 1 (MaxFrameNum is 16): 14, 15 and 0 are inferred across the wraparound, the sliding
    /// window (4 frames) keeps 13 and those three, and reference index 3 is frame_num 13. Without
    /// the inferred frames it would be frame_num 10, a copy of the IDR picture.
    /// </summary>
    [Fact]
    public void GapAcrossTheFrameNumWraparound()
    {
        List<byte[]> pictures = [Idr()];
        for (int frameNum = 1; frameNum <= 12; frameNum++)
        {
            pictures.Add(PSkip(frameNum));
        }

        pictures.Add(PPcm(frameNum: 13));
        pictures.Add(PFrame(frameNum: 1, refIdx: 3));
        var frames = Decode(maxNumRefFrames: 4, pictures);
        Assert.Equal(15, frames.Count);
        Assert.All(frames.Take(13), frame => AssertFlat(frame, Luma, Cb, Cr));
        Assert.All(frames.Skip(13), frame => AssertFlat(frame, OtherLuma, OtherCb, OtherCr));
    }

    private const byte OtherLuma = 30;
    private const byte OtherCb = 140;
    private const byte OtherCr = 80;

    private static void AssertFlat(VideoFrame frame) => AssertFlat(frame, Luma, Cb, Cr);

    private static void AssertFlat(VideoFrame frame, byte luma, byte cb, byte cr)
    {
        Assert.All(frame.Planes[0], sample => Assert.Equal(luma, sample));
        Assert.All(frame.Planes[1], sample => Assert.Equal(cb, sample));
        Assert.All(frame.Planes[2], sample => Assert.Equal(cr, sample));
    }

    private static List<VideoFrame> Decode(int maxNumRefFrames, List<byte[]> pictures)
    {
        var sps = Sps(maxNumRefFrames).ToNal(3, 7);
        var pps = Pps().ToNal(3, 8);
        var decoder = new H264Decoder([1, 66, 0, 30, 0xFF, 0xE1, 0, (byte)sps.Length, .. sps, 1, 0, (byte)pps.Length, .. pps]);
        var frames = new List<VideoFrame>();
        for (int i = 0; i < pictures.Count; i++)
        {
            var nal = pictures[i];
            var packet = new byte[4 + nal.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(packet, nal.Length);
            nal.CopyTo(packet, 4);
            frames.AddRange(decoder.Decode(new Packet(0, i, packet, i == 0, i, i, 1)));
        }

        frames.AddRange(decoder.Flush());
        return frames;
    }

    private static BitWriter Sps(int maxNumRefFrames)
    {
        var w = new BitWriter();
        w.Bits(66, 8); // profile_idc: Baseline
        w.Bits(0, 8);  // constraint flags
        w.Bits(30, 8); // level_idc
        w.Ue(0);       // seq_parameter_set_id
        w.Ue(0);       // log2_max_frame_num_minus4: MaxFrameNum 16
        w.Ue(2);       // pic_order_cnt_type
        w.Ue((uint)maxNumRefFrames);
        w.Bit(1);      // gaps_in_frame_num_value_allowed_flag
        w.Ue(0);       // pic_width_in_mbs_minus1: one macroblock
        w.Ue(0);       // pic_height_in_map_units_minus1
        w.Bit(1);      // frame_mbs_only_flag
        w.Bit(1);      // direct_8x8_inference_flag
        w.Bit(0);      // frame_cropping_flag
        w.Bit(0);      // vui_parameters_present_flag
        w.Trailing();
        return w;
    }

    private static BitWriter Pps()
    {
        var w = new BitWriter();
        w.Ue(0);      // pic_parameter_set_id
        w.Ue(0);      // seq_parameter_set_id
        w.Bit(0);     // entropy_coding_mode_flag: CAVLC
        w.Bit(0);     // bottom_field_pic_order_in_frame_present_flag
        w.Ue(0);      // num_slice_groups_minus1
        w.Ue(3);      // num_ref_idx_l0_default_active_minus1: 4 references
        w.Ue(0);      // num_ref_idx_l1_default_active_minus1
        w.Bit(0);     // weighted_pred_flag
        w.Bits(0, 2); // weighted_bipred_idc
        w.Se(0);      // pic_init_qp_minus26
        w.Se(0);      // pic_init_qs_minus26
        w.Se(0);      // chroma_qp_index_offset
        w.Bit(1);     // deblocking_filter_control_present_flag
        w.Bit(0);     // constrained_intra_pred_flag
        w.Bit(0);     // redundant_pic_cnt_present_flag
        w.Trailing();
        return w;
    }

    /// <summary>The IDR picture: frame_num 0, one I_PCM macroblock with flat samples.</summary>
    private static byte[] Idr()
    {
        var w = new BitWriter();
        w.Ue(0);      // first_mb_in_slice
        w.Ue(7);      // slice_type: I
        w.Ue(0);      // pic_parameter_set_id
        w.Bits(0, 4); // frame_num
        w.Ue(0);      // idr_pic_id
        w.Bit(0);     // no_output_of_prior_pics_flag
        w.Bit(0);     // long_term_reference_flag
        w.Se(0);      // slice_qp_delta
        w.Ue(1);      // disable_deblocking_filter_idc: off
        w.Ue(25);     // mb_type: I_PCM
        PcmSamples(w, Luma, Cb, Cr);
        w.Trailing();
        return w.ToNal(3, 5);
    }

    private static void PcmSamples(BitWriter w, byte luma, byte cb, byte cr)
    {
        w.AlignWith(0); // pcm_alignment_zero_bit
        foreach (var (value, count) in new[] { (luma, 256), (cb, 64), (cr, 64) })
        {
            for (int i = 0; i < count; i++)
            {
                w.Bits(value, 8);
            }
        }
    }

    /// <summary>A reference P picture whose slice header uses the PPS defaults and the sliding window.</summary>
    private static BitWriter PSliceHeader(int frameNum)
    {
        var w = new BitWriter();
        w.Ue(0);             // first_mb_in_slice
        w.Ue(5);             // slice_type: P (all slices of the picture)
        w.Ue(0);             // pic_parameter_set_id
        w.Bits(frameNum, 4); // frame_num
        w.Bit(0);            // num_ref_idx_active_override_flag
        w.Bit(0);            // ref_pic_list_modification_flag_l0
        w.Bit(0);            // adaptive_ref_pic_marking_mode_flag
        w.Se(0);             // slice_qp_delta
        w.Ue(1);             // disable_deblocking_filter_idc: off
        return w;
    }

    /// <summary>One P_L0_16x16 macroblock predicted from reference index <paramref name="refIdx"/>, zero motion, no residual.</summary>
    private static byte[] PFrame(int frameNum, int refIdx)
    {
        var w = PSliceHeader(frameNum);
        w.Ue(0);             // mb_skip_run
        w.Ue(0);             // mb_type: P_L0_16x16
        w.Ue((uint)refIdx);  // ref_idx_l0, te(v) with 4 references
        w.Se(0);             // mvd_l0 x
        w.Se(0);             // mvd_l0 y
        w.Ue(0);             // coded_block_pattern 0 (inter mapping)
        w.Trailing();
        return w.ToNal(2, 1);
    }

    /// <summary>A P picture whose one macroblock is I_PCM (mb_type 5 + 25) with the other samples.</summary>
    private static byte[] PPcm(int frameNum)
    {
        var w = PSliceHeader(frameNum);
        w.Ue(0);  // mb_skip_run
        w.Ue(30); // mb_type: I_PCM
        PcmSamples(w, OtherLuma, OtherCb, OtherCr);
        w.Trailing();
        return w.ToNal(2, 1);
    }

    /// <summary>A P picture whose one macroblock is skipped: a copy of reference index 0.</summary>
    private static byte[] PSkip(int frameNum)
    {
        var w = PSliceHeader(frameNum);
        w.Ue(1); // mb_skip_run
        w.Trailing();
        return w.ToNal(2, 1);
    }
}
