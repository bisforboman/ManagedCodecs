using ManagedCodecs.Containers;
using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// Decodes a sample with its SPS and PPS rewritten, for parameter set features libx264 never writes:
/// the rewritten stream is decoded and compared with another rewrite that must give the same result.
/// </summary>
internal static class ParameterSetRewriter
{
    public static bool Exists(string sampleName) => TestSamples.Directory is not null && File.Exists(TestSamples.PathOf(sampleName));

    /// <summary>Every frame of the sample, in output order, decoded with each SPS and PPS NAL unit passed through the rewrites.</summary>
    public static List<VideoFrame> Decode(string sampleName, Func<byte[], byte[]>? sps = null, Func<byte[], byte[]>? pps = null)
    {
        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;
        var original = video.CodecPrivate.Span;
        var config = AvcConfiguration.Parse(original);

        // The avcC rebuilt around the rewritten NAL units, without the High profile extension bytes.
        var avcC = new List<byte> { 1, original[1], original[2], original[3], original[4], (byte)(0xE0 | config.Sps.Count) };
        foreach (var nal in config.Sps.Select(sps ?? (n => n)))
        {
            avcC.AddRange([(byte)(nal.Length >> 8), (byte)nal.Length, .. nal]);
        }

        avcC.Add((byte)config.Pps.Count);
        foreach (var nal in config.Pps.Select(pps ?? (n => n)))
        {
            avcC.AddRange([(byte)(nal.Length >> 8), (byte)nal.Length, .. nal]);
        }

        var decoder = new H264Decoder([.. avcC]);
        var frames = new List<VideoFrame>();
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex))
        {
            frames.AddRange(decoder.Decode(packet));
        }

        frames.AddRange(decoder.Flush());
        return frames;
    }

    /// <summary>The scaling lists a PPS sends (null entries absent), or null when it has none.</summary>
    public static byte[]?[]? PpsScalingLists(byte[] ppsNal) => PictureParameterSet.Parse(NalUnit.Parse(ppsNal).CreateReader()).ScalingLists;

    /// <summary>
    /// A seq_parameter_set_rbsp of a High profile SPS copied field by field, with
    /// seq_scaling_matrix_present_flag and the lists replaced (null: no matrix).
    /// </summary>
    public static byte[] Sps(byte[] spsNal, ScalingLists? scaling)
    {
        var nal = NalUnit.Parse(spsNal);
        var r = nal.CreateReader();
        var w = new BitWriter();
        int profile = (int)r.ReadBits(8);
        Assert.Equal(100, profile);
        w.Bits(profile, 8);
        w.Bits(r.ReadBits(16), 16); // constraint flags, level_idc
        w.Ue(r.ReadUe()); // seq_parameter_set_id
        uint chromaFormat = r.ReadUe();
        Assert.Equal(1u, chromaFormat);
        w.Ue(chromaFormat);
        w.Ue(r.ReadUe()); // bit_depth_luma_minus8
        w.Ue(r.ReadUe()); // bit_depth_chroma_minus8
        w.Bit(r.ReadFlag() ? 1 : 0); // qpprime_y_zero_transform_bypass_flag
        Assert.False(r.ReadFlag()); // libx264 never sends SPS lists
        Write(w, scaling);
        CopyRest(r, w);
        return w.ToNal(nal.RefIdc, (int)NalUnitType.Sps);
    }

    /// <summary>
    /// A seq_parameter_set_rbsp of a 4:0:0 SPS (High 4:4:4 Predictive) copied field by field, with
    /// bit_depth_chroma_minus8 replaced: what JM writes (8 bits whatever the luma's), which no plane uses.
    /// </summary>
    public static byte[] SpsChromaDepth(byte[] spsNal, int chromaDepthMinus8)
    {
        var nal = NalUnit.Parse(spsNal);
        var r = nal.CreateReader();
        var w = new BitWriter();
        int profile = (int)r.ReadBits(8);
        Assert.Equal(244, profile);
        w.Bits(profile, 8);
        w.Bits(r.ReadBits(16), 16); // constraint flags, level_idc
        w.Ue(r.ReadUe()); // seq_parameter_set_id
        uint chromaFormat = r.ReadUe();
        Assert.Equal(0u, chromaFormat);
        w.Ue(chromaFormat);
        w.Ue(r.ReadUe()); // bit_depth_luma_minus8
        r.ReadUe(); // bit_depth_chroma_minus8
        w.Ue((uint)chromaDepthMinus8);
        CopyRest(r, w);
        return w.ToNal(nal.RefIdc, (int)NalUnitType.Sps);
    }

    /// <summary>
    /// A pic_parameter_set_rbsp copied field by field, with the chroma QP offsets and the scaling
    /// lists replaced where given (<paramref name="replaceScaling"/>: by <paramref name="scaling"/>,
    /// null for no matrix).
    /// </summary>
    public static byte[] Pps(byte[] ppsNal, int? cbOffset = null, int? crOffset = null, bool replaceScaling = false, ScalingLists? scaling = null)
    {
        var nal = NalUnit.Parse(ppsNal);
        var r = nal.CreateReader();
        var w = new BitWriter();
        w.Ue(r.ReadUe()); // pic_parameter_set_id
        w.Ue(r.ReadUe()); // seq_parameter_set_id
        w.Bits(r.ReadBits(2), 2); // entropy_coding_mode_flag, bottom_field_pic_order_in_frame_present_flag
        Assert.Equal(0u, r.ReadUe()); // num_slice_groups_minus1
        w.Ue(0);
        w.Ue(r.ReadUe()); // num_ref_idx_l0_default_active_minus1
        w.Ue(r.ReadUe()); // num_ref_idx_l1_default_active_minus1
        w.Bits(r.ReadBits(3), 3); // weighted_pred_flag, weighted_bipred_idc
        w.Se(r.ReadSe()); // pic_init_qp_minus26
        w.Se(r.ReadSe()); // pic_init_qs_minus26
        int cb = r.ReadSe(); // chroma_qp_index_offset
        w.Se(cbOffset ?? cb);
        w.Bits(r.ReadBits(3), 3); // deblocking_filter_control_present_flag, constrained_intra_pred_flag, redundant_pic_cnt_present_flag
        bool transform8x8 = r.ReadFlag();
        w.Bit(transform8x8 ? 1 : 0);
        byte[]?[]? original = r.ReadFlag() ? ScalingMatrix.ParseLists(r, transform8x8 ? 8 : 6) : null;
        if (replaceScaling)
        {
            Write(w, scaling);
        }
        else
        {
            Write(w, original is null ? null : new ScalingLists(original));
        }

        int cr = r.ReadSe(); // second_chroma_qp_index_offset
        w.Se(crOffset ?? cr);
        w.Trailing();
        return w.ToNal(nal.RefIdc, (int)NalUnitType.Pps);
    }

    /// <summary>The scaling_matrix_present_flag and, with one, a present flag and scaling_list() for each list.</summary>
    private static void Write(BitWriter w, ScalingLists? scaling)
    {
        w.Bit(scaling is null ? 0 : 1);
        foreach (var list in scaling?.Lists ?? [])
        {
            w.Bit(list is null ? 0 : 1);
            if (list is null)
            {
                continue;
            }

            if (list.Length == 0)
            {
                w.Se(-8); // nextScale 0 at once: useDefaultScalingMatrixFlag
                continue;
            }

            int last = 8;
            foreach (byte value in list)
            {
                int delta = value - last;
                w.Se(delta > 127 ? delta - 256 : delta < -128 ? delta + 256 : delta);
                last = value;
            }
        }
    }

    private static void CopyRest(BitReader r, BitWriter w)
    {
        while (r.MoreRbspData())
        {
            w.Bit((int)r.ReadBits(1));
        }

        w.Trailing();
    }
}

/// <summary>
/// Scaling lists to write, in zig-zag order: a null entry is absent, an empty one sends
/// useDefaultScalingMatrixFlag.
/// </summary>
internal sealed record ScalingLists(byte[]?[] Lists);
