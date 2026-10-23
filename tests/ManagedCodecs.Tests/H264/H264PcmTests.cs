using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// I_PCM, which libx264 never emits: a hand-built 32x16 picture whose first macroblock is I_PCM
/// with flat samples and whose second is Intra_16x16 DC prediction without residual, so the whole
/// frame must come out flat. With CABAC this also checks that the arithmetic decoder stops at the
/// right bit before the samples and restarts after them (9.3.1.2). In 4:2:2 (High 4:2:2 profile) the
/// macroblock carries 8x16 chroma samples a component, and DC prediction of the second one's lower
/// chroma blocks reads them too. At 10 bits (High 10, High 4:2:2) each sample takes 10 bits
/// (pcm_sample_luma and pcm_sample_chroma are BitDepth bits wide) and comes out as a 16-bit word.
/// In 4:4:4 (High 4:4:4 Predictive) chroma has 256 samples a component, and the second
/// macroblock, with no intra_chroma_pred_mode, predicts Cb and Cr with its luma mode and codes
/// their DC levels as luma's (coded_block_flag contexts 460 and 472 with CABAC).
/// </summary>
public class H264PcmTests
{
    [Theory]
    [InlineData(false, 0, 8)]
    [InlineData(true, 0, 8)]
    [InlineData(true, 0, 10)]
    [InlineData(false, 1, 8)]
    [InlineData(true, 1, 8)]
    [InlineData(false, 2, 8)]
    [InlineData(true, 2, 8)]
    [InlineData(false, 1, 10)]
    [InlineData(true, 1, 10)]
    [InlineData(true, 2, 10)]
    [InlineData(false, 3, 8)]
    [InlineData(true, 3, 8)]
    [InlineData(true, 3, 10)]
    public void PcmMacroblock_GivesItsSamplesAndDecodingResumes(bool cabac, int chromaFormat, int bitDepth)
    {
        int[] values = bitDepth == 8 ? [100, 50, 200] : [700, 300, 900];
        var decoder = new H264Decoder(AvcC(Sps(chromaFormat, bitDepth), Pps(cabac)));
        var slice = cabac ? CabacSlice(chromaFormat, bitDepth, values) : CavlcSlice(chromaFormat, bitDepth, values);
        var nal = slice.ToNal(3, 5);
        var packet = new byte[4 + nal.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(packet, nal.Length);
        nal.CopyTo(packet, 4);

        var frame = Assert.Single(decoder.Decode(new Packet(0, 0, packet, true, 0, 0, 1)));
        Assert.Equal((32, 16), (frame.Width, frame.Height));
        var expectedFormat = (chromaFormat, bitDepth) switch
        {
            (0, 8) => PixelFormat.Gray8,
            (0, _) => PixelFormat.Gray10,
            (3, 8) => PixelFormat.Yuv444P,
            (3, _) => PixelFormat.Yuv444P10,
            (2, 8) => PixelFormat.Yuv422P,
            (2, _) => PixelFormat.Yuv422P10,
            (_, 8) => PixelFormat.Yuv420P,
            _ => PixelFormat.Yuv420P10,
        };
        Assert.Equal(expectedFormat, frame.Format);
        Assert.Equal(chromaFormat == 0 ? 1 : 3, frame.Planes.Length);
        for (int plane = 0; plane < frame.Planes.Length; plane++)
        {
            int[] samples = bitDepth == 8
                ? [.. frame.Planes[plane].Select(b => (int)b)]
                : [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(frame.Planes[plane]).ToArray().Select(v => (int)v)];
            Assert.All(samples, sample => Assert.Equal(values[plane], sample));
        }
    }

    private static BitWriter Sps(int chromaFormat, int bitDepth)
    {
        var w = new BitWriter();
        w.Bits(chromaFormat switch { 3 => 244, 2 => 122, 0 when bitDepth == 8 => 100, _ => bitDepth > 8 ? 110 : 77 }, 8); // profile_idc: High 4:4:4 Predictive, High 4:2:2, High, High 10 or Main
        w.Bits(0, 8);  // constraint flags
        w.Bits(30, 8); // level_idc
        w.Ue(0);       // seq_parameter_set_id
        if (chromaFormat != 1 || bitDepth > 8)
        {
            w.Ue((uint)chromaFormat); // chroma_format_idc
            if (chromaFormat == 3)
            {
                w.Bit(0); // separate_colour_plane_flag
            }

            w.Ue((uint)bitDepth - 8); // bit_depth_luma_minus8
            w.Ue(chromaFormat == 0 ? 10u - (uint)bitDepth : (uint)bitDepth - 8); // bit_depth_chroma_minus8: unused in 4:0:0, so set apart
            w.Bit(0); // qpprime_y_zero_transform_bypass_flag
            w.Bit(0); // seq_scaling_matrix_present_flag
        }

        w.Ue(0);       // log2_max_frame_num_minus4
        w.Ue(2);       // pic_order_cnt_type
        w.Ue(0);       // max_num_ref_frames
        w.Bit(0);      // gaps_in_frame_num_value_allowed_flag
        w.Ue(1);       // pic_width_in_mbs_minus1: 2 macroblocks
        w.Ue(0);       // pic_height_in_map_units_minus1: 1
        w.Bit(1);      // frame_mbs_only_flag
        w.Bit(1);      // direct_8x8_inference_flag
        w.Bit(0);      // frame_cropping_flag
        w.Bit(0);      // vui_parameters_present_flag
        w.Trailing();
        return w;
    }

    private static BitWriter Pps(bool cabac)
    {
        var w = new BitWriter();
        w.Ue(0);            // pic_parameter_set_id
        w.Ue(0);            // seq_parameter_set_id
        w.Bit(cabac ? 1 : 0); // entropy_coding_mode_flag
        w.Bit(0);           // bottom_field_pic_order_in_frame_present_flag
        w.Ue(0);            // num_slice_groups_minus1
        w.Ue(0);            // num_ref_idx_l0_default_active_minus1
        w.Ue(0);            // num_ref_idx_l1_default_active_minus1
        w.Bit(0);           // weighted_pred_flag
        w.Bits(0, 2);       // weighted_bipred_idc
        w.Se(0);            // pic_init_qp_minus26
        w.Se(0);            // pic_init_qs_minus26
        w.Se(0);            // chroma_qp_index_offset
        w.Bit(1);           // deblocking_filter_control_present_flag
        w.Bit(0);           // constrained_intra_pred_flag
        w.Bit(0);           // redundant_pic_cnt_present_flag
        w.Trailing();
        return w;
    }

    private static BitWriter SliceHeader()
    {
        var w = new BitWriter();
        w.Ue(0);      // first_mb_in_slice
        w.Ue(7);      // slice_type: I (all slices of the picture)
        w.Ue(0);      // pic_parameter_set_id
        w.Bits(0, 4); // frame_num
        w.Ue(0);      // idr_pic_id
        w.Bit(0);     // no_output_of_prior_pics_flag
        w.Bit(0);     // long_term_reference_flag
        w.Se(0);      // slice_qp_delta: SliceQP_Y 26
        w.Ue(1);      // disable_deblocking_filter_idc: off, so the samples stay exact
        return w;
    }

    private static void PcmSamples(BitWriter w, int chromaFormat, int bitDepth, int[] values)
    {
        w.AlignWith(0); // pcm_alignment_zero_bit
        int chroma = chromaFormat switch { 0 => 0, 3 => 256, 2 => 128, _ => 64 };
        foreach (var (value, count) in new[] { (values[0], 256), (values[1], chroma), (values[2], chroma) })
        {
            for (int i = 0; i < count; i++)
            {
                w.Bits(value, bitDepth);
            }
        }
    }

    private static BitWriter CavlcSlice(int chromaFormat, int bitDepth, int[] values)
    {
        var w = SliceHeader();
        w.Ue(25); // mb_type I_PCM
        PcmSamples(w, chromaFormat, bitDepth, values);
        w.Ue(3);  // mb_type I_16x16_2_0_0: DC prediction, no coded blocks
        if (chromaFormat is 1 or 2)
        {
            w.Ue(0); // intra_chroma_pred_mode: DC
        }

        w.Se(0);  // mb_qp_delta

        // Intra16x16DCLevel coeff_token, nC 16 (left is I_PCM): TotalCoeff 0; in 4:4:4 also Cb's and Cr's.
        for (int c = 0; c < (chromaFormat == 3 ? 3 : 1); c++)
        {
            w.Bits(3, 6);
        }

        w.Trailing();
        return w;
    }

    /// <summary>The same two macroblocks with CABAC; each context as 9.3.3.1 derives it.</summary>
    private static BitWriter CabacSlice(int chromaFormat, int bitDepth, int[] values)
    {
        var w = SliceHeader();
        w.AlignWith(1); // cabac_alignment_one_bit
        var e = new CabacEncoder(table: 0, qp: 26, w);

        // Macroblock 0: mb_type bin 0 (no neighbours: ctxIdx 3), then the terminate bin 1 for I_PCM.
        e.Decision(3, 1);
        e.Terminate(1);
        PcmSamples(w, chromaFormat, bitDepth, values);
        e.InitEngine();
        e.Terminate(0); // end_of_slice_flag

        // Macroblock 1, left of it I_PCM (which is not I_NxN): mb_type bin 0 in ctxIdx 4, terminate 0,
        // no luma or chroma CBP (ctxIdx 6, 7), prediction mode 2 as bins 1, 0 (ctxIdx 9, 10).
        e.Decision(4, 1);
        e.Terminate(0);
        e.Decision(6, 0);
        e.Decision(7, 0);
        e.Decision(9, 1);
        e.Decision(10, 0);
        if (chromaFormat is 1 or 2)
        {
            e.Decision(64, 0); // intra_chroma_pred_mode 0: I_PCM and missing neighbours both count 0
        }

        e.Decision(60, 0); // mb_qp_delta 0: the previous macroblock is I_PCM
        e.Decision(85 + 3, 0); // DC coded_block_flag 0: left I_PCM counts 1, the missing top (intra) 1
        if (chromaFormat == 3)
        {
            e.Decision(460 + 3, 0); // Cb's (ctxBlockCat 6) and Cr's (10), the same way
            e.Decision(472 + 3, 0);
        }

        e.Terminate(1); // end_of_slice_flag; the flush ends with rbsp_stop_one_bit
        w.AlignWith(0);
        return w;
    }

    private static byte[] AvcC(BitWriter sps, BitWriter pps)
    {
        var spsNal = sps.ToNal(3, 7);
        var ppsNal = pps.ToNal(3, 8);
        return [1, 77, 0, 30, 0xFF, 0xE1, 0, (byte)spsNal.Length, .. spsNal, 1, 0, (byte)ppsNal.Length, .. ppsNal];
    }
}
