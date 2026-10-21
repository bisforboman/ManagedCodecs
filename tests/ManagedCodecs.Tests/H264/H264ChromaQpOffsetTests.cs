using ManagedCodecs.Containers.Mp4;
using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// second_chroma_qp_index_offset, which libx264 always writes equal to chroma_qp_index_offset: High
/// samples decoded with their PPS rewritten. Cb must follow the first offset and Cr the second, in
/// scaling and in deblocking alike, so a stream with offsets (a, b) must give the Cb plane of (a, a)
/// and the Cr plane of (b, b), frame by frame; luma does not depend on either. Equal offsets are
/// checked against FFmpeg by the conformance tests.
/// </summary>
public class H264ChromaQpOffsetTests
{
    public static TheoryData<string> Samples() =>
        new(new[] { "h264-high-p.mp4", "h264-high-b-cavlc.mp4" }.Where(name => TestSamples.Directory is not null && File.Exists(TestSamples.PathOf(name))));

    [Theory]
    [MemberData(nameof(Samples))]
    public void CbFollowsTheFirstOffsetAndCrTheSecond(string sampleName)
    {
        const int Cb = -3;
        const int Cr = 5;
        var mixed = Decode(sampleName, Cb, Cr);
        var cbOnly = Decode(sampleName, Cb, Cb);
        var crOnly = Decode(sampleName, Cr, Cr);

        Assert.Equal(cbOnly.Count, mixed.Count);
        Assert.Equal(crOnly.Count, mixed.Count);
        for (int i = 0; i < mixed.Count; i++)
        {
            Assert.Equal(cbOnly[i].Planes[0], mixed[i].Planes[0]);
            Assert.Equal(cbOnly[i].Planes[1], mixed[i].Planes[1]);
            Assert.Equal(crOnly[i].Planes[2], mixed[i].Planes[2]);
        }

        // The offsets matter at all: the planes of different offsets differ.
        Assert.Contains(Enumerable.Range(0, mixed.Count), i => !mixed[i].Planes[2].AsSpan().SequenceEqual(cbOnly[i].Planes[2]));
    }

    private static List<VideoFrame> Decode(string sampleName, int cbOffset, int crOffset)
    {
        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;
        var config = AvcConfiguration.Parse(video.CodecPrivate.Span);
        var decoder = new H264Decoder(AvcC(video.CodecPrivate.Span, config, cbOffset, crOffset));

        var frames = new List<VideoFrame>();
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex))
        {
            frames.AddRange(decoder.Decode(packet));
        }

        frames.AddRange(decoder.Flush());
        return frames;
    }

    /// <summary>The avcC with each PPS rewritten to the given offsets (and without the High profile extension bytes).</summary>
    private static byte[] AvcC(ReadOnlySpan<byte> original, AvcConfiguration config, int cbOffset, int crOffset)
    {
        var data = new List<byte> { 1, original[1], original[2], original[3], original[4], (byte)(0xE0 | config.Sps.Count) };
        foreach (var sps in config.Sps)
        {
            data.AddRange([(byte)(sps.Length >> 8), (byte)sps.Length, .. sps]);
        }

        data.Add((byte)config.Pps.Count);
        foreach (var pps in config.Pps)
        {
            var rewritten = WithChromaOffsets(pps, cbOffset, crOffset);
            data.AddRange([(byte)(rewritten.Length >> 8), (byte)rewritten.Length, .. rewritten]);
        }

        return [.. data];
    }

    /// <summary>pic_parameter_set_rbsp (7.3.2.2) copied field by field, with the two chroma offsets replaced.</summary>
    private static byte[] WithChromaOffsets(byte[] ppsNal, int cbOffset, int crOffset)
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
        r.ReadSe();
        w.Se(cbOffset); // chroma_qp_index_offset
        w.Bits(r.ReadBits(3), 3); // deblocking_filter_control_present_flag, constrained_intra_pred_flag, redundant_pic_cnt_present_flag
        w.Bit(r.ReadFlag() ? 1 : 0); // transform_8x8_mode_flag
        Assert.False(r.ReadFlag()); // pic_scaling_matrix_present_flag
        w.Bit(0);
        r.ReadSe();
        w.Se(crOffset); // second_chroma_qp_index_offset
        w.Trailing();
        return w.ToNal(nal.RefIdc, (int)NalUnitType.Pps);
    }
}
