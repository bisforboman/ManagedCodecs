using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// I_PCM, which libx264 never emits: a hand-built 32x16 picture whose first macroblock is I_PCM
/// with flat samples and whose second is Intra_16x16 DC prediction without residual, so the whole
/// frame must come out flat. With CABAC this also checks that the arithmetic decoder stops at the
/// right bit before the samples and restarts after them (9.3.1.2).
/// </summary>
public class H264PcmTests
{
    private const byte Luma = 100;
    private const byte Cb = 50;
    private const byte Cr = 200;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PcmMacroblock_GivesItsSamplesAndDecodingResumes(bool cabac)
    {
        var decoder = new H264Decoder(AvcC(Sps(), Pps(cabac)));
        var slice = cabac ? CabacSlice() : CavlcSlice();
        var nal = Nal(3, 5, slice);
        var packet = new byte[4 + nal.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(packet, nal.Length);
        nal.CopyTo(packet, 4);

        var frame = Assert.Single(decoder.Decode(new Packet(0, 0, packet, true, 0, 0, 1)));
        Assert.Equal((32, 16), (frame.Width, frame.Height));
        Assert.All(frame.Planes[0], sample => Assert.Equal(Luma, sample));
        Assert.All(frame.Planes[1], sample => Assert.Equal(Cb, sample));
        Assert.All(frame.Planes[2], sample => Assert.Equal(Cr, sample));
    }

    private static BitWriter Sps()
    {
        var w = new BitWriter();
        w.Bits(77, 8); // profile_idc: Main
        w.Bits(0, 8);  // constraint flags
        w.Bits(30, 8); // level_idc
        w.Ue(0);       // seq_parameter_set_id
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

    private static void PcmSamples(BitWriter w)
    {
        w.AlignWith(0); // pcm_alignment_zero_bit
        foreach (var (value, count) in new[] { (Luma, 256), (Cb, 64), (Cr, 64) })
        {
            for (int i = 0; i < count; i++)
            {
                w.Bits(value, 8);
            }
        }
    }

    private static BitWriter CavlcSlice()
    {
        var w = SliceHeader();
        w.Ue(25); // mb_type I_PCM
        PcmSamples(w);
        w.Ue(3);  // mb_type I_16x16_2_0_0: DC prediction, no coded blocks
        w.Ue(0);  // intra_chroma_pred_mode: DC
        w.Se(0);  // mb_qp_delta
        w.Bits(3, 6); // Intra16x16DCLevel coeff_token, nC 16 (left is I_PCM): TotalCoeff 0
        w.Trailing();
        return w;
    }

    /// <summary>The same two macroblocks with CABAC; each context as 9.3.3.1 derives it.</summary>
    private static BitWriter CabacSlice()
    {
        var w = SliceHeader();
        w.AlignWith(1); // cabac_alignment_one_bit
        var e = new CabacEncoder(table: 0, qp: 26, w);

        // Macroblock 0: mb_type bin 0 (no neighbours: ctxIdx 3), then the terminate bin 1 for I_PCM.
        e.Decision(3, 1);
        e.Terminate(1);
        PcmSamples(w);
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
        e.Decision(64, 0); // intra_chroma_pred_mode 0: I_PCM and missing neighbours both count 0
        e.Decision(60, 0); // mb_qp_delta 0: the previous macroblock is I_PCM
        e.Decision(85 + 3, 0); // DC coded_block_flag 0: left I_PCM counts 1, the missing top (intra) 1
        e.Terminate(1); // end_of_slice_flag; the flush ends with rbsp_stop_one_bit
        w.AlignWith(0);
        return w;
    }

    private static byte[] AvcC(BitWriter sps, BitWriter pps)
    {
        var spsNal = Nal(3, 7, sps);
        var ppsNal = Nal(3, 8, pps);
        return [1, 77, 0, 30, 0xFF, 0xE1, 0, (byte)spsNal.Length, .. spsNal, 1, 0, (byte)ppsNal.Length, .. ppsNal];
    }

    /// <summary>A NAL unit with emulation prevention bytes inserted (7.4.1).</summary>
    private static byte[] Nal(int refIdc, int type, BitWriter payload)
    {
        var nal = new List<byte> { (byte)((refIdc << 5) | type) };
        int zeros = 0;
        foreach (byte b in payload.ToBytes())
        {
            if (zeros >= 2 && b <= 3)
            {
                nal.Add(3);
                zeros = 0;
            }

            nal.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return [.. nal];
    }
}
