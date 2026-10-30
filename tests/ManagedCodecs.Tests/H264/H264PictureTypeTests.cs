using System.Buffers.Binary;
using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// <see cref="VideoFrame.PictureType"/> of a picture whose slices differ in type, on a hand-built 32x16 stream (two
/// macroblocks, one slice each): FFmpeg types the frame by its first slice (<c>h264_frame_start</c>), which
/// <c>ffprobe -show_frames</c> gives for this stream as I, I, P. No encoder used for the samples mixes slice types in a
/// picture (libx264, OpenH264 and JM give every slice of a picture one type), so the stream is built here; the
/// samples' types are checked against ffprobe's in <see cref="H264ConformanceTests"/>.
/// </summary>
public class H264PictureTypeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Frame_TakesItsFirstSlicesType(int threads)
    {
        var frames = Decode(threads, [[Idr()], [IPcm(1, firstMb: 0), PSkip(1, firstMb: 1)], [PSkip(2, firstMb: 0), IPcm(2, firstMb: 1)]]);
        Assert.Equal([PictureType.I, PictureType.I, PictureType.P], frames.Select(f => f.PictureType));
    }

    private static List<VideoFrame> Decode(int threads, List<byte[][]> pictures)
    {
        var sps = Sps().ToNal(3, 7);
        var pps = Pps().ToNal(3, 8);
        var decoder = new H264Decoder([1, 66, 0, 30, 0xFF, 0xE1, 0, (byte)sps.Length, .. sps, 1, 0, (byte)pps.Length, .. pps]) { Threads = threads };
        var frames = new List<VideoFrame>();
        for (int i = 0; i < pictures.Count; i++)
        {
            var packet = new List<byte>();
            foreach (var nal in pictures[i])
            {
                var length = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(length, nal.Length);
                packet.AddRange(length);
                packet.AddRange(nal);
            }

            frames.AddRange(decoder.Decode(new Packet(0, i, packet.ToArray(), i == 0, i, i, 1)));
        }

        frames.AddRange(decoder.Flush());
        return frames;
    }

    private static BitWriter Sps()
    {
        var w = new BitWriter();
        w.Bits(66, 8); // profile_idc: Baseline
        w.Bits(0, 8);  // constraint flags
        w.Bits(30, 8); // level_idc
        w.Ue(0);       // seq_parameter_set_id
        w.Ue(0);       // log2_max_frame_num_minus4: MaxFrameNum 16
        w.Ue(2);       // pic_order_cnt_type: output in decoding order
        w.Ue(1);       // max_num_ref_frames
        w.Bit(0);      // gaps_in_frame_num_value_allowed_flag
        w.Ue(1);       // pic_width_in_mbs_minus1: two macroblocks
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
        w.Ue(0);      // num_ref_idx_l0_default_active_minus1: one reference
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

    /// <summary>The IDR picture: frame_num 0, one I slice of two I_PCM macroblocks.</summary>
    private static byte[] Idr()
    {
        var w = new BitWriter();
        w.Ue(0);      // first_mb_in_slice
        w.Ue(2);      // slice_type: I
        w.Ue(0);      // pic_parameter_set_id
        w.Bits(0, 4); // frame_num
        w.Ue(0);      // idr_pic_id
        w.Bit(0);     // no_output_of_prior_pics_flag
        w.Bit(0);     // long_term_reference_flag
        w.Se(0);      // slice_qp_delta
        w.Ue(1);      // disable_deblocking_filter_idc: off
        w.Ue(25);     // mb_type: I_PCM
        PcmSamples(w);
        w.Ue(25);     // mb_type: I_PCM
        PcmSamples(w);
        w.Trailing();
        return w.ToNal(3, 5);
    }

    /// <summary>A non-IDR I slice of one I_PCM macroblock, at <paramref name="firstMb"/>.</summary>
    private static byte[] IPcm(int frameNum, int firstMb)
    {
        var w = new BitWriter();
        w.Ue((uint)firstMb); // first_mb_in_slice
        w.Ue(2);             // slice_type: I (this slice alone)
        w.Ue(0);             // pic_parameter_set_id
        w.Bits(frameNum, 4); // frame_num
        w.Bit(0);            // adaptive_ref_pic_marking_mode_flag
        w.Se(0);             // slice_qp_delta
        w.Ue(1);             // disable_deblocking_filter_idc: off
        w.Ue(25);            // mb_type: I_PCM
        PcmSamples(w);
        w.Trailing();
        return w.ToNal(2, 1);
    }

    /// <summary>A P slice of one skipped macroblock (a copy of the previous picture), at <paramref name="firstMb"/>.</summary>
    private static byte[] PSkip(int frameNum, int firstMb)
    {
        var w = new BitWriter();
        w.Ue((uint)firstMb); // first_mb_in_slice
        w.Ue(0);             // slice_type: P (this slice alone)
        w.Ue(0);             // pic_parameter_set_id
        w.Bits(frameNum, 4); // frame_num
        w.Bit(0);            // num_ref_idx_active_override_flag
        w.Bit(0);            // ref_pic_list_modification_flag_l0
        w.Bit(0);            // adaptive_ref_pic_marking_mode_flag
        w.Se(0);             // slice_qp_delta
        w.Ue(1);             // disable_deblocking_filter_idc: off
        w.Ue(1);             // mb_skip_run
        w.Trailing();
        return w.ToNal(2, 1);
    }

    private static void PcmSamples(BitWriter w)
    {
        w.AlignWith(0); // pcm_alignment_zero_bit
        for (int i = 0; i < 256 + 64 + 64; i++)
        {
            w.Bits(128, 8);
        }
    }
}
