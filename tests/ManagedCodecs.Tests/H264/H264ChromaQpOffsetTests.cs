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
        new(new[] { "h264-high-p.mp4", "h264-high-b-cavlc.mp4" }.Where(ParameterSetRewriter.Exists));

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

    private static List<VideoFrame> Decode(string sampleName, int cbOffset, int crOffset) =>
        ParameterSetRewriter.Decode(sampleName, pps: nal => ParameterSetRewriter.Pps(nal, cbOffset, crOffset));
}
